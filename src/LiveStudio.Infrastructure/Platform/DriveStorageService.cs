using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LiveStudio.Application.Platform;
using LiveStudio.Infrastructure.Persistence;
using LiveStudio.Infrastructure.YouTube;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class DriveStorageService(IDbContextFactory<LiveStudioDbContext> factory, GoogleTokenBroker broker, IHttpClientFactory httpFactory) : IDriveStorageService
{
    private const long MaxVideoBytes = 1024L * 1024 * 1024;

    public async Task<DriveStatus> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var tokens = await broker.LoadTokensAsync(userId, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var link = await db.DriveLinks.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return new(tokens is not null, link is { Enabled: true } && tokens is not null, link?.FolderName, link?.Email);
    }

    public async Task<OperationResult> DisableAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var link = await db.DriveLinks.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (link is not null) { link.Enabled = false; await db.SaveChangesAsync(ct); }
        return OperationResult.Ok();
    }

    private async Task<(string Token, string FolderId)> ContextAsync(Guid userId, bool requireEnabled, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var link = await db.DriveLinks.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (link is null || string.IsNullOrEmpty(link.FolderId) || (requireEnabled && !link.Enabled))
            throw new InvalidOperationException("Google Drive متصل نیست. از تنظیمات › ذخیره‌سازی آن را متصل کنید.");
        return (await broker.GetValidAccessTokenAsync(userId, ct), link.FolderId!);
    }

    public async Task<OperationResult<string>> CreateUploadSessionAsync(Guid userId, string name, string mimeType, long size, string origin, CancellationToken ct = default)
    {
        try
        {
            if (size <= 0 || size > MaxVideoBytes) return OperationResult<string>.Fail("حداکثر حجم ویدئو ۱ گیگابایت است.");
            if (!mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return OperationResult<string>.Fail("فقط فایل ویدئویی در Drive ذخیره می‌شود.");
            var (token, folder) = await ContextAsync(userId, true, ct);
            var http = httpFactory.CreateClient("GoogleOAuth");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id")
            {
                Content = JsonContent.Create(new { name = Path.GetFileName(name), parents = new[] { folder }, mimeType })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mimeType);
            req.Headers.TryAddWithoutValidation("X-Upload-Content-Length", size.ToString());
            if (!string.IsNullOrEmpty(origin)) req.Headers.TryAddWithoutValidation("Origin", origin);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode || res.Headers.Location is null)
                return OperationResult<string>.Fail($"Google Drive درخواست آپلود را نپذیرفت ({(int)res.StatusCode}).");
            return OperationResult<string>.Ok(res.Headers.Location.ToString());
        }
        catch (InvalidOperationException ex) { return OperationResult<string>.Fail(ex.Message); }
    }

    public async Task<OperationResult<DriveFileCheck>> VerifyFileAsync(Guid userId, string fileId, CancellationToken ct)
    {
        try
        {
            var (token, folder) = await ContextAsync(userId, false, ct);
            var http = httpFactory.CreateClient("GoogleOAuth");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields=id,mimeType,size,parents,trashed");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return OperationResult<DriveFileCheck>.Fail("فایل در Google Drive یافت نشد.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var mime = root.GetProperty("mimeType").GetString() ?? "";
            if (!mime.StartsWith("video/")) return OperationResult<DriveFileCheck>.Fail("فایل ویدئویی نیست.");
            if (root.TryGetProperty("trashed", out var t) && t.GetBoolean()) return OperationResult<DriveFileCheck>.Fail("فایل در سطل زباله است.");
            var inFolder = root.TryGetProperty("parents", out var ps) && ps.EnumerateArray().Any(p => p.GetString() == folder);
            if (!inFolder) return OperationResult<DriveFileCheck>.Fail("فایل در پوشه‌ی LiveStudio نیست.");
            long size = root.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sz) ? sz : 0;
            return OperationResult<DriveFileCheck>.Ok(new(mime, size));
        }
        catch (InvalidOperationException ex) { return OperationResult<DriveFileCheck>.Fail(ex.Message); }
    }

    public async Task<HttpResponseMessage> OpenFileAsync(Guid userId, string driveFileId, string? range, CancellationToken ct = default)
    {
        var (token, _) = await ContextAsync(userId, false, ct);
        var http = httpFactory.CreateClient("GoogleOAuth");
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(driveFileId)}?alt=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(range) && System.Text.RegularExpressions.Regex.IsMatch(range, @"^bytes=\d*-\d*$")) req.Headers.TryAddWithoutValidation("Range", range);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task DeleteFileAsync(Guid userId, string driveFileId, CancellationToken ct = default)
    {
        var (token, _) = await ContextAsync(userId, false, ct);
        var http = httpFactory.CreateClient("GoogleOAuth");
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(driveFileId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
    }
}
