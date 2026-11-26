using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveStudio.Application.Platform;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.Platform;

/// <summary>
/// SMS one-time-password delivery through OTPy (https://otpy.ir). OTPy generates, sends and verifies the code itself:
/// POST /v1/otp/send {"phone"} and POST /v1/otp/verify {"phone","code"} -> {"verified": bool}.
/// The code is never stored or logged here.
/// </summary>
public sealed class OtpyClient(
    IHttpClientFactory httpFactory,
    ISettingsService settings,
    PlatformOptions options,
    ILogger<OtpyClient> logger) : ISmsOtpClient
{
    private HttpRequestMessage Build(string path, object body, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (string.Equals(options.OtpyAuthHeader, "Bearer", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        else
            request.Headers.TryAddWithoutValidation(options.OtpyAuthHeader, apiKey);
        return request;
    }

    private async Task<string> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { var t = await response.Content.ReadAsStringAsync(ct); return t.Length > 300 ? t[..300] : t; }
        catch { return string.Empty; }
    }

    private async Task<string?> ApiKeyAsync(CancellationToken ct)
    {
        var key = await settings.GetAsync(SettingKeys.SmsApiKey, ct);
        return string.IsNullOrWhiteSpace(key) ? options.OtpyApiKeyFallback : key;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => options.DevOtpBypassCode is not null || !string.IsNullOrWhiteSpace(await ApiKeyAsync(ct));

    public async Task<OperationResult> SendAsync(string mobile, CancellationToken ct = default)
    {
        if (options.DevOtpBypassCode is not null) return OperationResult.Ok();
        if (!await settings.GetBoolAsync(SettingKeys.SmsEnabled, true, ct))
            return OperationResult.Fail("ارسال پیامک موقتاً غیرفعال است. با پشتیبانی تماس بگیرید.");
        var apiKey = await ApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return OperationResult.Fail("سرویس پیامک پیکربندی نشده است. لطفاً بعداً تلاش کنید.");

        try
        {
            using var request = Build(options.OtpySendPath, new Dictionary<string, string> { [options.OtpyPhoneField] = mobile }, apiKey);
            using var response = await httpFactory.CreateClient("Otpy").SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return OperationResult.Ok();

            logger.LogWarning("OTPy send failed: {Status} {Url} {Body}", (int)response.StatusCode, response.RequestMessage?.RequestUri, await BodyAsync(response, ct));
            var body = await BodyAsync(response, ct);
            string? code = null; int? retryIn = null;
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.TryGetProperty("error", out var e)) code = e.GetString();
                if (d.RootElement.TryGetProperty("retry_in", out var r) && r.TryGetInt32(out var sec)) retryIn = sec;
            }
            catch (JsonException) { }
            return code switch
            {
                "bad_phone" => OperationResult.Fail("شماره موبایل نامعتبر است."),
                "unauthorized" => OperationResult.Fail("کلید API سرویس پیامک نامعتبر است. در تنظیمات مدیریت بررسی شود."),
                "insufficient_balance" => OperationResult.Fail("موجودی سرویس پیامک کافی نیست. به پشتیبانی اطلاع دهید."),
                "rate_limited" => OperationResult.Fail(retryIn is > 0 ? $"لطفاً {retryIn} ثانیه دیگر دوباره تلاش کنید." : "تعداد درخواست‌ها زیاد است؛ کمی بعد دوباره تلاش کنید."),
                "daily_limit" or "hourly_limit" or "key_limit" => OperationResult.Fail("سقف ارسال پیامک پر شده است. بعداً دوباره تلاش کنید."),
                "sms_unavailable" or "service_unavailable" => OperationResult.Fail("اپراتور پیامک موقتاً در دسترس نیست. کمی بعد دوباره تلاش کنید."),
                _ => OperationResult.Fail("ارسال پیامک ناموفق بود. چند لحظه بعد دوباره تلاش کنید.")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("OTPy send request failed: {Type} {Message}", ex.GetType().Name, ex.Message);
            return OperationResult.Fail("ارتباط با سرویس پیامک برقرار نشد. دوباره تلاش کنید.");
        }
    }

    public async Task<OperationResult<bool>> VerifyAsync(string mobile, string code, CancellationToken ct = default)
    {
        if (options.DevOtpBypassCode is { } bypass)
            return OperationResult<bool>.Ok(string.Equals(bypass, code, StringComparison.Ordinal));
        var apiKey = await ApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey)) return OperationResult<bool>.Fail("سرویس پیامک پیکربندی نشده است.");

        try
        {
            using var request = Build(options.OtpyVerifyPath, new Dictionary<string, string> { [options.OtpyPhoneField] = mobile, ["code"] = code }, apiKey);
            using var response = await httpFactory.CreateClient("Otpy").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return OperationResult<bool>.Fail("تعداد تلاش‌ها زیاد است؛ کمی بعد دوباره تلاش کنید.");
            if (!response.IsSuccessStatusCode)
            {
                // A rejected code may be reported as 4xx by the provider: treat as "not verified" rather than an outage.
                if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    return OperationResult<bool>.Ok(false);
                logger.LogWarning("OTPy verify failed: {Status} {Body}", (int)response.StatusCode, await BodyAsync(response, ct));
                return OperationResult<bool>.Fail("تأیید کد ناموفق بود. دوباره تلاش کنید.");
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var verified = doc.RootElement.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
            return OperationResult<bool>.Ok(verified);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("OTPy verify request failed: {Type} {Message}", ex.GetType().Name, ex.Message);
            return OperationResult<bool>.Fail("ارتباط با سرویس پیامک برقرار نشد. دوباره تلاش کنید.");
        }
    }
}
