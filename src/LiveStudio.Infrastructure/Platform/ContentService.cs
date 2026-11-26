using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Platform;

public sealed class ContentService(IDbContextFactory<LiveStudioDbContext> factory, ISettingsService settings, IActivityLogService log) : IContentService
{
    public const string DefaultHeroTitle = "استودیوی حرفه‌ای پخش زنده، مستقیم در مرورگر شما";
    public const string DefaultHeroSubtitle = "بدون OBS و بدون نصب نرم‌افزار؛ دوربین، میکروفون، صحنه‌ها و گرافیک را در مرورگر ترکیب کنید و با یک کلیک روی کانال یوتیوب خودتان پخش زنده کنید.";

    public async Task<SiteContent> GetSiteContentAsync(CancellationToken ct = default) => new()
    {
        SiteName = await settings.GetAsync(SettingKeys.SiteName, "LiveStudio", ct),
        HeroTitle = await settings.GetAsync(SettingKeys.HeroTitle, DefaultHeroTitle, ct),
        HeroSubtitle = await settings.GetAsync(SettingKeys.HeroSubtitle, DefaultHeroSubtitle, ct),
        AnnouncementBar = await settings.GetAsync(SettingKeys.AnnouncementBar, string.Empty, ct),
        SupportPhone = await settings.GetAsync(SettingKeys.SupportPhone, string.Empty, ct),
        SupportEmail = await settings.GetAsync(SettingKeys.SupportEmail, string.Empty, ct),
        SupportTelegram = await settings.GetAsync(SettingKeys.SupportTelegram, string.Empty, ct),
        FooterText = await settings.GetAsync(SettingKeys.FooterText, "تمامی حقوق برای LiveStudio محفوظ است.", ct),
        TermsText = await settings.GetAsync(SettingKeys.TermsText, string.Empty, ct)
    };

    public async Task SaveSiteContentAsync(SiteContent content, ActorInfo actor, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.SiteName, content.SiteName?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.HeroTitle, content.HeroTitle?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.HeroSubtitle, content.HeroSubtitle?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.AnnouncementBar, content.AnnouncementBar?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.SupportPhone, content.SupportPhone?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.SupportEmail, content.SupportEmail?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.SupportTelegram, content.SupportTelegram?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.FooterText, content.FooterText?.Trim(), false, ct);
        await settings.SetAsync(SettingKeys.TermsText, content.TermsText?.Trim(), false, ct);
        await log.LogAsync(actor, "content.site-update", null, null, ct);
    }

    public async Task<IReadOnlyList<FaqItem>> GetFaqAsync(bool onlyActive, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Faqs.AsNoTracking().AsQueryable();
        if (onlyActive) q = q.Where(x => x.IsActive);
        return await q.OrderBy(x => x.SortOrder).ToListAsync(ct);
    }

    public async Task<OperationResult> SaveFaqAsync(FaqItem item, ActorInfo actor, CancellationToken ct = default)
    {
        item.Question = (item.Question ?? string.Empty).Trim(); item.Answer = (item.Answer ?? string.Empty).Trim();
        if (item.Question.Length < 4) return OperationResult.Fail("متن پرسش را وارد کنید.");
        if (item.Answer.Length < 4) return OperationResult.Fail("متن پاسخ را وارد کنید.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Faqs.FirstOrDefaultAsync(x => x.Id == item.Id, ct);
        if (existing is null) db.Faqs.Add(item);
        else { existing.Question = item.Question; existing.Answer = item.Answer; existing.SortOrder = item.SortOrder; existing.IsActive = item.IsActive; }
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, existing is null ? "faq.create" : "faq.update", item.Question, null, ct);
        return OperationResult.Ok();
    }

    public async Task DeleteFaqAsync(Guid id, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.Faqs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;
        db.Faqs.Remove(item);
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "faq.delete", item.Question, null, ct);
    }

    public async Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(bool onlyActive, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var q = db.Announcements.AsNoTracking().AsQueryable();
        if (onlyActive) q = q.Where(x => x.IsActive && (x.ExpiresAt == null || x.ExpiresAt > now));
        return await q.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
    }

    public async Task<OperationResult> SaveAnnouncementAsync(Announcement item, ActorInfo actor, CancellationToken ct = default)
    {
        item.Title = (item.Title ?? string.Empty).Trim();
        if (item.Title.Length < 3) return OperationResult.Fail("عنوان اعلان را وارد کنید.");
        if (item.Level is not ("info" or "success" or "warning" or "danger")) item.Level = "info";
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Announcements.FirstOrDefaultAsync(x => x.Id == item.Id, ct);
        if (existing is null) db.Announcements.Add(item);
        else { existing.Title = item.Title; existing.Body = item.Body; existing.Level = item.Level; existing.IsActive = item.IsActive; existing.ExpiresAt = item.ExpiresAt; }
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, existing is null ? "announcement.create" : "announcement.update", item.Title, null, ct);
        return OperationResult.Ok();
    }

    public async Task DeleteAnnouncementAsync(Guid id, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;
        db.Announcements.Remove(item);
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "announcement.delete", item.Title, null, ct);
    }
}
