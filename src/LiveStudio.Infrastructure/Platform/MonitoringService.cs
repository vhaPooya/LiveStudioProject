using System.Diagnostics;
using System.Text.Json;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.Platform;

public sealed class MonitoringService(
    IDbContextFactory<LiveStudioDbContext> factory,
    IHttpClientFactory httpFactory,
    ISettingsService settings,
    ISmsOtpClient sms,
    PlatformOptions options,
    ILogger<MonitoringService> logger) : IMonitoringService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SystemSnapshot? _last;

    public SystemSnapshot? LastSnapshot => _last;

    public async Task<SystemSnapshot> CheckNowAsync(bool raiseAlerts, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var services = new List<ServiceHealth>
            {
                await CheckDatabaseAsync(ct),
                await CheckApiAsync(ct),
                await CheckLiveKitAsync(ct),
                await CheckSmsAsync(ct),
                await CheckGatewayAsync(ct)
            };

            var minDiskGb = await settings.GetIntAsync(SettingKeys.MonitorMinDiskGb, 2, ct);
            long free = 0, total = 0;
            try
            {
                var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
                var drive = new DriveInfo(root);
                free = drive.AvailableFreeSpace / 1_073_741_824; total = drive.TotalSize / 1_073_741_824;
                services.Add(new ServiceHealth("disk", "فضای دیسک", free >= minDiskGb, $"{free} گیگابایت آزاد از {total}"));
            }
            catch (Exception) { services.Add(new ServiceHealth("disk", "فضای دیسک", true, "قابل اندازه‌گیری نیست", null, true)); }

            using var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / 1_048_576;
            services.Add(new ServiceHealth("memory", "حافظه فرایند وب", workingSetMb < 3000, $"{workingSetMb} مگابایت در حال استفاده"));

            var snapshot = new SystemSnapshot(DateTime.UtcNow, services, workingSetMb, free, total,
                DateTime.UtcNow - process.StartTime.ToUniversalTime(), process.Threads.Count);
            _last = snapshot;
            if (raiseAlerts) await ApplyAlertsAsync(services, ct);
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private async Task<ServiceHealth> CheckDatabaseAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var count = await db.Users.CountAsync(ct);
            return new ServiceHealth("database", "پایگاه داده SQLite", true, $"فعال - {count} کاربر ثبت‌شده", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ServiceHealth("database", "پایگاه داده SQLite", false, "خطا در اتصال: " + ex.GetType().Name, sw.ElapsedMilliseconds);
        }
    }

    private async Task<ServiceHealth> CheckApiAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await httpFactory.CreateClient("Monitor").GetAsync($"{options.ApiBaseUrl}/health", ct);
            return new ServiceHealth("api", "سرویس API یوتیوب", response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "پاسخ‌گو" : $"کد وضعیت {(int)response.StatusCode}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ServiceHealth("api", "سرویس API یوتیوب", false, "عدم پاسخ‌گویی: " + ex.GetType().Name, sw.ElapsedMilliseconds);
        }
    }

    private async Task<ServiceHealth> CheckLiveKitAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.ApiBaseUrl}/api/v1/livekit/status");
            request.Headers.Add("X-Internal-Key", options.InternalApiKey);
            using var response = await httpFactory.CreateClient("Monitor").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new ServiceHealth("livekit", "سرور LiveKit", false, $"وضعیت از API دریافت نشد ({(int)response.StatusCode})", sw.ElapsedMilliseconds);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var configured = root.TryGetProperty("configured", out var c) && c.GetBoolean();
            var reachable = root.TryGetProperty("reachable", out var r) && r.GetBoolean();
            var rooms = root.TryGetProperty("activeRooms", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            if (!configured) return new ServiceHealth("livekit", "سرور LiveKit", false, "کلیدهای LiveKit تنظیم نشده است", sw.ElapsedMilliseconds);
            return new ServiceHealth("livekit", "سرور LiveKit", reachable, reachable ? $"فعال - {rooms} اتاق باز" : "سرور LiveKit در دسترس نیست", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ServiceHealth("livekit", "سرور LiveKit", false, "عدم دسترسی به API: " + ex.GetType().Name, sw.ElapsedMilliseconds);
        }
    }

    private async Task<ServiceHealth> CheckSmsAsync(CancellationToken ct)
    {
        var configured = await sms.IsConfiguredAsync(ct);
        var enabled = await settings.GetBoolAsync(SettingKeys.SmsEnabled, true, ct);
        if (!enabled) return new ServiceHealth("sms", "پنل پیامک (OTPy)", false, "ارسال پیامک غیرفعال شده است");
        return new ServiceHealth("sms", "پنل پیامک (OTPy)", configured, configured ? "کلید API تنظیم شده است" : "کلید API ثبت نشده؛ ورود کاربران ممکن نیست");
    }

    private async Task<ServiceHealth> CheckGatewayAsync(CancellationToken ct)
    {
        var enabled = await settings.GetBoolAsync(SettingKeys.GatewayEnabled, false, ct);
        if (!enabled) return new ServiceHealth("gateway", "درگاه پرداخت", true, "غیرفعال (پرداخت آنلاین خاموش است)", null, true);
        var provider = await settings.GetAsync(SettingKeys.GatewayProvider, "none", ct);
        var hasMerchant = await settings.HasValueAsync(SettingKeys.GatewayMerchantId, ct);
        var ok = provider == "simulator" ? options.AllowPaymentSimulator : provider == "zarinpal" && hasMerchant;
        return new ServiceHealth("gateway", "درگاه پرداخت", ok, ok ? $"فعال ({provider})" : "درگاه فعال است اما تنظیمات ناقص است");
    }

    private async Task ApplyAlertsAsync(IReadOnlyList<ServiceHealth> services, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var open = await db.Alerts.Where(x => !x.IsResolved).ToListAsync(ct);
        foreach (var service in services)
        {
            if (service.Unknown) continue;
            var existing = open.Where(x => x.Source == service.Key).ToList();
            if (service.Healthy)
            {
                foreach (var alert in existing) { alert.IsResolved = true; alert.ResolvedAt = DateTime.UtcNow; alert.Message = (alert.Message ?? string.Empty) + " | بازیابی خودکار"; }
            }
            else if (existing.Count == 0)
            {
                var severity = service.Key is "database" or "api" or "livekit" ? AlertSeverity.Critical : AlertSeverity.Warning;
                db.Alerts.Add(new SystemAlert { Severity = severity, Source = service.Key, Title = $"مشکل در {service.Title}", Message = service.Detail });
                logger.LogWarning("Monitoring alert raised for {Service}: {Detail}", service.Key, service.Detail);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RaiseAsync(AlertSeverity severity, string source, string title, string? message, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var since = DateTime.UtcNow.AddMinutes(-10);
            var duplicate = await db.Alerts.AnyAsync(x => !x.IsResolved && x.Source == source && x.Title == title && x.At > since, ct);
            if (duplicate) return;
            db.Alerts.Add(new SystemAlert { Severity = severity, Source = source, Title = title, Message = message is { Length: > 1000 } ? message[..1000] : message });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { logger.LogWarning("Alert could not be stored: {Message}", ex.Message); }
    }

    public async Task<PagedResult<SystemAlert>> AlertsAsync(string? query, bool? resolved, AlertSeverity? severity, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Alerts.AsNoTracking().AsQueryable();
        if (resolved is not null) q = q.Where(x => x.IsResolved == resolved.Value);
        if (severity is not null) q = q.Where(x => x.Severity == severity.Value);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            q = q.Where(x => x.Title.Contains(term) || x.Source.Contains(term) || (x.Message != null && x.Message.Contains(term)));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.At).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<SystemAlert>(items, total, page, pageSize);
    }

    public async Task ResolveAsync(Guid alertId, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var alert = await db.Alerts.FirstOrDefaultAsync(x => x.Id == alertId, ct);
        if (alert is null || alert.IsResolved) return;
        alert.IsResolved = true; alert.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveAllAsync(ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var open = await db.Alerts.Where(x => !x.IsResolved).ToListAsync(ct);
        foreach (var alert in open) { alert.IsResolved = true; alert.ResolvedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> OpenAlertCountAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Alerts.AsNoTracking().CountAsync(x => !x.IsResolved, ct);
    }
}
