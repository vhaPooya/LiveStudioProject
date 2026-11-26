using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.Platform;

public sealed class ActivityLogService(IDbContextFactory<LiveStudioDbContext> factory, ILogger<ActivityLogService> logger) : IActivityLogService
{
    public async Task LogAsync(ActorInfo actor, string action, string? details = null, string? userAgent = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.ActivityLogs.Add(new ActivityLog
            {
                At = DateTime.UtcNow, ActorType = actor.Type, ActorId = Trim(actor.Id, 64), ActorName = Trim(actor.Name, 120),
                Action = Trim(action, 120), Details = details is null ? null : Trim(details, 2000), Ip = actor.Ip is null ? null : Trim(actor.Ip, 64),
                UserAgent = userAgent is null ? null : Trim(userAgent, 300)
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditing must never break the business operation.
            logger.LogWarning("Activity log write failed: {Message}", ex.Message);
        }
    }

    public async Task<PagedResult<ActivityLog>> SearchAsync(string? query, ActorType? actorType, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 200);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.ActivityLogs.AsNoTracking().AsQueryable();
        if (actorType is not null) q = q.Where(x => x.ActorType == actorType.Value);
        if (fromUtc is not null) q = q.Where(x => x.At >= fromUtc.Value);
        if (toUtc is not null) q = q.Where(x => x.At < toUtc.Value);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            q = q.Where(x => x.Action.Contains(term) || (x.Details != null && x.Details.Contains(term)) || x.ActorName.Contains(term)
                             || x.ActorId.Contains(term) || (x.Ip != null && x.Ip.Contains(term)));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.At).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ActivityLog>(items, total, page, pageSize);
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
