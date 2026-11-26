using System.Net;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class StreamDestinationService(IDbContextFactory<LiveStudioDbContext> factory, IDataProtectionProvider protection) : IStreamDestinationService
{
    private const int MaxPerUser = 6;
    private IDataProtector Protector => protection.CreateProtector("LiveStudio.StreamDestination.v1");

    public async Task<IReadOnlyList<DestinationRow>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.StreamDestinations.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(x => new DestinationRow(x.Id, x.Name, x.Platform, HostOf(x.RtmpUrl), x.IsEnabled)).ToList();
    }

    public async Task<OperationResult> AddAsync(Guid userId, string name, string platform, string rtmpUrl, string streamKey, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim(); rtmpUrl = (rtmpUrl ?? string.Empty).Trim().TrimEnd('/'); streamKey = (streamKey ?? string.Empty).Trim();
        if (name.Length is < 2 or > 60) return OperationResult.Fail("نام مقصد باید بین ۲ تا ۶۰ نویسه باشد.");
        if (streamKey.Length is < 3 or > 400) return OperationResult.Fail("Stream Key معتبر نیست.");
        var error = ValidateUrl(rtmpUrl);
        if (error is not null) return OperationResult.Fail(error);
        platform = platform is "twitch" or "facebook" or "aparat" or "instagram" or "kick" ? platform : "custom";

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.StreamDestinations.CountAsync(x => x.UserId == userId, ct) >= MaxPerUser)
            return OperationResult.Fail($"حداکثر {MaxPerUser} مقصد اضافه مجاز است.");
        db.StreamDestinations.Add(new StreamDestination
        {
            UserId = userId, Name = name, Platform = platform, RtmpUrl = rtmpUrl, ProtectedStreamKey = Protector.Protect(streamKey)
        });
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetEnabledAsync(Guid userId, Guid id, bool enabled, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.StreamDestinations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("مقصد پیدا نشد.");
        row.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.StreamDestinations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("مقصد پیدا نشد.");
        db.StreamDestinations.Remove(row);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<IReadOnlyList<DestinationTarget>> GetEnabledTargetsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.StreamDestinations.AsNoTracking().Where(x => x.UserId == userId && x.IsEnabled).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var result = new List<DestinationTarget>();
        foreach (var row in rows)
        {
            try { result.Add(new DestinationTarget(row.Id, row.Name, $"{row.RtmpUrl}/{Protector.Unprotect(row.ProtectedStreamKey)}")); }
            catch (System.Security.Cryptography.CryptographicException) { /* key ring changed; skip this target */ }
        }
        return result;
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : string.Empty;

    /// <summary>Only public rtmp/rtmps hosts are accepted so a tenant cannot point Egress at the internal network.</summary>
    private static string? ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "rtmp" && uri.Scheme != "rtmps"))
            return "نشانی باید با rtmp:// یا rtmps:// شروع شود.";
        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal) || !host.Contains('.') && !host.Contains(':'))
            return "نشانی مقصد باید یک سرور عمومی باشد.";
        if (IPAddress.TryParse(host, out var ip) && IsPrivate(ip))
            return "نشانی مقصد باید یک سرور عمومی باشد.";
        return null;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}
