using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using LiveStudio.Infrastructure.YouTube;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LiveStudio.Infrastructure.Platform;

/// <summary>Light Media Box files (images, SVG, short audio) on the server disk. Videos are never stored here.</summary>
public sealed class MediaStorageService(IDbContextFactory<LiveStudioDbContext> factory, IConfiguration config, IDriveStorageService drive) : IMediaStorageService
{
    private const long QuotaBytes = 500L * 1024 * 1024;
    private const long MaxImage = 15L * 1024 * 1024;
    private const long MaxAudio = 20L * 1024 * 1024;
    private const int MaxFiles = 300;
    private static readonly HashSet<string> Categories = ["overlay", "logo", "image-bg", "video-bg", "stinger", "audio"];

    private string Root
    {
        get { var p = config["Media:RootPath"]; if (string.IsNullOrWhiteSpace(p)) p = Path.Combine(AppContext.BaseDirectory, "media"); Directory.CreateDirectory(p); return p; }
    }

    private static MediaAssetRow Map(MediaAssetRecord x) => new(x.Id, x.Name, x.MimeType, x.Category, x.Size, x.Storage, x.CreatedAt);

    public string GetPath(Guid userId, string storedName)
    {
        var safe = Path.GetFileName(storedName);
        return Path.Combine(Root, userId.ToString("N"), safe);
    }

