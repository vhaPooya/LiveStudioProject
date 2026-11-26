using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class RundownService(IDbContextFactory<LiveStudioDbContext> factory) : IRundownService
{
    private const int MaxItems = 60;

    private static RundownRow Map(RundownItem x) => new(x.Id, x.Order, x.AssetId, x.Title, x.PlannedAt, x.AutoPlay, x.Played);

    public async Task<IReadOnlyList<RundownRow>> ListAsync(Guid userId, string broadcastId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.RundownItems.AsNoTracking().Where(x => x.UserId == userId && x.BroadcastId == broadcastId).OrderBy(x => x.Order).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<OperationResult> AddAsync(Guid userId, string broadcastId, string assetId, string title, DateTime? plannedAtUtc, bool autoPlay, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > 128) return OperationResult.Fail("شناسه پخش معتبر نیست.");
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 64) return OperationResult.Fail("یک ویدئو انتخاب کنید.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var count = await db.RundownItems.CountAsync(x => x.UserId == userId && x.BroadcastId == broadcastId, ct);
        if (count >= MaxItems) return OperationResult.Fail($"حداکثر {MaxItems} آیتم در برنامه مجاز است.");
        var max = count == 0 ? 0 : await db.RundownItems.Where(x => x.UserId == userId && x.BroadcastId == broadcastId).MaxAsync(x => x.Order, ct);
        db.RundownItems.Add(new RundownItem
        {
            UserId = userId, BroadcastId = broadcastId, AssetId = assetId, Order = max + 1,
            Title = (title ?? string.Empty).Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, 120)] : "ویدئو",
            PlannedAt = plannedAtUtc, AutoPlay = autoPlay && plannedAtUtc.HasValue
        });
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateAsync(Guid userId, Guid id, DateTime? plannedAtUtc, bool autoPlay, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.RundownItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("آیتم پیدا نشد.");
        row.PlannedAt = plannedAtUtc; row.AutoPlay = autoPlay && plannedAtUtc.HasValue; row.Played = false;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> MarkPlayedAsync(Guid userId, Guid id, bool played, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.RundownItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("آیتم پیدا نشد.");
        row.Played = played;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.RundownItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("آیتم پیدا نشد.");
        db.RundownItems.Remove(row);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> MoveAsync(Guid userId, Guid id, int direction, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.RundownItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (row is null) return OperationResult.Fail("آیتم پیدا نشد.");
        var list = await db.RundownItems.Where(x => x.UserId == userId && x.BroadcastId == row.BroadcastId).OrderBy(x => x.Order).ToListAsync(ct);
        var i = list.FindIndex(x => x.Id == id); var j = i + (direction < 0 ? -1 : 1);
        if (j < 0 || j >= list.Count) return OperationResult.Ok();
        (list[i].Order, list[j].Order) = (list[j].Order, list[i].Order);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }
}
