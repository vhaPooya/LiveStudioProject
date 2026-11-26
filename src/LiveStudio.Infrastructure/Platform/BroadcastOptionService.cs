using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class BroadcastOptionService(IDbContextFactory<LiveStudioDbContext> factory) : IBroadcastOptionService
{
    public async Task SetAsync(Guid userId, string broadcastId, bool keepArchive, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.BroadcastOptions.FirstOrDefaultAsync(x => x.UserId == userId && x.BroadcastId == broadcastId, ct);
        if (row is null) db.BroadcastOptions.Add(new BroadcastOption { UserId = userId, BroadcastId = broadcastId, KeepArchive = keepArchive });
        else if (!row.Finalized) row.KeepArchive = keepArchive;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool?> GetKeepArchiveAsync(Guid userId, string broadcastId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.BroadcastOptions.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.BroadcastId == broadcastId, ct);
        return row?.KeepArchive;
    }

    public async Task<bool> IsFinalizedAsync(Guid userId, string broadcastId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.BroadcastOptions.AsNoTracking().AnyAsync(x => x.UserId == userId && x.BroadcastId == broadcastId && x.Finalized, ct);
    }

    public async Task MarkFinalizedAsync(Guid userId, string broadcastId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.BroadcastOptions.FirstOrDefaultAsync(x => x.UserId == userId && x.BroadcastId == broadcastId, ct);
        if (row is null) return;
        row.Finalized = true;
        await db.SaveChangesAsync(ct);
    }
}
