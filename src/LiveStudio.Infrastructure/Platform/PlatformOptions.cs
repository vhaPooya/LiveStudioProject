using Microsoft.Extensions.Configuration;

namespace LiveStudio.Infrastructure.Platform;

/// <summary>Typed view over the configuration values used by the platform layer.</summary>
public sealed class PlatformOptions(IConfiguration configuration)
{
    public string AdminUsername => configuration["Admin:Username"] is { Length: > 0 } u ? u : "admin";
    public string AdminPassword => configuration["Admin:Password"] ?? string.Empty;
    public string InternalApiKey => configuration["Platform:InternalApiKey"] ?? string.Empty;
    public string PublicBaseUrl => (configuration["Platform:PublicBaseUrl"] ?? "https://localhost:5200").TrimEnd('/');
    public string ApiBaseUrl => (configuration["Api:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
    public bool AllowPaymentSimulator => string.Equals(configuration["Platform:AllowPaymentSimulator"], "true", StringComparison.OrdinalIgnoreCase);

    public bool IsDevelopment =>
        string.Equals(configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>A fixed OTP accepted instead of an SMS - honoured ONLY when ASPNETCORE_ENVIRONMENT=Development.</summary>
    public string? DevOtpBypassCode => IsDevelopment && configuration["Otp:DevBypassCode"] is { Length: > 0 } c ? c : null;

    public string? OtpyApiKeyFallback => configuration["Otpy:ApiKey"];
    public string OtpyBaseUrl => (configuration["Otpy:BaseUrl"] is { Length: > 0 } b ? b : "https://api.otpy.ir").TrimEnd('/') + "/";
    public string OtpySendPath => configuration["Otpy:SendPath"] is { Length: > 0 } p ? p.TrimStart('/') : "v1/otp/send";
    public string OtpyVerifyPath => configuration["Otpy:VerifyPath"] is { Length: > 0 } p ? p.TrimStart('/') : "v1/otp/verify";
    /// <summary>Bearer (default), or the name of a header such as X-API-KEY.</summary>
    public string OtpyAuthHeader => configuration["Otpy:AuthHeader"] is { Length: > 0 } h ? h : "Bearer";
    /// <summary>Name of the JSON field that carries the mobile number (default "phone").</summary>
    public string OtpyPhoneField => configuration["Otpy:PhoneField"] is { Length: > 0 } f ? f : "phone";
}
