using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class TicketService(IDbContextFactory<LiveStudioDbContext> factory, IActivityLogService log) : ITicketService
{
    private static readonly string[] Departments = ["support", "billing", "technical", "sales"];

    public async Task<OperationResult<Guid>> CreateAsync(Guid userId, string subject, string department, TicketPriority priority, string body, CancellationToken ct = default)
    {
        subject = (subject ?? string.Empty).Trim(); body = (body ?? string.Empty).Trim();
        if (subject.Length is < 4 or > 200) return OperationResult<Guid>.Fail("موضوع تیکت باید بین ۴ تا ۲۰۰ نویسه باشد.");
        if (body.Length is < 10 or > 5000) return OperationResult<Guid>.Fail("متن تیکت باید حداقل ۱۰ و حداکثر ۵۰۰۰ نویسه باشد.");
        if (!Departments.Contains(department)) department = "support";

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return OperationResult<Guid>.Fail("کاربر پیدا نشد.");
        var openCount = await db.Tickets.CountAsync(x => x.UserId == userId && x.Status != TicketStatus.Closed, ct);
        if (openCount >= 15) return OperationResult<Guid>.Fail("تعداد تیکت‌های باز شما به سقف مجاز رسیده است. ابتدا تیکت‌های قبلی را ببندید.");

        var last = await db.Tickets.AsNoTracking().OrderByDescending(x => x.Number).Select(x => x.Number).FirstOrDefaultAsync(ct);
        var ticket = new Ticket { Number = Math.Max(last, 10000) + 1, UserId = userId, Subject = subject, Department = department, Priority = priority, Status = TicketStatus.Open };
        db.Tickets.Add(ticket);
        db.TicketMessages.Add(new TicketMessage { TicketId = ticket.Id, IsStaff = false, SenderName = user.FullName, Body = body });
        await db.SaveChangesAsync(ct);
        await log.LogAsync(new ActorInfo(ActorType.Customer, userId.ToString(), user.FullName), "ticket.create", $"#{ticket.Number}: {subject}", null, ct);
        return OperationResult<Guid>.Ok(ticket.Id);
    }

    public async Task<PagedResult<TicketRow>> SearchAsync(Guid? userId, string? query, TicketStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Tickets.AsNoTracking().AsQueryable();
        if (userId is not null) q = q.Where(x => x.UserId == userId.Value);
        if (status is not null) q = q.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = IranianValidators.NormalizeDigits(query.Trim());
            var userIds = await db.Users.AsNoTracking().Where(u => u.FullName.Contains(term) || u.Mobile.Contains(term)).Select(u => u.Id).ToListAsync(ct);
            var number = long.TryParse(term, out var n) ? n : -1;
            q = q.Where(x => x.Subject.Contains(term) || x.Number == number || userIds.Contains(x.UserId));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Select(x => x.UserId).Distinct().ToList();
        var ticketIds = rows.Select(x => x.Id).ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        var messages = await db.TicketMessages.AsNoTracking().Where(m => ticketIds.Contains(m.TicketId)).Select(m => m.TicketId).ToListAsync(ct);
        var items = rows.Select(t =>
        {
            var u = users.FirstOrDefault(x => x.Id == t.UserId);
            return new TicketRow(t.Id, t.Number, t.Subject, t.Department, t.Priority, t.Status, t.CreatedAt, t.UpdatedAt, t.UserId,
                u?.FullName ?? "—", u?.Mobile ?? "—", messages.Count(m => m == t.Id));
        }).ToList();
        return new PagedResult<TicketRow>(items, total, page, pageSize);
    }

    public async Task<TicketDetail?> GetAsync(Guid id, Guid? requiredUserId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (ticket is null || (requiredUserId is not null && ticket.UserId != requiredUserId.Value)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticket.UserId, ct);
        if (user is null) return null;
        var messages = await db.TicketMessages.AsNoTracking().Where(x => x.TicketId == id).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return new TicketDetail(ticket, user, messages);
    }

    public async Task<OperationResult> ReplyAsync(Guid ticketId, Guid? requiredUserId, bool isStaff, string senderName, string body, CancellationToken ct = default)
    {
        body = (body ?? string.Empty).Trim();
        if (body.Length is < 2 or > 5000) return OperationResult.Fail("متن پیام باید بین ۲ تا ۵۰۰۰ نویسه باشد.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (ticket is null || (requiredUserId is not null && ticket.UserId != requiredUserId.Value)) return OperationResult.Fail("تیکت پیدا نشد.");
        if (ticket.Status == TicketStatus.Closed && !isStaff) return OperationResult.Fail("این تیکت بسته شده است. برای موضوع جدید تیکت تازه ثبت کنید.");

        db.TicketMessages.Add(new TicketMessage { TicketId = ticketId, IsStaff = isStaff, SenderName = senderName, Body = body });
        ticket.Status = isStaff ? TicketStatus.Answered : TicketStatus.CustomerReply;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(new ActorInfo(isStaff ? ActorType.Staff : ActorType.Customer, requiredUserId?.ToString() ?? "staff", senderName),
            isStaff ? "ticket.staff-reply" : "ticket.customer-reply", $"#{ticket.Number}", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetStatusAsync(Guid ticketId, TicketStatus status, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (ticket is null) return OperationResult.Fail("تیکت پیدا نشد.");
        ticket.Status = status; ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "ticket.status", $"#{ticket.Number} → {status}", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetPriorityAsync(Guid ticketId, TicketPriority priority, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (ticket is null) return OperationResult.Fail("تیکت پیدا نشد.");
        ticket.Priority = priority;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "ticket.priority", $"#{ticket.Number} → {priority}", null, ct);
        return OperationResult.Ok();
    }

    public async Task<int> CountOpenAsync(Guid? userId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Tickets.AsNoTracking().AsQueryable();
        // Admin view: tickets waiting for staff. Customer view: tickets with a staff answer.
        q = userId is null
            ? q.Where(x => x.Status == TicketStatus.Open || x.Status == TicketStatus.CustomerReply)
            : q.Where(x => x.UserId == userId.Value && x.Status == TicketStatus.Answered);
        return await q.CountAsync(ct);
    }
}
