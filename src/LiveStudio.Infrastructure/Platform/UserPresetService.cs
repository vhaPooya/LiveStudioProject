using System.Text.Json;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class UserPresetService(IDbContextFactory<LiveStudioDbContext> factory) : IUserPresetService
{
    private const int MaxPerKind = 40;

    public async Task<IReadOnlyList<PresetRow>> ListAsync(Guid userId, string kind, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.UserPresets.AsNoTracking().Where(x => x.UserId == userId && x.Kind == kind).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return rows.Select(x => new PresetRow(x.Id, x.Name, x.Json)).ToList();
    }

    public async Task<OperationResult> SaveAsync(Guid userId, string kind, string name, string json, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 2 or > 60) return OperationResult.Fail("نام پریست باید بین ۲ تا ۶۰ نویسه باشد.");
        if (kind != "anim") return OperationResult.Fail("نوع پریست معتبر نیست.");
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4000) return OperationResult.Fail("داده‌ی پریست معتبر نیست.");
        try { using var _ = JsonDocument.Parse(json); } catch (JsonException) { return OperationResult.Fail("داده‌ی پریست معتبر نیست."); }

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.UserPresets.FirstOrDefaultAsync(x => x.UserId == userId && x.Kind == kind && x.Name == name, ct);
        if (existing is not null) { existing.Json = json; await db.SaveChangesAsync(ct); return OperationResult.Ok(); }
        if (await db.UserPresets.CountAsync(x => x.UserId == userId && x.Kind == kind, ct) >= MaxPerKind)
            return OperationResult.Fail($"حداکثر {MaxPerKind} پریست مجاز است.");
        db.UserPresets.Add(new UserPreset { UserId = userId, Kind = kind, Name = name, Json = json });
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.UserPresets.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("پریست پیدا نشد.");
        db.UserPresets.Remove(row);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }
}
