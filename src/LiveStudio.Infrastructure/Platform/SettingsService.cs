using System.Security.Cryptography;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LiveStudio.Infrastructure.Platform;

public sealed class SettingsService(
    IDbContextFactory<LiveStudioDbContext> factory,
    IDataProtectionProvider protection,
    IMemoryCache cache) : ISettingsService
{
    private const string CacheKey = "platform:settings";
    private readonly IDataProtector _protector = protection.CreateProtector("LiveStudio.Settings.v1");

    private async Task<Dictionary<string, SystemSetting>> LoadAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out Dictionary<string, SystemSetting>? cached) && cached is not null) return cached;
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Settings.AsNoTracking().ToListAsync(ct);
        var map = rows.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        cache.Set(CacheKey, map, TimeSpan.FromSeconds(20));
        return map;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var map = await LoadAsync(ct);
        if (!map.TryGetValue(key, out var row)) return null;
        if (!row.IsSecret) return row.Value;
        try { return string.IsNullOrEmpty(row.Value) ? null : _protector.Unprotect(row.Value); }
        catch (CryptographicException) { return null; }
    }

    public async Task<string> GetAsync(string key, string defaultValue, CancellationToken ct = default)
    {
        var value = await GetAsync(key, ct);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default)
    {
        var value = await GetAsync(key, ct);
        return value is null ? defaultValue : value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default)
    {
        var value = await GetAsync(key, ct);
        return int.TryParse(value, out var number) ? number : defaultValue;
    }

    public async Task<bool> HasValueAsync(string key, CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetAsync(key, ct));

    public async Task SetAsync(string key, string? value, bool isSecret = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct);
        var stored = value ?? string.Empty;
        if (isSecret && stored.Length > 0) stored = _protector.Protect(stored);
        if (row is null) db.Settings.Add(new SystemSetting { Key = key, Value = stored, IsSecret = isSecret, UpdatedAt = DateTime.UtcNow });
        else { row.Value = stored; row.IsSecret = isSecret; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }
}
