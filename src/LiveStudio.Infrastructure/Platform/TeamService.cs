using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class TeamService(IDbContextFactory<LiveStudioDbContext> factory) : ITeamService
{
    private const int MaxMembers = 10;

    public async Task<IReadOnlyList<TeamMemberRow>> ListMembersAsync(Guid ownerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await (from m in db.TeamMembers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.MemberUserId equals u.Id
                          where m.OwnerUserId == ownerId
                          orderby m.CreatedAt
                          select new TeamMemberRow(m.Id, u.Id, u.FullName, u.Mobile, m.Role)).ToListAsync(ct);
        return rows;
    }

    public async Task<OperationResult> InviteAsync(Guid ownerId, string mobile, string role, CancellationToken ct = default)
    {
        if (!StudioRoles.IsValid(role)) return OperationResult.Fail("نقش معتبر نیست.");
        var normalized = IranianValidators.NormalizeMobile(mobile ?? string.Empty);
        if (normalized is null) return OperationResult.Fail("شماره موبایل را به‌صورت صحیح وارد کنید (مثال: 09123456789).");
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(x => x.Mobile == normalized, ct);
        if (user is null) return OperationResult.Fail("کاربری با این شماره در LiveStudio ثبت‌نام نکرده است. ابتدا باید ثبت‌نام کند.");
        if (user.Id == ownerId) return OperationResult.Fail("شما صاحب این استودیو هستید.");
        if (user.IsBlocked) return OperationResult.Fail("این کاربر مسدود است.");
        if (await db.TeamMembers.AnyAsync(x => x.OwnerUserId == ownerId && x.MemberUserId == user.Id, ct))
            return OperationResult.Fail("این کاربر قبلاً به تیم اضافه شده است.");
        if (await db.TeamMembers.CountAsync(x => x.OwnerUserId == ownerId, ct) >= MaxMembers)
            return OperationResult.Fail($"حداکثر {MaxMembers} عضو مجاز است.");
        db.TeamMembers.Add(new TeamMember { OwnerUserId = ownerId, MemberUserId = user.Id, Role = role });
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> ChangeRoleAsync(Guid ownerId, Guid id, string role, CancellationToken ct = default)
    {
        if (!StudioRoles.IsValid(role)) return OperationResult.Fail("نقش معتبر نیست.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TeamMembers.FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == ownerId, ct);
        if (row is null) return OperationResult.Fail("عضو پیدا نشد.");
        row.Role = role;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> RemoveAsync(Guid ownerId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TeamMembers.FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == ownerId, ct);
        if (row is null) return OperationResult.Fail("عضو پیدا نشد.");
        db.TeamMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok();
    }

    public async Task<IReadOnlyList<TeamMembership>> MembershipsAsync(Guid memberId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (from m in db.TeamMembers.AsNoTracking()
                      join u in db.Users.AsNoTracking() on m.OwnerUserId equals u.Id
                      where m.MemberUserId == memberId && !u.IsBlocked
                      select new TeamMembership(u.Id, u.ChannelName ?? u.FullName, m.Role)).ToListAsync(ct);
    }

    public async Task<string?> GetRoleAsync(Guid ownerId, Guid userId, CancellationToken ct = default)
    {
        if (ownerId == userId) return StudioRoles.Host;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AsNoTracking().Where(x => x.OwnerUserId == ownerId && x.MemberUserId == userId).Select(x => x.Role).FirstOrDefaultAsync(ct);
    }
}
