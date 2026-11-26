using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Entities;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using LiveStudio.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LiveStudio.Infrastructure.YouTube;

/// <summary>
/// Per-customer Google OAuth (BYO client). The software never creates or changes the customer's Google Cloud
/// project; it validates and stores what the customer registers, then runs the standard authorization-code + PKCE flow.
/// </summary>
public sealed partial class GoogleOAuthTenantService(
    IDbContextFactory<LiveStudioDbContext> factory,
    GoogleTokenBroker broker,
    ISettingsService settings,
    IActivityLogService log,
    IMemoryCache cache,
    PlatformOptions options) : IGoogleOAuthTenantService
{
    private static readonly string[] Scopes =
    [
        "openid", "email", "profile",
        "https://www.googleapis.com/auth/youtube.readonly",
        "https://www.googleapis.com/auth/youtube"
    ];

    [GeneratedRegex(@"^\d{6,}-[a-z0-9]{8,}\.apps\.googleusercontent\.com$", RegexOptions.IgnoreCase)] private static partial Regex ClientIdRegex();
    [GeneratedRegex(@"^[a-z][a-z0-9-]{4,28}[a-z0-9]$")] private static partial Regex ProjectIdRegex();

    private const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    private sealed record Pending(Guid UserId, string Verifier, string RedirectUri, bool Drive);

    public async Task<string> GetSharedRedirectUriAsync(CancellationToken ct = default)
    {
        var custom = await settings.GetAsync(SettingKeys.GoogleRedirectUri, ct);
        return string.IsNullOrWhiteSpace(custom) ? $"{options.PublicBaseUrl}/oauth/google/callback" : custom.Trim();
    }

    public async Task<GoogleOAuthSettingsView> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TenantOAuth.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var shared = await GetSharedRedirectUriAsync(ct);
        var video = await settings.GetAsync(SettingKeys.TutorialVideoUrl, ct);
        if (row is null)
            return new GoogleOAuthSettingsView(false, string.Empty, false, string.Empty, shared, OAuthConnectionState.NotConfigured, null, null, null, null, null, null, null, null, video);
        return new GoogleOAuthSettingsView(true, row.ClientId, !string.IsNullOrEmpty(row.ProtectedClientSecret), row.ProjectId, shared, row.State, row.LastError,
            row.GoogleEmail, row.ChannelId, row.ChannelTitle, row.ChannelThumbnailUrl, row.ValidatedAt, row.ConnectedAt, row.LastTestAt, video);
    }

    // ------------------------------------------------ steps 1-2: validate + store
    public async Task<OperationResult> SaveAsync(Guid userId, GoogleOAuthSaveRequest request, ActorInfo actor, CancellationToken ct = default)
    {
        var clientId = (request.ClientId ?? string.Empty).Trim();
        var secret = (request.ClientSecret ?? string.Empty).Trim();
        var projectId = (request.ProjectId ?? string.Empty).Trim();
        var shared = await GetSharedRedirectUriAsync(ct);

        var errors = new List<string>();
        if (!ClientIdRegex().IsMatch(clientId)) errors.Add("فرمت Client ID صحیح نیست (باید مانند 123456789-abcdef.apps.googleusercontent.com باشد).");
        if (!ProjectIdRegex().IsMatch(projectId)) errors.Add("Project ID نامعتبر است (۶ تا ۳۰ نویسه؛ حروف کوچک انگلیسی، عدد و خط تیره).");
        if (!IsSecureRedirect(shared)) errors.Add("Redirect URI نرم‌افزار باید HTTPS باشد (فقط localhost در محیط توسعه مجاز است). با مدیر سامانه تماس بگیرید.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TenantOAuth.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var hasStoredSecret = row is not null && !string.IsNullOrEmpty(row.ProtectedClientSecret);
        if (secret.Length == 0 && !hasStoredSecret) errors.Add("Client Secret را وارد کنید.");
        else if (secret.Length > 0 && (secret.Length < 16 || secret.Any(char.IsWhiteSpace))) errors.Add("Client Secret نامعتبر است (کوتاه است یا فاصله دارد).");
        if (await db.TenantOAuth.AnyAsync(x => x.UserId != userId && x.ClientId == clientId, ct))
            errors.Add("این Client ID قبلاً توسط حساب کاربری دیگری ثبت شده است.");
        if (errors.Count > 0) return OperationResult.Fail(string.Join(" ", errors));

        var clientChanged = row is not null && !string.Equals(row.ClientId, clientId, StringComparison.Ordinal);
        if (row is null)
        {
            row = new TenantGoogleOAuth { UserId = userId };
            db.TenantOAuth.Add(row);
        }
        row.ClientId = clientId; row.ProjectId = projectId; row.RedirectUri = shared;
        if (secret.Length > 0) row.ProtectedClientSecret = broker.ProtectSecret(userId, secret);
        if (clientChanged || secret.Length > 0 || row.State is OAuthConnectionState.NotConfigured)
        {
            // New credentials invalidate any previous connection.
            row.State = OAuthConnectionState.Saved; row.LastError = null; row.GoogleEmail = null; row.ChannelId = null;
            row.ChannelTitle = null; row.ChannelThumbnailUrl = null; row.ConnectedAt = null;
            await broker.DeleteTokensAsync(userId, ct);
        }
        row.ValidatedAt = DateTime.UtcNow; row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await log.LogAsync(actor, "oauth.saved", $"ClientId={Mask(clientId)}, Project={projectId}", null, ct);
        return OperationResult.Ok();
    }

    // ------------------------------------------------ step 3: authorization request
    public async Task<OperationResult<string>> BeginAuthorizationAsync(Guid userId, ActorInfo actor, CancellationToken ct = default, bool includeDrive = false)
    {
        var client = await broker.GetClientAsync(userId, ct);
        if (client is null) return OperationResult<string>.Fail("ابتدا Client ID و Client Secret را ذخیره کنید.");
        var redirect = await GetSharedRedirectUriAsync(ct);
        if (!IsSecureRedirect(redirect)) return OperationResult<string>.Fail("Redirect URI نرم‌افزار باید HTTPS باشد.");

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        cache.Set($"google-oauth:{state}", new Pending(userId, verifier, redirect, includeDrive), TimeSpan.FromMinutes(10));

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.TenantOAuth.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (row is not null) { row.State = OAuthConnectionState.AwaitingConsent; row.LastError = null; row.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        }

        var query = new Dictionary<string, string>
        {
            ["client_id"] = client.Value.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = string.Join(' ', includeDrive ? Scopes.Append(DriveScope) : Scopes), ["access_type"] = "offline", ["prompt"] = "consent select_account",
            ["include_granted_scopes"] = "true", ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        var url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        await log.LogAsync(actor, "oauth.begin", null, null, ct);
        return OperationResult<string>.Ok(url);
    }

    // ------------------------------------------------ steps 5-7: callback, token exchange, connection test, activation
    public async Task<OAuthCallbackResult> CompleteAuthorizationAsync(string? code, string? state, string? error, Guid? expectedUserId, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state) || !cache.TryGetValue($"google-oauth:{state}", out Pending? pending) || pending is null)
            return new(false, null, "درخواست OAuth نامعتبر یا منقضی شده است. فرآیند اتصال را دوباره شروع کنید.");
        cache.Remove($"google-oauth:{state}");
        var actor = new ActorInfo(ActorType.Customer, pending.UserId.ToString(), "مشتری", ip);

        if (expectedUserId is not null && expectedUserId.Value != pending.UserId)
            return new(false, null, "نشست کاربری با درخواست اتصال مطابقت ندارد.");

        if (!string.IsNullOrWhiteSpace(error))
        {
            var message = error == "access_denied" ? "دسترسی در صفحه گوگل تأیید نشد." : $"گوگل خطا برگرداند: {error}";
            await broker.MarkErrorAsync(pending.UserId, message, ct);
            await log.LogAsync(actor, "oauth.denied", error, null, ct);
            return new(false, pending.UserId, message);
        }
        if (string.IsNullOrWhiteSpace(code)) { await broker.MarkErrorAsync(pending.UserId, "کد تأیید از گوگل دریافت نشد.", ct); return new(false, pending.UserId, "کد تأیید از گوگل دریافت نشد."); }

        try
        {
            var client = await broker.GetClientAsync(pending.UserId, ct) ?? throw new InvalidOperationException("اطلاعات Client یافت نشد؛ دوباره ذخیره کنید.");
            var token = await broker.ExchangeCodeAsync(client.ClientId, client.ClientSecret, pending.RedirectUri, code, pending.Verifier, ct);
            var previous = await broker.LoadTokensAsync(pending.UserId, ct);
            var refresh = !string.IsNullOrWhiteSpace(token.RefreshToken) ? token.RefreshToken : previous?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refresh))
                throw new InvalidOperationException("گوگل Refresh Token برنگرداند. دسترسی برنامه را در حساب گوگل لغو کنید و دوباره متصل شوید.");

            var user = await broker.GetUserInfoAsync(token.AccessToken, ct);
            var channel = await broker.GetMyChannelAsync(token.AccessToken, ct);

            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.TenantOAuth.AnyAsync(x => x.UserId != pending.UserId && x.ChannelId == channel.Id && x.State == OAuthConnectionState.Connected, ct))
                throw new InvalidOperationException("این کانال یوتیوب قبلاً توسط حساب کاربری دیگری متصل شده است.");

            await broker.SaveTokensAsync(pending.UserId, new StoredTokens(token.AccessToken, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
                user.Sub, channel.Id, channel.Title, channel.ThumbnailUrl), ct);

            var row = await db.TenantOAuth.FirstAsync(x => x.UserId == pending.UserId, ct);
            row.State = OAuthConnectionState.Connected; row.LastError = null; row.GoogleEmail = user.Email;
            row.ChannelId = channel.Id; row.ChannelTitle = channel.Title; row.ChannelThumbnailUrl = channel.ThumbnailUrl;
            row.ConnectedAt = DateTime.UtcNow; row.LastTestAt = DateTime.UtcNow; row.UpdatedAt = DateTime.UtcNow;

            var existing = await db.Channels.FirstOrDefaultAsync(x => x.ChannelId == channel.Id, ct);
            if (existing is null)
                db.Channels.Add(new YouTubeChannel { GoogleSubject = user.Sub, ChannelId = channel.Id, Title = channel.Title, ThumbnailUrl = channel.ThumbnailUrl, IsActive = true });
            else { existing.GoogleSubject = user.Sub; existing.Title = channel.Title; existing.ThumbnailUrl = channel.ThumbnailUrl; existing.IsActive = true; }
            await db.SaveChangesAsync(ct);

            if (pending.Drive)
            {
                var folder = await broker.EnsureDriveFolderAsync(token.AccessToken, ct);
                var link = await db.DriveLinks.FirstOrDefaultAsync(x => x.UserId == pending.UserId, ct);
                if (link is null) { link = new DriveLink { UserId = pending.UserId }; db.DriveLinks.Add(link); }
                link.Enabled = true; link.FolderId = folder.Id; link.FolderName = folder.Name; link.Email = user.Email; link.ConnectedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await log.LogAsync(actor, "drive.connected", null, null, ct);
                return new(true, pending.UserId, "Google Drive متصل شد؛ ویدئوهای Media Box در پوشه‌ی «LiveStudio Media» ذخیره می‌شوند.", true);
            }
            await log.LogAsync(actor, "oauth.connected", $"کانال: {channel.Title}", null, ct);
            return new(true, pending.UserId, $"کانال «{channel.Title}» با موفقیت متصل شد.");
        }
        catch (InvalidOperationException ex)
        {
            await broker.MarkErrorAsync(pending.UserId, ex.Message, ct);
            await log.LogAsync(actor, "oauth.failed", ex.Message, null, ct);
            return new(false, pending.UserId, ex.Message, pending.Drive);
        }
    }

    public async Task<OperationResult> TestConnectionAsync(Guid userId, ActorInfo actor, CancellationToken ct = default)
    {
        try
        {
            var access = await broker.GetValidAccessTokenAsync(userId, ct);
            var channel = await broker.GetMyChannelAsync(access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.TenantOAuth.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (row is not null)
            {
                row.State = OAuthConnectionState.Connected; row.LastError = null; row.LastTestAt = DateTime.UtcNow;
                row.ChannelId = channel.Id; row.ChannelTitle = channel.Title; row.ChannelThumbnailUrl = channel.ThumbnailUrl; row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            await log.LogAsync(actor, "oauth.test-ok", null, null, ct);
            return OperationResult.Ok();
        }
        catch (InvalidOperationException ex)
        {
            await broker.MarkErrorAsync(userId, ex.Message, ct);
            await log.LogAsync(actor, "oauth.test-failed", ex.Message, null, ct);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> DisconnectAsync(Guid userId, bool removeCredentials, ActorInfo actor, CancellationToken ct = default)
    {
        try
        {
            var tokens = await broker.LoadTokensAsync(userId, ct);
            if (tokens is not null) { try { await broker.RevokeAsync(tokens.RefreshToken, ct); } catch (Exception) { /* best effort */ } }
        }
        catch (InvalidOperationException) { /* undecryptable tokens are simply dropped */ }
        await broker.DeleteTokensAsync(userId, ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TenantOAuth.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is not null)
        {
            if (removeCredentials) db.TenantOAuth.Remove(row);
            else
            {
                row.State = OAuthConnectionState.Saved; row.GoogleEmail = null; row.ChannelId = null; row.ChannelTitle = null;
                row.ChannelThumbnailUrl = null; row.ConnectedAt = null; row.LastError = null; row.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        await log.LogAsync(actor, removeCredentials ? "oauth.removed" : "oauth.disconnected", null, null, ct);
        return OperationResult.Ok();
    }

    private static bool IsSecureRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme == Uri.UriSchemeHttps) return true;
        return parsed.Scheme == Uri.UriSchemeHttp && (parsed.Host is "localhost" or "127.0.0.1");
    }

    private static string Mask(string clientId) => clientId.Length > 14 ? clientId[..8] + "…" + clientId[^26..] : clientId;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
