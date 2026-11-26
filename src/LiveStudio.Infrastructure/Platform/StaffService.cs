using System.Text.RegularExpressions;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed partial class StaffService(IDbContextFactory<LiveStudioDbContext> factory, IActivityLogService log, PlatformOptions options) : IStaffService
{
    [GeneratedRegex("^[a-zA-Z0-9_.]{3,32}$")] private static partial Regex UsernameRegex();

    public async Task<PagedResult<StaffUser>> SearchAsync(string? query, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.StaffUsers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            q = q.Where(x => x.Username.Contains(term) || x.DisplayName.Contains(query.Trim()));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(x => x.Username).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<StaffUser>(items, total, page, pageSize);
    }

    public async Task<StaffUser?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<OperationResult> SaveAsync(Guid? id, string username, string displayName, string? newPassword, IEnumerable<string> permissions, bool isActive, ActorInfo actor, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim().ToLowerInvariant();
        displayName = (displayName ?? string.Empty).Trim();
        if (!UsernameRegex().IsMatch(username)) return OperationResult.Fail("نام کاربری باید ۳ تا ۳۲ نویسه و فقط شامل حروف انگلیسی، عدد، نقطه و زیرخط باشد.");
        if (string.Equals(username, options.AdminUsername, StringComparison.OrdinalIgnoreCase)) return OperationResult.Fail("این نام کاربری برای مدیر اصلی رزرو شده است.");
        if (displayName.Length < 2) return OperationResult.Fail("نام نمایشی را وارد کنید.");

        var valid = Permissions.All.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = permissions.Where(valid.Contains).Select(x => x.ToLowerInvariant()).Distinct().ToList();
        if (selected.Count == 0) return OperationResult.Fail("حداقل یک دسترسی انتخاب کنید.");

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.StaffUsers.AnyAsync(x => x.Username == username && x.Id != (id ?? Guid.Empty), ct)) return OperationResult.Fail("این نام کاربری قبلاً ثبت شده است.");

        var entity = id is null ? null : await db.StaffUsers.FirstOrDefaultAsync(x => x.Id == id.Value, ct);
        if (id is not null && entity is null) return OperationResult.Fail("کاربر مدیریتی پیدا نشد.");
        if (entity is null && string.IsNullOrWhiteSpace(newPassword)) return OperationResult.Fail("برای کاربر جدید رمز عبور لازم است.");
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            if (newPassword.Length < 8 || !newPassword.Any(char.IsLetter) || !newPassword.Any(char.IsDigit))
                return OperationResult.Fail("رمز عبور باید حداقل ۸ نویسه و شامل حرف و عدد باشد.");
        }

        if (entity is null)
        {
            entity = new StaffUser { Username = username };
            db.StaffUsers.Add(entity);
        }
        entity.Username = username; entity.DisplayName = displayName; entity.IsActive = isActive; entity.PermissionsCsv = string.Join(',', selected);
        if (!string.IsNullOrWhiteSpace(newPassword)) entity.PasswordHash = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, id is null ? "staff.create" : "staff.update", $"{username} ({string.Join(',', selected)})", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(Guid id, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.StaffUsers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return OperationResult.Fail("کاربر پیدا نشد.");
        db.StaffUsers.Remove(entity);
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "staff.delete", entity.Username, null, ct);
        return OperationResult.Ok();
    }
}
