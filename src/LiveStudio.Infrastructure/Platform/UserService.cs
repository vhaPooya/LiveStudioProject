using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class UserService(IDbContextFactory<LiveStudioDbContext> factory, IActivityLogService log) : IUserService
{
    public async Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<PagedResult<UserListItem>> SearchAsync(string? query, string? filter, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = IranianValidators.NormalizeDigits(query.Trim());
            q = q.Where(x => x.FullName.Contains(term) || x.Mobile.Contains(term) || x.NationalCode.Contains(term)
                             || (x.Email != null && x.Email.Contains(term)) || (x.ChannelName != null && x.ChannelName.Contains(term)));
        }
        switch (filter)
        {
            case "blocked": q = q.Where(x => x.IsBlocked); break;
            case "subscribed":
                q = q.Where(x => db.Subscriptions.Any(s => s.UserId == x.Id && s.Status == SubscriptionStatus.Active && s.EndsAt > now)); break;
            case "expired":
                q = q.Where(x => !db.Subscriptions.Any(s => s.UserId == x.Id && s.Status == SubscriptionStatus.Active && s.EndsAt > now)); break;
            case "connected":
                q = q.Where(x => db.TenantOAuth.Any(o => o.UserId == x.Id && o.State == OAuthConnectionState.Connected)); break;
        }

        var total = await q.CountAsync(ct);
        var users = await q.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = users.Select(x => x.Id).ToList();
        var subs = await db.Subscriptions.AsNoTracking().Where(x => ids.Contains(x.UserId) && x.Status == SubscriptionStatus.Active).ToListAsync(ct);
        var oauth = await db.TenantOAuth.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync(ct);

        var items = users.Select(u =>
        {
            var latest = subs.Where(s => s.UserId == u.Id).OrderByDescending(s => s.EndsAt).FirstOrDefault();
            var o = oauth.FirstOrDefault(x => x.UserId == u.Id);
            return new UserListItem(u.Id, u.FullName, u.NationalCode, u.Mobile, u.IsBlocked, u.CreatedAt, u.LastLoginAt,
                latest?.PlanName, latest?.EndsAt, o?.State ?? OAuthConnectionState.NotConfigured);
        }).ToList();
        return new PagedResult<UserListItem>(items, total, page, pageSize);
    }

    public async Task<OperationResult> UpdateProfileAsync(Guid id, string fullName, string? email, string? channelName, ActorInfo actor, CancellationToken ct = default)
    {
        fullName = (fullName ?? string.Empty).Trim();
        if (fullName.Length is < 3 or > 120) return OperationResult.Fail("نام و نام خانوادگی باید بین ۳ تا ۱۲۰ نویسه باشد.");
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (email is not null && (email.Length > 200 || !email.Contains('@') || email.Contains(' '))) return OperationResult.Fail("ایمیل نامعتبر است.");
        channelName = string.IsNullOrWhiteSpace(channelName) ? null : channelName.Trim();
        if (channelName?.Length > 200) return OperationResult.Fail("نام کانال بیش از حد طولانی است.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (user is null) return OperationResult.Fail("کاربر پیدا نشد.");
        user.FullName = fullName; user.Email = email; user.ChannelName = channelName;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "profile.update", null, null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> AdminUpdateAsync(Guid id, string fullName, string nationalCode, string mobile, string? email, string? note, ActorInfo actor, CancellationToken ct = default)
    {
        var normalizedMobile = IranianValidators.NormalizeMobile(mobile);
        var code = IranianValidators.NormalizeDigits(nationalCode);
        if (normalizedMobile is null) return OperationResult.Fail("شماره موبایل نامعتبر است.");
        if (!IranianValidators.IsValidNationalCode(code)) return OperationResult.Fail("کد ملی نامعتبر است.");
        var current = await GetAsync(id, ct);
        if (current is null) return OperationResult.Fail("کاربر پیدا نشد.");
        var basic = await UpdateProfileAsync(id, fullName, email, current.ChannelName, actor, ct);
        if (!basic.Success) return basic;

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(x => x.Id != id && (x.Mobile == normalizedMobile || x.NationalCode == code), ct))
            return OperationResult.Fail("کد ملی یا موبایل برای کاربر دیگری ثبت شده است.");
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (user is null) return OperationResult.Fail("کاربر پیدا نشد.");
        user.Mobile = normalizedMobile; user.NationalCode = code; user.AdminNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "user.admin-update", $"کاربر {id}", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetBlockedAsync(Guid id, bool blocked, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (user is null) return OperationResult.Fail("کاربر پیدا نشد.");
        user.IsBlocked = blocked;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, blocked ? "user.block" : "user.unblock", $"{user.FullName} ({user.Mobile})", null, ct);
        return OperationResult.Ok();
    }
}
