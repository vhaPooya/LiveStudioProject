using System.Net.Http.Json;
using System.Text.Json;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.Platform;

public sealed class PaymentService(
    IDbContextFactory<LiveStudioDbContext> factory,
    ISettingsService settings,
    ISubscriptionService subscriptions,
    IActivityLogService log,
    IMonitoringService monitoring,
    IHttpClientFactory httpFactory,
    PlatformOptions options,
    ILogger<PaymentService> logger) : IPaymentService
{
    // ------------------------------------------------------------ gateway settings
    public async Task<GatewaySettings> GetGatewaySettingsAsync(CancellationToken ct = default) => new()
    {
        Provider = (await settings.GetAsync(SettingKeys.GatewayProvider, "none", ct)).ToLowerInvariant(),
        Enabled = await settings.GetBoolAsync(SettingKeys.GatewayEnabled, false, ct),
        HasMerchantId = await settings.HasValueAsync(SettingKeys.GatewayMerchantId, ct),
        Sandbox = await settings.GetBoolAsync(SettingKeys.GatewaySandbox, true, ct),
        Description = await settings.GetAsync(SettingKeys.GatewayDescription, "اشتراک LiveStudio", ct)
    };

    public async Task SaveGatewaySettingsAsync(GatewaySettings value, string? merchantIdOrNull, ActorInfo actor, CancellationToken ct = default)
    {
        var provider = value.Provider is "zarinpal" or "simulator" ? value.Provider : "none";
        await settings.SetAsync(SettingKeys.GatewayProvider, provider, false, ct);
        await settings.SetAsync(SettingKeys.GatewayEnabled, value.Enabled ? "true" : "false", false, ct);
        await settings.SetAsync(SettingKeys.GatewaySandbox, value.Sandbox ? "true" : "false", false, ct);
        await settings.SetAsync(SettingKeys.GatewayDescription, string.IsNullOrWhiteSpace(value.Description) ? "اشتراک LiveStudio" : value.Description.Trim(), false, ct);
        if (!string.IsNullOrWhiteSpace(merchantIdOrNull))
            await settings.SetAsync(SettingKeys.GatewayMerchantId, merchantIdOrNull.Trim(), true, ct);
        await log.LogAsync(actor, "gateway.settings-update", $"provider={provider}, enabled={value.Enabled}, sandbox={value.Sandbox}, merchantChanged={!string.IsNullOrWhiteSpace(merchantIdOrNull)}", null, ct);
    }

    public async Task<bool> IsCheckoutAvailableAsync(CancellationToken ct = default)
    {
        var cfg = await GetGatewaySettingsAsync(ct);
        if (!cfg.Enabled) return false;
        return cfg.Provider switch
        {
            "zarinpal" => cfg.HasMerchantId,
            "simulator" => options.AllowPaymentSimulator,
            _ => false
        };
    }

    // ------------------------------------------------------------ checkout
    public async Task<GatewayStartResult> StartCheckoutAsync(Guid userId, Guid planId, string? ip, string baseUrl, CancellationToken ct = default)
    {
        if (!await IsCheckoutAvailableAsync(ct)) return new(false, null, "پرداخت آنلاین در حال حاضر فعال نیست. از طریق تیکت با پشتیبانی هماهنگ کنید.");
        var cfg = await GetGatewaySettingsAsync(ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == planId && x.IsActive && !x.IsTrial, ct);
        if (user is null || user.IsBlocked) return new(false, null, "حساب کاربری مجاز به پرداخت نیست.");
        if (plan is null) return new(false, null, "طرح انتخاب‌شده در دسترس نیست.");
        if (plan.Price <= 0) return new(false, null, "این طرح رایگان است و از مسیر پرداخت قابل خرید نیست.");

        var last = await db.Payments.AsNoTracking().OrderByDescending(x => x.Number).Select(x => x.Number).FirstOrDefaultAsync(ct);
        var payment = new Payment
        {
            Number = Math.Max(last, 1000) + 1, UserId = userId, PlanId = plan.Id, PlanName = plan.Name, DurationDays = plan.DurationDays,
            Amount = plan.Price, Gateway = cfg.Provider, Status = PaymentStatus.Pending, Description = $"{cfg.Description} - {plan.Name}"
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        var callback = $"{baseUrl.TrimEnd('/')}/payment/callback";
        var request = await RequestAsync(cfg, payment, callback, user.Mobile, baseUrl, ct);
        if (!request.Ok)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = Truncate(request.Error, 500);
            await db.SaveChangesAsync(ct);
            await log.LogAsync(new ActorInfo(ActorType.Customer, userId.ToString(), user.FullName, ip), "payment.start-failed", $"#{payment.Number}: {request.Error}", null, ct);
            await monitoring.RaiseAsync(AlertSeverity.Warning, "payment", "خطا در شروع پرداخت", request.Error, ct);
            return new(false, null, "ارتباط با درگاه پرداخت برقرار نشد. دوباره تلاش کنید.", payment.Id);
        }

        payment.Authority = request.Authority;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(new ActorInfo(ActorType.Customer, userId.ToString(), user.FullName, ip), "payment.start", $"#{payment.Number} / {plan.Name} / {plan.Price:N0} تومان", null, ct);
        return new(true, request.PayUrl, null, payment.Id);
    }

    public async Task<PaymentCallbackResult> HandleCallbackAsync(string? authority, string? status, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authority)) return new(false, null, "شناسه پرداخت نامعتبر است.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.Authority == authority, ct);
        if (payment is null) return new(false, null, "پرداخت پیدا نشد.");
        if (payment.Status == PaymentStatus.Paid) return new(true, payment.Id, "پرداخت قبلاً با موفقیت ثبت شده است.", payment.RefId);
        if (payment.Status != PaymentStatus.Pending) return new(false, payment.Id, "این پرداخت دیگر قابل تأیید نیست.");

        var actor = new ActorInfo(ActorType.Customer, payment.UserId.ToString(), "کاربر", ip);
        if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            payment.Status = PaymentStatus.Cancelled;
            payment.FailureReason = "پرداخت توسط کاربر لغو یا ناموفق شد.";
            await db.SaveChangesAsync(ct);
            await log.LogAsync(actor, "payment.cancelled", $"#{payment.Number}", null, ct);
            return new(false, payment.Id, "پرداخت انجام نشد یا لغو شد.");
        }

        var cfg = await GetGatewaySettingsAsync(ct);
        var verify = await VerifyAsync(cfg, payment, ct);
        if (!verify.Ok)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = Truncate(verify.Error, 500);
            await db.SaveChangesAsync(ct);
            await log.LogAsync(actor, "payment.verify-failed", $"#{payment.Number}: {verify.Error}", null, ct);
            await monitoring.RaiseAsync(AlertSeverity.Warning, "payment", "تأیید پرداخت ناموفق بود", $"پرداخت #{payment.Number}: {verify.Error}", ct);
            return new(false, payment.Id, "تأیید پرداخت توسط درگاه انجام نشد. اگر مبلغ از حساب شما کسر شده، با پشتیبانی تماس بگیرید.");
        }

        payment.Status = PaymentStatus.Paid;
        payment.RefId = verify.RefId; payment.CardPan = verify.CardPan; payment.PaidAt = DateTime.UtcNow;
        payment.FailureReason = null;
        await db.SaveChangesAsync(ct);

        await subscriptions.GrantAsync(payment.UserId, payment.PlanId, payment.PlanName, payment.DurationDays, SubscriptionSource.Payment,
            $"پرداخت #{payment.Number}", new ActorInfo(ActorType.System, "payment", "پرداخت آنلاین"), ct);
        await log.LogAsync(actor, "payment.paid", $"#{payment.Number} / {payment.Amount:N0} تومان / مرجع {payment.RefId}", null, ct);
        return new(true, payment.Id, "پرداخت با موفقیت انجام شد و اشتراک شما فعال شد.", payment.RefId);
    }

    public async Task<Payment?> GetByAuthorityAsync(string authority, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Authority == authority, ct);
    }

    // ------------------------------------------------------------ queries
    public async Task<PagedResult<PaymentRow>> SearchAsync(Guid? userId, string? query, PaymentStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Payments.AsNoTracking().AsQueryable();
        if (userId is not null) q = q.Where(x => x.UserId == userId.Value);
        if (status is not null) q = q.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = IranianValidators.NormalizeDigits(query.Trim());
            var userIds = await db.Users.AsNoTracking().Where(u => u.FullName.Contains(term) || u.Mobile.Contains(term) || u.NationalCode.Contains(term))
                .Select(u => u.Id).ToListAsync(ct);
            var number = long.TryParse(term, out var n) ? n : -1;
            q = q.Where(x => userIds.Contains(x.UserId) || x.Number == number || x.PlanName.Contains(term)
                             || (x.Authority != null && x.Authority.Contains(term)) || (x.RefId != null && x.RefId.Contains(term)));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Select(x => x.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        var items = rows.Select(p =>
        {
            var u = users.FirstOrDefault(x => x.Id == p.UserId);
            return new PaymentRow(p, u?.FullName ?? "—", u?.Mobile ?? "—");
        }).ToList();
        return new PagedResult<PaymentRow>(items, total, page, pageSize);
    }

    public async Task<PaymentRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.UserId, ct);
        return new PaymentRow(p, u?.FullName ?? "—", u?.Mobile ?? "—");
    }

    public async Task<OperationResult> ManualMarkPaidAsync(Guid paymentId, string note, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == paymentId, ct);
        if (payment is null) return OperationResult.Fail("پرداخت پیدا نشد.");
        if (payment.Status == PaymentStatus.Paid) return OperationResult.Fail("این پرداخت قبلاً تأیید شده است.");
        if (string.IsNullOrWhiteSpace(note)) return OperationResult.Fail("توضیح یا شماره پیگیری دستی را وارد کنید.");
        payment.Status = PaymentStatus.Paid; payment.PaidAt = DateTime.UtcNow; payment.RefId = "MANUAL"; payment.FailureReason = Truncate(note.Trim(), 500);
        await db.SaveChangesAsync(ct);
        await subscriptions.GrantAsync(payment.UserId, payment.PlanId, payment.PlanName, payment.DurationDays, SubscriptionSource.Payment,
            $"تأیید دستی پرداخت #{payment.Number}", actor, ct);
        await log.LogAsync(actor, "payment.manual-paid", $"#{payment.Number}: {note}", null, ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> ManualCancelAsync(Guid paymentId, ActorInfo actor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == paymentId, ct);
        if (payment is null) return OperationResult.Fail("پرداخت پیدا نشد.");
        if (payment.Status != PaymentStatus.Pending) return OperationResult.Fail("فقط پرداخت‌های در انتظار قابل لغو هستند.");
        payment.Status = PaymentStatus.Cancelled; payment.FailureReason = "لغو توسط مدیر";
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "payment.manual-cancel", $"#{payment.Number}", null, ct);
        return OperationResult.Ok();
    }

    // ------------------------------------------------------------ gateways
    private sealed record GatewayRequest(bool Ok, string? Authority, string? PayUrl, string? Error);
    private sealed record GatewayVerify(bool Ok, string? RefId, string? CardPan, string? Error);

    private async Task<GatewayRequest> RequestAsync(GatewaySettings cfg, Payment payment, string callbackUrl, string mobile, string baseUrl, CancellationToken ct)
    {
        if (cfg.Provider == "simulator")
        {
            var authority = "SIM" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
            return new(true, authority, $"{baseUrl.TrimEnd('/')}/pay/simulate/{authority}", null);
        }
        var merchant = await settings.GetAsync(SettingKeys.GatewayMerchantId, ct);
        if (string.IsNullOrWhiteSpace(merchant)) return new(false, null, null, "شناسه پذیرنده درگاه تنظیم نشده است.");

        var host = cfg.Sandbox ? "https://sandbox.zarinpal.com" : "https://payment.zarinpal.com";
        try
        {
            var body = new
            {
                merchant_id = merchant,
                amount = payment.Amount * 10, // Toman -> Rial
                callback_url = callbackUrl,
                description = payment.Description ?? "LiveStudio",
                metadata = new { mobile }
            };
            using var response = await httpFactory.CreateClient("Gateway").PostAsJsonAsync($"{host}/pg/v4/payment/request.json", body, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("code", out var code) && code.GetInt32() == 100
                && data.TryGetProperty("authority", out var auth) && auth.GetString() is { Length: > 0 } authority)
                return new(true, authority, $"{host}/pg/StartPay/{authority}", null);
            return new(false, null, null, "پاسخ نامعتبر از درگاه: " + ExtractGatewayError(doc.RootElement));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Gateway request failed: {Type}", ex.GetType().Name);
            return new(false, null, null, "خطای ارتباط با درگاه: " + ex.GetType().Name);
        }
    }

    private async Task<GatewayVerify> VerifyAsync(GatewaySettings cfg, Payment payment, CancellationToken ct)
    {
        if (cfg.Provider == "simulator" || payment.Gateway == "simulator")
        {
            if (!options.AllowPaymentSimulator) return new(false, null, null, "شبیه‌ساز پرداخت غیرفعال است.");
            return new(true, "SIM-" + Random.Shared.Next(100000, 999999), "603799******1234", null);
        }
        var merchant = await settings.GetAsync(SettingKeys.GatewayMerchantId, ct);
        if (string.IsNullOrWhiteSpace(merchant)) return new(false, null, null, "شناسه پذیرنده درگاه تنظیم نشده است.");
        var host = cfg.Sandbox ? "https://sandbox.zarinpal.com" : "https://payment.zarinpal.com";
        try
        {
            var body = new { merchant_id = merchant, amount = payment.Amount * 10, authority = payment.Authority };
            using var response = await httpFactory.CreateClient("Gateway").PostAsJsonAsync($"{host}/pg/v4/payment/verify.json", body, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("code", out var code) && code.GetInt32() is 100 or 101)
            {
                var refId = data.TryGetProperty("ref_id", out var r) ? r.ToString() : null;
                var card = data.TryGetProperty("card_pan", out var c) ? c.GetString() : null;
                return new(true, refId, card, null);
            }
            return new(false, null, null, ExtractGatewayError(doc.RootElement));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, null, null, "خطای ارتباط با درگاه: " + ex.GetType().Name);
        }
    }

    private static string ExtractGatewayError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors))
        {
            if (errors.ValueKind == JsonValueKind.Object && errors.TryGetProperty("message", out var m)) return m.GetString() ?? "خطای نامشخص";
            if (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0) return errors[0].ToString();
        }
        return "خطای نامشخص";
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