    public async Task<IReadOnlyList<MediaAssetRow>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.MediaAssets.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<MediaUsage> UsageAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var used = await db.MediaAssets.Where(x => x.UserId == userId && x.Storage == "server").SumAsync(x => (long?)x.Size, ct) ?? 0;
        return new(used, QuotaBytes, MaxImage);
    }

    /// <summary>Detects the real type from the first bytes; the client-declared MIME is never trusted.</summary>
    private static (string Mime, string Ext, bool Video, bool Audio)? Sniff(byte[] h, int n)
    {
        static bool Starts(byte[] b, int len, params byte[] sig) => len >= sig.Length && b.Take(sig.Length).SequenceEqual(sig);
        if (Starts(h, n, 0x89, 0x50, 0x4E, 0x47)) return ("image/png", ".png", false, false);
        if (Starts(h, n, 0xFF, 0xD8, 0xFF)) return ("image/jpeg", ".jpg", false, false);
        if (Starts(h, n, 0x47, 0x49, 0x46, 0x38)) return ("image/gif", ".gif", false, false);
        if (n >= 12 && Starts(h, n, 0x52, 0x49, 0x46, 0x46) && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return ("image/webp", ".webp", false, false);
        if (n >= 12 && Starts(h, n, 0x52, 0x49, 0x46, 0x46) && h[8] == 0x57 && h[9] == 0x41 && h[10] == 0x56 && h[11] == 0x45) return ("audio/wav", ".wav", false, true);
        if (Starts(h, n, 0x4F, 0x67, 0x67, 0x53)) return ("audio/ogg", ".ogg", false, true);
        if (Starts(h, n, 0x49, 0x44, 0x33) || (n > 1 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)) return ("audio/mpeg", ".mp3", false, true);
        if (n >= 8 && h[4] == 0x66 && h[5] == 0x74 && h[6] == 0x79 && h[7] == 0x70) return ("video/mp4", ".mp4", true, false);
        if (Starts(h, n, 0x1A, 0x45, 0xDF, 0xA3)) return ("video/webm", ".webm", true, false);
        var text = System.Text.Encoding.UTF8.GetString(h, 0, n).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return ("image/svg+xml", ".svg", false, false);
        return null;
    }

    private static bool SvgUnsafe(string svg)
    {
        var s = svg.ToLowerInvariant();
        return s.Contains("<script") || s.Contains("javascript:") || s.Contains("<foreignobject") || s.Contains("<iframe")
            || s.Contains("<!entity") || s.Contains("<!doctype") || System.Text.RegularExpressions.Regex.IsMatch(s, @"\son[a-z]+\s*=")
            || System.Text.RegularExpressions.Regex.IsMatch(s, @"(href|src)\s*=\s*[""']\s*(https?:|//|data:text)");
    }

    public async Task<OperationResult<MediaAssetRow>> AddFileAsync(Guid userId, string fileName, string category, Stream content, long length, CancellationToken ct = default)
    {
        if (!Categories.Contains(category)) return OperationResult<MediaAssetRow>.Fail("دسته‌بندی معتبر نیست.");
        if (length <= 0) return OperationResult<MediaAssetRow>.Fail("فایل خالی است.");
        if (length > MaxAudio) return OperationResult<MediaAssetRow>.Fail("حجم فایل برای ذخیره روی سرور زیاد است. ویدئوها را از طریق Google Drive ذخیره کنید.");

        var head = new byte[2048];
        var n = await content.ReadAsync(head.AsMemory(0, head.Length), ct);
        var kind = Sniff(head, n);
        if (kind is null) return OperationResult<MediaAssetRow>.Fail("فرمت فایل پشتیبانی نمی‌شود (PNG، JPG، GIF، WebP، SVG، MP3، WAV، OGG).");
        if (kind.Value.Video) return OperationResult<MediaAssetRow>.Fail("ویدئو روی سرور ذخیره نمی‌شود؛ Google Drive را در تنظیمات متصل کنید.");
        if (!kind.Value.Audio && length > MaxImage) return OperationResult<MediaAssetRow>.Fail("حداکثر حجم تصویر ۱۵ مگابایت است.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.MediaAssets.CountAsync(x => x.UserId == userId, ct) >= MaxFiles) return OperationResult<MediaAssetRow>.Fail("تعداد فایل‌ها به سقف رسیده است.");
        var used = await db.MediaAssets.Where(x => x.UserId == userId && x.Storage == "server").SumAsync(x => (long?)x.Size, ct) ?? 0;
        if (used + length > QuotaBytes) return OperationResult<MediaAssetRow>.Fail("فضای ذخیره‌سازی سرور پر است.");

        var id = Guid.NewGuid();
        var stored = id.ToString("N") + kind.Value.Ext;
        var dir = Path.GetDirectoryName(GetPath(userId, stored))!;
        Directory.CreateDirectory(dir);
        var path = GetPath(userId, stored);
        long written = 0;
        try
        {
            await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(head.AsMemory(0, n), ct); written = n;
                var buf = new byte[81920]; int r;
                while ((r = await content.ReadAsync(buf, ct)) > 0)
                {
                    written += r;
                    if (written > length + 1024 || written > MaxAudio) throw new InvalidOperationException("حجم فایل بیش از مقدار اعلام‌شده است.");
                    await fs.WriteAsync(buf.AsMemory(0, r), ct);
                }
            }
            if (kind.Value.Mime == "image/svg+xml")
            {
                if (written > 1024 * 1024) throw new InvalidOperationException("حداکثر حجم SVG یک مگابایت است.");
                if (SvgUnsafe(await File.ReadAllTextAsync(path, ct))) throw new InvalidOperationException("فایل SVG حاوی محتوای ناامن است.");
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(path); } catch { /* ignore */ }
            return OperationResult<MediaAssetRow>.Fail(ex is InvalidOperationException ? ex.Message : "ذخیره فایل ناموفق بود.");
        }

        var clean = Path.GetFileName((fileName ?? "file").Replace('\\', '/')).Trim();
        if (clean.Length == 0) clean = "file";
        var rec = new MediaAssetRecord { Id = id, UserId = userId, Name = clean[..Math.Min(clean.Length, 150)], MimeType = kind.Value.Mime, Category = category, Size = written, Storage = "server", StoredName = stored };
        db.MediaAssets.Add(rec);
        await db.SaveChangesAsync(ct);
        return OperationResult<MediaAssetRow>.Ok(Map(rec));
    }

    public async Task<OperationResult<MediaAssetRow>> RegisterDriveFileAsync(Guid userId, string driveFileId, string name, string category, CancellationToken ct = default)
    {
        if (!Categories.Contains(category)) return OperationResult<MediaAssetRow>.Fail("دسته‌بندی معتبر نیست.");
        if (string.IsNullOrWhiteSpace(driveFileId) || driveFileId.Length > 100 || !driveFileId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return OperationResult<MediaAssetRow>.Fail("شناسه فایل معتبر نیست.");
        var info = await drive.VerifyFileAsync(userId, driveFileId, ct);
        if (!info.Success) return OperationResult<MediaAssetRow>.Fail(info.Error ?? "فایل در Drive یافت نشد.");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.MediaAssets.AnyAsync(x => x.DriveFileId == driveFileId, ct)) return OperationResult<MediaAssetRow>.Fail("این فایل قبلاً ثبت شده است.");
        var clean = Path.GetFileName((name ?? "video").Replace('\\', '/')).Trim();
        if (clean.Length == 0) clean = "video";
        var rec = new MediaAssetRecord { UserId = userId, Name = clean[..Math.Min(clean.Length, 150)], MimeType = info.Value!.MimeType, Category = category, Size = info.Value.Size, Storage = "drive", DriveFileId = driveFileId };
        db.MediaAssets.Add(rec);
        await db.SaveChangesAsync(ct);
        return OperationResult<MediaAssetRow>.Ok(Map(rec));
    }

    public async Task<MediaFileInfo?> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var x = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        return x is null ? null : new(x.Id, x.Name, x.MimeType, x.Storage, x.StoredName, x.DriveFileId);
    }

    public async Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var x = await db.MediaAssets.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (x is null) return OperationResult.Ok();
        if (x.Storage == "server" && x.StoredName is not null) { try { File.Delete(GetPath(userId, x.StoredName)); } catch { /* ignore */ } }
        else if (x.Storage == "drive" && x.DriveFileId is not null) { try { await drive.DeleteFileAsync(userId, x.DriveFileId, ct); } catch { /* file may already be gone */ } }
        db.MediaAssets.Remove(x);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }
}
