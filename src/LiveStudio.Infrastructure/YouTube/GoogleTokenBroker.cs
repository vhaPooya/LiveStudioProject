using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveStudio.Domain.Entities;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.YouTube;

public sealed record StoredTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt,
    string GoogleSubject, string ChannelId, string ChannelTitle, string? ThumbnailUrl);

public sealed record GoogleChannelInfo(string Id, string Title, string? ThumbnailUrl);
public sealed record GoogleTokenResult(string AccessToken, string? RefreshToken, int ExpiresIn);

/// <summary>
/// Per-tenant Google credential handling: client secret + token storage (encrypted with a tenant-specific
/// Data Protection sub-key), token refresh and the Google identity / channel calls used while connecting.
/// </summary>
public sealed class GoogleTokenBroker(
    IDbContextFactory<LiveStudioDbContext> factory,
    IDataProtectionProvider protection,
    IHttpClientFactory httpFactory)
{
    private readonly IDataProtector _tokenProtector = protection.CreateProtector("LiveStudio.GoogleOAuth.Token.v2");
    private readonly IDataProtector _secretProtector = protection.CreateProtector("LiveStudio.GoogleOAuth.ClientSecret.v2");

    private static string Key(Guid tenantId) => tenantId.ToString("N");

    // ---------------------------------------------------------------- client credentials
    public string ProtectSecret(Guid tenantId, string secret) => _secretProtector.CreateProtector(Key(tenantId)).Protect(secret);

    public async Task<(string ClientId, string ClientSecret)?> GetClientAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TenantOAuth.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == tenantId, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.ClientId) || string.IsNullOrWhiteSpace(row.ProtectedClientSecret)) return null;
        try { return (row.ClientId, _secretProtector.CreateProtector(Key(tenantId)).Unprotect(row.ProtectedClientSecret)); }
        catch (CryptographicException) { return null; }
    }

    // ---------------------------------------------------------------- token storage
    public async Task<StoredTokens?> LoadTokensAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = Key(tenantId);
        var row = await db.GoogleCredentials.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<StoredTokens>(_tokenProtector.CreateProtector(key).Unprotect(row.ProtectedTokenPayload)); }
        catch (CryptographicException) { throw new InvalidOperationException("اطلاعات ذخیره‌شده اتصال گوگل قابل رمزگشایی نیست. کلیدهای Data Protection باید پایدار و مشترک باشند؛ اتصال را دوباره برقرار کنید."); }
    }

    public async Task SaveTokensAsync(Guid tenantId, StoredTokens tokens, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = Key(tenantId);
        var payload = _tokenProtector.CreateProtector(key).Protect(JsonSerializer.Serialize(tokens));
        var row = await db.GoogleCredentials.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) db.GoogleCredentials.Add(new GoogleOAuthCredential { Key = key, ProtectedTokenPayload = payload });
        else { row.ProtectedTokenPayload = payload; row.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteTokensAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = Key(tenantId);
        var row = await db.GoogleCredentials.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) return;
        db.GoogleCredentials.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- access token
    public async Task<string> GetValidAccessTokenAsync(Guid tenantId, CancellationToken ct)
    {
        var client = await GetClientAsync(tenantId, ct)
            ?? throw new InvalidOperationException("تنظیمات Google OAuth شما کامل نیست. از «تنظیمات › Google OAuth» اطلاعات را ثبت کنید.");
        var tokens = await LoadTokensAsync(tenantId, ct)
            ?? throw new InvalidOperationException("کانال یوتیوب متصل نیست. از «تنظیمات › Google OAuth» اتصال را انجام دهید.");
        if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return tokens.AccessToken;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
                ["refresh_token"] = tokens.RefreshToken, ["grant_type"] = "refresh_token"
            })
        };
        using var response = await httpFactory.CreateClient("GoogleOAuth").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
            {
                await MarkErrorAsync(tenantId, "دسترسی گوگل لغو یا منقضی شده است یا اطلاعات Client نامعتبر است. اتصال را دوباره برقرار کنید.", ct);
                throw new InvalidOperationException("دسترسی به حساب گوگل منقضی یا لغو شده است. از تنظیمات Google OAuth دوباره متصل شوید.");
            }
            throw new InvalidOperationException($"تمدید توکن گوگل ناموفق بود ({(int)response.StatusCode}).");
        }
        var refreshed = await response.Content.ReadFromJsonAsync<TokenDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("پاسخ تمدید توکن گوگل نامعتبر است.");
        tokens = tokens with { AccessToken = refreshed.AccessToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, refreshed.ExpiresIn)) };
        await SaveTokensAsync(tenantId, tokens, ct);
        return tokens.AccessToken;
    }

    public async Task MarkErrorAsync(Guid tenantId, string message, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TenantOAuth.FirstOrDefaultAsync(x => x.UserId == tenantId, ct);
        if (row is null) return;
        row.State = OAuthConnectionState.Error; row.LastError = message.Length > 600 ? message[..600] : message; row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- connect flow calls
    public async Task<GoogleTokenResult> ExchangeCodeAsync(string clientId, string clientSecret, string redirectUri, string code, string verifier, CancellationToken ct)
    {
        using var response = await httpFactory.CreateClient("GoogleOAuth").PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code, ["client_id"] = clientId, ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
            }), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var reason = body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase) ? "Client ID یا Client Secret از نظر گوگل نامعتبر است."
                : body.Contains("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase) ? "Redirect URI ثبت‌شده در Google Cloud با آدرس نرم‌افزار یکی نیست."
                : body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ? "کد تأیید نامعتبر یا منقضی شده است؛ دوباره تلاش کنید."
                : $"تبادل کد با گوگل ناموفق بود ({(int)response.StatusCode}).";
            throw new InvalidOperationException(reason);
        }
        var token = await response.Content.ReadFromJsonAsync<TokenDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("پاسخ توکن گوگل نامعتبر است.");
        return new GoogleTokenResult(token.AccessToken, token.RefreshToken, token.ExpiresIn);
    }

    public async Task<(string Sub, string Email)> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpFactory.CreateClient("GoogleOAuth").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("دریافت مشخصات حساب گوگل ناموفق بود.");
        var info = await response.Content.ReadFromJsonAsync<UserInfoDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("پاسخ مشخصات گوگل نامعتبر است.");
        return (info.Sub, info.Email ?? string.Empty);
    }

    public async Task<GoogleChannelInfo> GetMyChannelAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet%2Cid&mine=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpFactory.CreateClient("YouTube").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(body.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase) || body.Contains("has not been used", StringComparison.OrdinalIgnoreCase)
                ? "YouTube Data API v3 در پروژه Google Cloud شما فعال نیست."
                : response.StatusCode == System.Net.HttpStatusCode.Forbidden ? "گوگل دسترسی به کانال یوتیوب را رد کرد (مجوزها یا سهمیه API را بررسی کنید)."
                : $"دریافت کانال یوتیوب ناموفق بود ({(int)response.StatusCode}).");
        }
        var data = await response.Content.ReadFromJsonAsync<ChannelListDto>(cancellationToken: ct) ?? new ChannelListDto();
        var item = data.Items.FirstOrDefault() ?? throw new InvalidOperationException("برای این حساب گوگل کانال یوتیوب پیدا نشد. ابتدا کانال بسازید.");
        return new GoogleChannelInfo(item.Id, item.Snippet.Title, item.Snippet.Thumbnails?.Default?.Url);
    }

    // ---------------------------------------------------------------- Google Drive (scope drive.file: only files this app creates)
    public const string DriveFolderName = "LiveStudio Media";

    /// <summary>Finds or creates the app folder. A 403 here means the Drive API / scope was not granted.</summary>
    public async Task<(string Id, string Name)> EnsureDriveFolderAsync(string accessToken, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("GoogleOAuth");
        var q = Uri.EscapeDataString($"name = '{DriveFolderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false");
        using (var find = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files?q={q}&fields=files(id,name)&pageSize=1"))
        {
            find.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var r = await http.SendAsync(find, ct);
            if (r.StatusCode == System.Net.HttpStatusCode.Forbidden || r.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("دسترسی Google Drive داده نشد. «Google Drive API» را در پروژه‌ی Google Cloud خود فعال کنید و در صفحه‌ی گوگل دسترسی Drive را تأیید کنید.");
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"اتصال به Google Drive ناموفق بود ({(int)r.StatusCode}).");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                return (files[0].GetProperty("id").GetString()!, DriveFolderName);
        }
        using var create = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id")
        {
            Content = JsonContent.Create(new { name = DriveFolderName, mimeType = "application/vnd.google-apps.folder" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var cr = await http.SendAsync(create, ct);
        if (!cr.IsSuccessStatusCode) throw new InvalidOperationException($"ساخت پوشه در Google Drive ناموفق بود ({(int)cr.StatusCode}).");
        using var cdoc = JsonDocument.Parse(await cr.Content.ReadAsStringAsync(ct));
        return (cdoc.RootElement.GetProperty("id").GetString()!, DriveFolderName);
    }

    public Task RevokeAsync(string token, CancellationToken ct)
        => httpFactory.CreateClient("GoogleOAuth").PostAsync("https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
    private sealed class UserInfoDto { [JsonPropertyName("sub")] public string Sub { get; set; } = string.Empty; [JsonPropertyName("email")] public string? Email { get; set; } }
    private sealed class ChannelListDto { [JsonPropertyName("items")] public List<ChannelDto> Items { get; set; } = []; }
    private sealed class ChannelDto { [JsonPropertyName("id")] public string Id { get; set; } = string.Empty; [JsonPropertyName("snippet")] public SnippetDto Snippet { get; set; } = new(); }
    private sealed class SnippetDto { [JsonPropertyName("title")] public string Title { get; set; } = string.Empty; [JsonPropertyName("thumbnails")] public ThumbsDto? Thumbnails { get; set; } }
    private sealed class ThumbsDto { [JsonPropertyName("default")] public ThumbDto? Default { get; set; } }
    private sealed class ThumbDto { [JsonPropertyName("url")] public string? Url { get; set; } }
}
