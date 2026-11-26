using System.Security.Cryptography;
using System.Text;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LiveStudio.Infrastructure.Platform;

public sealed class AuthService(
    IDbContextFactory<LiveStudioDbContext> factory,
    ISmsOtpClient sms,
    ISettingsService settings,
    ISubscriptionService subscriptions,
    IActivityLogService log,
    IMemoryCache cache,
    PlatformOptions options) : IAuthService
{
    private const int MaxSendsPerHourPerMobile = 5;
    private const int MaxSendsPerHourPerIp = 25;
    private const int MaxVerifyAttempts = 5;
    private static readonly TimeSpan CooldownBetweenSends = TimeSpan.FromSeconds(60);

    // ---------------------------------------------------------------- OTP
    public async Task<OtpSendResult> SendOtpAsync(AuthMode mode, string nationalCode, string mobile, string? fullName, string? ip, CancellationToken ct = default)
    {
        var normalizedMobile = IranianValidators.NormalizeMobile(mobile);
        var code = IranianValidators.NormalizeDigits(nationalCode);
        if (normalizedMobile is null) return new(false, "شماره موبایل را به‌صورت صحیح وارد کنید (مثال: 09123456789).");
        if (!IranianValidators.IsValidNationalCode(code)) return new(false, "کد ملی نامعتبر است.");

        if (mode == AuthMode.Register)
        {
            if (!await settings.GetBoolAsync(SettingKeys.RegistrationOpen, true, ct)) return new(false, "ثبت‌نام کاربر جدید موقتاً بسته است.");
            if ((fullName ?? string.Empty).Trim().Length < 3) return new(false, "نام و نام خانوادگی را وارد کنید.");
        }

        var cooldownKey = $"otp:cd:{normalizedMobile}";
        if (cache.TryGetValue(cooldownKey, out DateTime until) && until > DateTime.UtcNow)
        {
            var wait = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            return new(false, $"برای دریافت کد جدید {wait} ثانیه صبر کنید.", wait);
        }
        if (Increment($"otp:hour:m:{normalizedMobile}", TimeSpan.FromHours(1)) > MaxSendsPerHourPerMobile)
            return new(false, "تعداد درخواست کد برای این شماره زیاد است. یک ساعت بعد دوباره تلاش کنید.");
        if (!string.IsNullOrEmpty(ip) && Increment($"otp:hour:ip:{ip}", TimeSpan.FromHours(1)) > MaxSendsPerHourPerIp)
            return new(false, "تعداد درخواست‌ها از این شبکه زیاد است. بعداً دوباره تلاش کنید.");

        await using var db = await factory.CreateDbContextAsync(ct);
        bool shouldSend;
        if (mode == AuthMode.Register)
        {
            var exists = await db.Users.AnyAsync(x => x.NationalCode == code || x.Mobile == normalizedMobile, ct);
            if (exists) return new(false, "با این کد ملی یا شماره موبایل قبلاً ثبت‌نام شده است. از بخش «ورود» استفاده کنید.");
            shouldSend = true;
        }
        else
        {
            // Do not reveal whether a national code / mobile pair is registered.
            shouldSend = await db.Users.AnyAsync(x => x.NationalCode == code && x.Mobile == normalizedMobile && !x.IsBlocked, ct);
        }

        if (shouldSend)
        {
            var result = await sms.SendAsync(normalizedMobile, ct);
            if (!result.Success) return new(false, result.Error ?? "ارسال پیامک ناموفق بود.");
        }

        cache.Set(cooldownKey, DateTime.UtcNow.Add(CooldownBetweenSends), CooldownBetweenSends);
        await log.LogAsync(new ActorInfo(ActorType.Customer, "anonymous", Mask(normalizedMobile), ip),
            mode == AuthMode.Register ? "auth.register.otp-sent" : "auth.login.otp-requested", shouldSend ? null : "no-matching-user");
        return new(true, null, (int)CooldownBetweenSends.TotalSeconds);
    }

    public async Task<OtpVerifyResult> VerifyOtpAsync(AuthMode mode, string nationalCode, string mobile, string code, string? fullName, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var normalizedMobile = IranianValidators.NormalizeMobile(mobile);
        var national = IranianValidators.NormalizeDigits(nationalCode);
        var otp = IranianValidators.NormalizeDigits(code);
        if (normalizedMobile is null || !IranianValidators.IsValidNationalCode(national)) return new(false, "اطلاعات واردشده نامعتبر است.", null);
        if (otp.Length is < 4 or > 8 || !otp.All(char.IsDigit)) return new(false, "کد تأیید را به‌صورت صحیح وارد کنید.", null);

        var attemptKey = $"otp:try:{normalizedMobile}";
        var attempts = cache.TryGetValue(attemptKey, out int a) ? a : 0;
        if (attempts >= MaxVerifyAttempts) return new(false, "تعداد تلاش ناموفق زیاد بود. چند دقیقه بعد کد جدید دریافت کنید.", null);

        await using var db = await factory.CreateDbContextAsync(ct);
        AppUser? user = null;
        if (mode == AuthMode.Login)
        {
            user = await db.Users.FirstOrDefaultAsync(x => x.NationalCode == national && x.Mobile == normalizedMobile, ct);
            if (user is null || user.IsBlocked)
            {
                BumpAttempts(attemptKey);
                return new(false, "کد وارد شده صحیح نیست یا منقضی شده است.", null);
            }
        }

        var verify = await sms.VerifyAsync(normalizedMobile, otp, ct);
        if (!verify.Success) return new(false, verify.Error, null);
        if (!verify.Value)
        {
            BumpAttempts(attemptKey);
            await log.LogAsync(new ActorInfo(ActorType.Customer, user?.Id.ToString() ?? "anonymous", Mask(normalizedMobile), ip), "auth.otp-failed", null, userAgent, ct);
            return new(false, "کد وارد شده صحیح نیست یا منقضی شده است.", null);
        }

        if (mode == AuthMode.Register)
        {
            var name = (fullName ?? string.Empty).Trim();
            if (name.Length < 3) return new(false, "نام و نام خانوادگی را وارد کنید.", null);
            if (await db.Users.AnyAsync(x => x.NationalCode == national || x.Mobile == normalizedMobile, ct))
                return new(false, "با این مشخصات قبلاً ثبت‌نام شده است. وارد شوید.", null);
            user = new AppUser { NationalCode = national, Mobile = normalizedMobile, FullName = name[..Math.Min(120, name.Length)], LastLoginAt = DateTime.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            var trialDays = await settings.GetIntAsync(SettingKeys.TrialDays, 7, ct);
            if (trialDays > 0)
            {
                var trialPlan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(x => x.IsTrial && x.IsActive, ct);
                await subscriptions.GrantAsync(user.Id, trialPlan?.Id, trialPlan?.Name ?? "دوره آزمایشی", trialDays, SubscriptionSource.Trial,
                    "هدیه ثبت‌نام", ActorInfo.System, ct);
            }
            await log.LogAsync(new ActorInfo(ActorType.Customer, user.Id.ToString(), user.FullName, ip), "auth.register", $"موبایل {Mask(normalizedMobile)}", userAgent, ct);
        }
        else
        {
            var tracked = await db.Users.FirstAsync(x => x.Id == user!.Id, ct);
            tracked.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await log.LogAsync(new ActorInfo(ActorType.Customer, user!.Id.ToString(), user.FullName, ip), "auth.login", null, userAgent, ct);
        }

        cache.Remove(attemptKey);
        var principal = new SignInPrincipal(user!.Id.ToString(), user.FullName, "Customer", []);
        return new(true, null, IssueTicket(principal));
    }

    // ---------------------------------------------------------------- Admin / staff
    public async Task<OperationResult<string>> AdminLoginAsync(string username, string password, string? ip, string? userAgent, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        var failKey = $"admin:fail:{ip ?? "?"}";
        if ((cache.TryGetValue(failKey, out int fails) ? fails : 0) >= 6)
            return OperationResult<string>.Fail("تلاش ناموفق زیاد بود. ۱۵ دقیقه بعد دوباره تلاش کنید.");

        SignInPrincipal? principal = null;
        var configuredPassword = options.AdminPassword;
        if (configuredPassword.Length > 0 && ConstantTimeEquals(username, options.AdminUsername) && ConstantTimeEquals(password ?? string.Empty, configuredPassword))
        {
            principal = new SignInPrincipal("admin", "مدیر سامانه", "Admin", Permissions.All.Select(x => x.Key).ToList());
        }
        else
        {
            var key = username.ToLowerInvariant();
            await using var db = await factory.CreateDbContextAsync(ct);
            var staff = await db.StaffUsers.FirstOrDefaultAsync(x => x.Username == key && x.IsActive, ct);
            if (staff is not null && PasswordHasher.Verify(password ?? string.Empty, staff.PasswordHash))
            {
                staff.LastLoginAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                principal = new SignInPrincipal(staff.Id.ToString(), string.IsNullOrWhiteSpace(staff.DisplayName) ? staff.Username : staff.DisplayName,
                    "Staff", Permissions.Parse(staff.PermissionsCsv).ToList());
            }
        }

        if (principal is null)
        {
            cache.Set(failKey, fails + 1, TimeSpan.FromMinutes(15));
            await log.LogAsync(new ActorInfo(ActorType.Admin, "anonymous", username.Length > 40 ? username[..40] : username, ip), "admin.login-failed", null, userAgent, ct);
            return OperationResult<string>.Fail("نام کاربری یا رمز عبور اشتباه است.");
        }

        cache.Remove(failKey);
        await log.LogAsync(new ActorInfo(principal.Role == "Admin" ? ActorType.Admin : ActorType.Staff, principal.Id, principal.DisplayName, ip), "admin.login", null, userAgent, ct);
        return OperationResult<string>.Ok(IssueTicket(principal));
    }

    public async Task<bool> IsPrincipalValidAsync(string role, string id, CancellationToken ct = default)
    {
        if (role == "Admin") return true;
        if (!Guid.TryParse(id, out var guid)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        return role switch
        {
            "Customer" => await db.Users.AsNoTracking().AnyAsync(x => x.Id == guid && !x.IsBlocked, ct),
            "Staff" => await db.StaffUsers.AsNoTracking().AnyAsync(x => x.Id == guid && x.IsActive, ct),
            _ => false
        };
    }

    public async Task<IReadOnlyList<string>> GetStaffPermissionsAsync(string staffId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(staffId, out var guid)) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        var staff = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == guid, ct);
        return staff is null ? [] : Permissions.Parse(staff.PermissionsCsv).ToList();
    }

    // ---------------------------------------------------------------- tickets (one-time sign-in handoff to the cookie endpoint)
    private string IssueTicket(SignInPrincipal principal)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        cache.Set($"auth:ticket:{ticket}", principal, TimeSpan.FromMinutes(2));
        return ticket;
    }

    public SignInPrincipal? RedeemTicket(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 100) return null;
        var key = $"auth:ticket:{ticket}";
        if (!cache.TryGetValue(key, out SignInPrincipal? principal) || principal is null) return null;
        cache.Remove(key);
        return principal;
    }

    // ---------------------------------------------------------------- helpers
    private int Increment(string key, TimeSpan window)
    {
        var count = (cache.TryGetValue(key, out int current) ? current : 0) + 1;
        if (count == 1) cache.Set(key, count, window);
        else cache.Set(key, count, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = window });
        return count;
    }

    private void BumpAttempts(string key)
    {
        var count = (cache.TryGetValue(key, out int current) ? current : 0) + 1;
        cache.Set(key, count, TimeSpan.FromMinutes(10));
    }

    private static string Mask(string mobile) => mobile.Length == 11 ? $"{mobile[..4]}***{mobile[7..]}" : "***";

    private static bool ConstantTimeEquals(string a, string b)
    {
        var ha = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hb = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}
