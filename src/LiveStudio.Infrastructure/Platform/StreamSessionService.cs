using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class StreamSessionService(IDbContextFactory<LiveStudioDbContext> factory) : IStreamSessionService
{
    private static SessionRow Map(StreamSession s) => new(s.Id, s.BroadcastId, s.Title, s.StartedAt, s.EndedAt, s.PeakViewers, s.LastViews, s.LastLikes, s.CommentCount, s.Destinations);

    private static Task<StreamSession?> OpenAsync(LiveStudioDbContext db, Guid userId, string broadcastId, CancellationToken ct)
        => db.StreamSessions.Where(x => x.UserId == userId && x.BroadcastId == broadcastId && x.EndedAt == null).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);

    public async Task StartAsync(Guid userId, string broadcastId, string title, string destinations, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var open = await OpenAsync(db, userId, broadcastId, ct);
        if (open is not null) { open.Destinations = Trim(destinations, 400); await db.SaveChangesAsync(ct); return; }
        db.StreamSessions.Add(new StreamSession { UserId = userId, BroadcastId = Trim(broadcastId, 128), Title = Trim(title, 200), Destinations = Trim(destinations, 400) });
        await db.SaveChangesAsync(ct);
    }

    public async Task<SessionRow?> RecordStatsAsync(Guid userId, string broadcastId, int concurrentViewers, long views, long likes, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await OpenAsync(db, userId, broadcastId, ct);
        if (s is null) return null;
        s.PeakViewers = Math.Max(s.PeakViewers, Math.Max(0, concurrentViewers));
        s.LastViews = Math.Max(s.LastViews, views); s.LastLikes = Math.Max(s.LastLikes, likes);
        await db.SaveChangesAsync(ct);
        return Map(s);
    }

    public async Task AddCommentsAsync(Guid userId, string broadcastId, int count, CancellationToken ct = default)
    {
        if (count <= 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await OpenAsync(db, userId, broadcastId, ct);
        if (s is null) return;
        s.CommentCount += count;
        await db.SaveChangesAsync(ct);
    }

    public async Task<SessionRow?> EndAsync(Guid userId, string broadcastId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await OpenAsync(db, userId, broadcastId, ct);
        if (s is null) return null;
        s.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Map(s);
    }

    public async Task<SessionRow?> GetLatestAsync(Guid userId, string broadcastId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.StreamSessions.AsNoTracking().Where(x => x.UserId == userId && x.BroadcastId == broadcastId).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        return s is null ? null : Map(s);
    }

    public async Task<IReadOnlyList<SessionRow>> ListAsync(Guid userId, int take = 50, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.StreamSessions.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.StartedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private static string Trim(string? v, int max) { v ??= string.Empty; return v.Length > max ? v[..max] : v; }
}
