using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class AdminStatsService(IDbContextFactory<LiveStudioDbContext> factory) : IAdminStatsService
{
    public async Task<AdminStats> GetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var today = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var from14 = today.AddDays(-13);

        var totalUsers = await db.Users.CountAsync(ct);
        var newUsers = await db.Users.CountAsync(x => x.CreatedAt >= today.AddDays(-6), ct);
        var activeUserIds = await db.Subscriptions.AsNoTracking().Where(x => x.Status == SubscriptionStatus.Active && x.EndsAt > now).Select(x => x.UserId).ToListAsync(ct);
        var active = activeUserIds.Distinct().Count();
        var soon = now.AddDays(7);
        var expiring = (await db.Subscriptions.AsNoTracking().Where(x => x.Status == SubscriptionStatus.Active && x.EndsAt > now && x.EndsAt <= soon).Select(x => x.UserId).ToListAsync(ct)).Distinct().Count();

        var revenueMonth = await db.Payments.Where(x => x.Status == PaymentStatus.Paid && x.PaidAt != null && x.PaidAt >= monthStart).SumAsync(x => x.Amount, ct);
        var revenueTotal = await db.Payments.Where(x => x.Status == PaymentStatus.Paid).SumAsync(x => x.Amount, ct);
        var openTickets = await db.Tickets.CountAsync(x => x.Status == TicketStatus.Open || x.Status == TicketStatus.CustomerReply, ct);
        var openAlerts = await db.Alerts.CountAsync(x => !x.IsResolved, ct);
        var connected = await db.TenantOAuth.CountAsync(x => x.State == OAuthConnectionState.Connected, ct);

        var signupDates = await db.Users.AsNoTracking().Where(x => x.CreatedAt >= from14).Select(x => x.CreatedAt).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().Where(x => x.Status == PaymentStatus.Paid && x.PaidAt != null && x.PaidAt >= from14).ToListAsync(ct);

        var signups = Enumerable.Range(0, 14).Select(i => from14.AddDays(i))
            .Select(d => new DailyPoint(d, signupDates.Count(x => x.Date == d))).ToList();
        var revenue = Enumerable.Range(0, 14).Select(i => from14.AddDays(i))
            .Select(d => new DailyPoint(d, payments.Where(x => x.PaidAt!.Value.Date == d).Sum(x => x.Amount))).ToList();

        return new AdminStats(totalUsers, newUsers, active, expiring, revenueMonth, revenueTotal, openTickets, openAlerts, connected, signups, revenue);
    }
}
