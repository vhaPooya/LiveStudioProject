using System.Text.RegularExpressions;
using LiveStudio.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LiveStudioDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        // The Web and Api processes may start at the same time; losing the creation race is harmless.
        try { await db.Database.EnsureCreatedAsync(ct); }
        catch (Exception ex) { logger.LogWarning("EnsureCreated raced with another process: {Message}", ex.Message); }
        await EnsureMissingTablesAsync(db, logger, ct);
        try { await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct); }
        catch (Exception ex) { logger.LogWarning("SQLite WAL mode could not be enabled: {Message}", ex.Message); }

        try { await SeedAsync(db, ct); }
        catch (Exception ex) { logger.LogWarning("Seeding skipped (another process seeded first): {Message}", ex.Message); }
        logger.LogInformation("LiveStudio database is ready.");
    }

    /// <summary>
    /// EnsureCreated does nothing for a database created by an earlier phase. This creates every table / index
    /// of the current model that is still missing, without touching existing tables.
    /// </summary>
    private static async Task EnsureMissingTablesAsync(LiveStudioDbContext db, ILogger logger, CancellationToken ct)
    {
        var script = db.Database.GenerateCreateScript();
        var statements = Regex.Split(script, @";\s*(?:\r?\n|$)").Select(s => s.Trim()).Where(s => s.Length > 0);
        foreach (var raw in statements)
        {
            var sql = raw;
            if (sql.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase) && !sql.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE TABLE IF NOT EXISTS " + sql["CREATE TABLE ".Length..];
            else if (sql.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase) && !sql.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE UNIQUE INDEX IF NOT EXISTS " + sql["CREATE UNIQUE INDEX ".Length..];
            else if (sql.StartsWith("CREATE INDEX ", StringComparison.OrdinalIgnoreCase) && !sql.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE INDEX IF NOT EXISTS " + sql["CREATE INDEX ".Length..];
            else
                continue;

            try { await db.Database.ExecuteSqlRawAsync(sql, ct); }
            catch (Exception ex) { logger.LogWarning("Schema statement skipped: {Message}", ex.Message); }
        }
    }

    private static async Task SeedAsync(LiveStudioDbContext db, CancellationToken ct)
    {
        if (!await db.Plans.AnyAsync(ct))
        {
            db.Plans.AddRange(DefaultPlans());
        }
        if (!await db.Faqs.AnyAsync(ct))
        {
            db.Faqs.AddRange(DefaultFaq());
        }
        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<SubscriptionPlan> DefaultPlans()
    {
        yield return new SubscriptionPlan
        {
            Name = "دوره آزمایشی", Slug = "trial", Price = 0, DurationDays = 7, IsTrial = true, IsActive = true, SortOrder = 0,
            MaxMonthlyBroadcasts = 3, MaxResolutionHeight = 720, StorageGb = 1,
            Tagline = "هدیه ثبت‌نام برای آشنایی با استودیو",
            FeaturesText = "دسترسی به استودیوی پخش زنده\nاستودیوی تولید محتوا\nتله‌پرومپتر هوشمند"
        };
        yield return new SubscriptionPlan
        {
            Name = "پایه", Slug = "starter-monthly", Price = 390_000, DurationDays = 30, SortOrder = 10,
            MaxMonthlyBroadcasts = 10, MaxResolutionHeight = 720, StorageGb = 5,
            Tagline = "برای شروع پخش زنده حرفه‌ای",
            FeaturesText = "پخش زنده تا کیفیت 720p\nتا ۱۰ پخش زنده در ماه\nتله‌پرومپتر و میکسر صدا\nMedia Box با ۵ گیگابایت فضا\nپشتیبانی از طریق تیکت"
        };
        yield return new SubscriptionPlan
        {
            Name = "حرفه‌ای", Slug = "pro-monthly", Price = 890_000, OldPrice = 1_090_000, DurationDays = 30, SortOrder = 20,
            IsPopular = true, BadgeText = "محبوب‌ترین", MaxMonthlyBroadcasts = 0, MaxResolutionHeight = 1080, StorageGb = 30,
            Tagline = "همه ابزارهای استودیو بدون محدودیت پخش",
            FeaturesText = "پخش زنده تا کیفیت 1080p\nتعداد پخش نامحدود\nاصلاح رنگ و نور زنده\nمدیریت چند صحنه و TAKE خودکار\nMedia Box با ۳۰ گیگابایت فضا\nپشتیبانی اولویت‌دار"
        };
        yield return new SubscriptionPlan
        {
            Name = "تیمی", Slug = "team-monthly", Price = 2_190_000, DurationDays = 30, SortOrder = 30,
            MaxMonthlyBroadcasts = 0, MaxResolutionHeight = 1080, StorageGb = 120,
            Tagline = "برای رسانه‌ها و تولیدکنندگان حرفه‌ای",
            FeaturesText = "تمام امکانات طرح حرفه‌ای\nفضای ۱۲۰ گیگابایت برای رسانه\nپشتیبانی اختصاصی\nمشاوره راه‌اندازی اولیه"
        };
        yield return new SubscriptionPlan
        {
            Name = "پایه سالانه", Slug = "starter-yearly", Price = 3_900_000, OldPrice = 4_680_000, DurationDays = 365, SortOrder = 40,
            MaxMonthlyBroadcasts = 10, MaxResolutionHeight = 720, StorageGb = 5,
            Tagline = "دو ماه رایگان نسبت به پرداخت ماهانه",
            FeaturesText = "تمام امکانات طرح پایه\nپرداخت یک‌باره برای یک سال"
        };
        yield return new SubscriptionPlan
        {
            Name = "حرفه‌ای سالانه", Slug = "pro-yearly", Price = 8_900_000, OldPrice = 10_680_000, DurationDays = 365, SortOrder = 50,
            IsPopular = true, BadgeText = "بهترین ارزش", MaxMonthlyBroadcasts = 0, MaxResolutionHeight = 1080, StorageGb = 30,
            Tagline = "دو ماه رایگان نسبت به پرداخت ماهانه",
            FeaturesText = "تمام امکانات طرح حرفه‌ای\nپرداخت یک‌باره برای یک سال\nپشتیبانی اولویت‌دار"
        };
        yield return new SubscriptionPlan
        {
            Name = "تیمی سالانه", Slug = "team-yearly", Price = 21_900_000, OldPrice = 26_280_000, DurationDays = 365, SortOrder = 60,
            MaxMonthlyBroadcasts = 0, MaxResolutionHeight = 1080, StorageGb = 120,
            Tagline = "دو ماه رایگان نسبت به پرداخت ماهانه",
            FeaturesText = "تمام امکانات طرح تیمی\nپرداخت یک‌باره برای یک سال"
        };
    }

    private static IEnumerable<FaqItem> DefaultFaq()
    {
        var items = new (string Q, string A)[]
        {
            ("آیا برای پخش زنده به OBS نیاز دارم؟", "خیر. LiveStudio یک استودیوی کامل داخل مرورگر است؛ دوربین، میکروفون، صحنه‌ها و لایه‌های تصویری مستقیم در مرورگر ترکیب می‌شوند و خروجی نهایی بدون نصب هیچ نرم‌افزاری به یوتیوب ارسال می‌شود."),
            ("اتصال به کانال یوتیوب چگونه انجام می‌شود؟", "هر مشتری Google OAuth Client اختصاصی خودش را در بخش تنظیمات ثبت می‌کند. ویدئوی آموزشی داخل پنل مراحل ساخت آن را در Google Cloud Console نشان می‌دهد. پس از ثبت، تأیید دسترسی در صفحه رسمی گوگل انجام می‌شود."),
            ("اطلاعات OAuth و Stream Key من امن است؟", "Client Secret و توکن‌های شما رمزنگاری‌شده و جدا برای حساب خودتان ذخیره می‌شود. Stream Key هرگز به مرورگر ارسال نمی‌شود و فقط در سرور استفاده می‌شود."),
            ("می‌توانم اشتراک را تمدید یا ارتقا دهم؟", "بله. از بخش «اشتراک و پرداخت» در پنل کاربری هر زمان می‌توانید پرداخت آنلاین انجام دهید. اگر اشتراک فعال داشته باشید، مدت جدید به انتهای اشتراک فعلی اضافه می‌شود."),
            ("در صورت بروز مشکل چگونه پشتیبانی بگیرم؟", "از بخش «تیکت‌ها» در پنل کاربری درخواست خود را ثبت کنید. تیم پشتیبانی در همان صفحه پاسخ می‌دهد و می‌توانید گفتگو را ادامه دهید."),
            ("به چه سخت‌افزاری نیاز دارم؟", "یک رایانه با مرورگر مدرن (Chrome یا Edge)، دوربین و میکروفون و اینترنت پایدار. برای پخش 1080p پیشنهاد می‌شود سرعت آپلود حداقل ۸ مگابیت بر ثانیه باشد."),
            ("ورود به سامانه چگونه است؟", "ورود با کد ملی و شماره موبایل انجام می‌شود و یک کد یک‌بارمصرف پیامکی برای تأیید هویت ارسال می‌شود؛ نیازی به رمز عبور نیست.")
        };
        var order = 1;
        foreach (var (q, a) in items) yield return new FaqItem { Question = q, Answer = a, SortOrder = order++ };
    }
}
