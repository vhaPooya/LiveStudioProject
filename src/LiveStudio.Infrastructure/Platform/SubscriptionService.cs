using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class SubscriptionService(IDbContextFactory<LiveStudioDbContext> factory, IActivityLogService log) : ISubscriptionService
{
    public async Task<SubscriptionInfo> GetCurrentAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var latest = await db.Subscriptions.AsNoTracking()
            .Where(x => x.UserId == userId && x.Status == SubscriptionStatus.Active)
            .OrderByDescending(x => x.EndsAt).FirstOrDefaultAsync(ct);
        if (latest is null) return SubscriptionInfo.None;

        var now = DateTime.UtcNow;
        var active = latest.EndsAt > now;
        var daysLeft = active ? Math.Max(1, (int)Math.Ceiling((latest.EndsAt - now).TotalDays)) : 0;
        var total = Math.Max(daysLeft, (int)Math.Ceiling((latest.EndsAt - latest.StartsAt).TotalDays));
        return new SubscriptionInfo(active, latest.PlanName, latest.StartsAt, latest.EndsAt, daysLeft, total, latest.Source);
    }

    public async Task<bool> HasActiveAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await db.Subscriptions.AsNoTracking().AnyAsync(x => x.UserId == userId && x.Status == SubscriptionStatus.Active && x.EndsAt > now, ct);
    }

    public async Task<IReadOnlyList<UserSubscription>> HistoryAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Subscriptions.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(bool onlyActive, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Plans.AsNoTracking().AsQueryable();
        if (onlyActive) q = q.Where(x => x.IsActive && !x.IsTrial);
        return await q.OrderBy(x => x.SortOrder).ThenBy(x => x.Price).ToListAsync(ct);
    }

    public async Task<SubscriptionPlan?> GetPlanAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Plans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<OperationResult> SavePlanAsync(SubscriptionPlan plan, ActorInfo actor, CancellationToken ct = default)
    {
        plan.Name = (plan.Name ?? string.Empty).Trim();
        plan.Slug = (plan.Slug ?? string.Empty).Trim().ToLowerInvariant();
        if (plan.Name.Length < 2) return OperationResult.Fail("نام طرح را وارد کنید.");
        if (plan.Slug.Length < 2 || !plan.Slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return OperationResult.Fail("شناسه (Slug) باید فقط شامل حروف انگلیسی، عدد و خط تیره باشد.");
        if (plan.Price < 0) return OperationResult.Fail("قیمت نمی‌تواند منفی باشد.");
        if (plan.DurationDays is < 1 or > 3650) return OperationResult.Fail("مدت طرح باید بین ۱ تا ۳۶۵۰ روز باشد.");
        if (plan.OldPrice is { } old && old < 0) return OperationResult.Fail("قیمت قبلی نامعتبر است.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Plans.AnyAsync(x => x.Slug == plan.Slug && x.Id != plan.Id, ct))
            return OperationResult.Fail("این شناسه (Slug) قبلاً برای طرح دیگری استفاده شده است.");

        var existing = await db.Plans.FirstOrDefaultAsync(x => x.Id == plan.Id, ct);
        if (existing is null)
        {
            db.Plans.Add(plan);
        }
        else
        {
            existing.Name = plan.Name; existing.Slug = plan.Slug; existing.Tagline = plan.Tagline; existing.Price = plan.Price;
            existing.OldPrice = plan.OldPrice; existing.DurationDays = plan.DurationDays; existing.BadgeText = plan.BadgeText;
            existing.IsPopular = plan.IsPopular; existing.IsActive = plan.IsActive; existing.IsTrial = plan.IsTrial; existing.SortOrder = plan.SortOrder;
            existing.MaxMonthlyBroadcasts = plan.MaxMonthlyBroadcasts; existing.MaxResolutionHeight = plan.MaxResolutionHeight;
            existing.StorageGb = plan.StorageGb; existing.FeaturesText = plan.FeaturesText;
        }
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, existing is null ? "plan.create" : "plan.update", $"{plan.Name} / {plan.Price:N0} تومان / {plan.DurationDays} روز", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeletePlanAsync(Guid id, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var plan = await db.Plans.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (plan is null) return OperationResult.Fail("طرح پیدا نشد.");
        var used = await db.Payments.AnyAsync(x => x.PlanId == id, ct) || await db.Subscriptions.AnyAsync(x => x.PlanId == id, ct);
        if (used) return OperationResult.Fail("این طرح در پرداخت‌ها یا اشتراک کاربران استفاده شده و قابل حذف نیست؛ آن را غیرفعال کنید.");
        db.Plans.Remove(plan);
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "plan.delete", plan.Name, null, ct);
        return OperationResult.Ok();
    }

    public async Task GrantAsync(Guid userId, Guid? planId, string planName, int days, SubscriptionSource source, string? note, ActorInfo actor, CancellationToken ct = default)
    {
        if (days <= 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var current = await db.Subscriptions.AsNoTracking()
            .Where(x => x.UserId == userId && x.Status == SubscriptionStatus.Active && x.EndsAt > now)
            .OrderByDescending(x => x.EndsAt).FirstOrDefaultAsync(ct);
        var start = current?.EndsAt ?? now;
        db.Subscriptions.Add(new UserSubscription
        {
            UserId = userId, PlanId = planId, PlanName = planName, StartsAt = start, EndsAt = start.AddDays(days),
            Status = SubscriptionStatus.Active, Source = source, Note = note, CreatedBy = actor.Name
        });
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "subscription.grant", $"{planName} / {days} روز / کاربر {userId}", null, ct);
    }

    public async Task<OperationResult> AdminSetAsync(Guid userId, Guid? planId, string planName, DateTime endsAtUtc, string? note, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(x => x.Id == userId, ct)) return OperationResult.Fail("کاربر پیدا نشد.");
        var now = DateTime.UtcNow;
        var active = await db.Subscriptions.Where(x => x.UserId == userId && x.Status == SubscriptionStatus.Active && x.EndsAt > now).ToListAsync(ct);
        foreach (var item in active)
        {
            item.Status = SubscriptionStatus.Cancelled;
            item.Note = string.IsNullOrWhiteSpace(item.Note) ? "جایگزین‌شده توسط مدیر" : item.Note + " | جایگزین‌شده توسط مدیر";
        }
        if (endsAtUtc > now)
        {
            db.Subscriptions.Add(new UserSubscription
            {
                UserId = userId, PlanId = planId, PlanName = planName, StartsAt = now, EndsAt = endsAtUtc,
                Status = SubscriptionStatus.Active, Source = SubscriptionSource.Admin, Note = note, CreatedBy = actor.Name
            });
        }
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "subscription.admin-set", $"کاربر {userId} / {planName} / تا {endsAtUtc:yyyy-MM-dd}", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> AdminCancelAsync(Guid userId, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var active = await db.Subscriptions.Where(x => x.UserId == userId && x.Status == SubscriptionStatus.Active && x.EndsAt > now).ToListAsync(ct);
        if (active.Count == 0) return OperationResult.Fail("اشتراک فعالی برای لغو وجود ندارد.");
        foreach (var item in active) { item.Status = SubscriptionStatus.Cancelled; item.Note = "لغو توسط مدیر"; }
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "subscription.cancel", $"کاربر {userId}", null, ct);
        return OperationResult.Ok();
    }
}
