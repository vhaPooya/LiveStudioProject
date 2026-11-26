using LiveStudio.Domain.Platform;

namespace LiveStudio.Application.Platform;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public static PagedResult<T> Empty(int page = 1, int pageSize = 10) => new([], 0, page, pageSize);
}

public sealed record OperationResult(bool Success, string? Error = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

public sealed record OperationResult<T>(bool Success, T? Value, string? Error = null)
{
    public static OperationResult<T> Ok(T value) => new(true, value);
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}

public sealed record ActorInfo(ActorType Type, string Id, string Name, string? Ip = null)
{
    public static ActorInfo System { get; } = new(ActorType.System, "system", "سیستم");
}

/// <summary>Identifies the tenant (customer) that the current request / circuit acts for.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    void Set(Guid? tenantId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public void Set(Guid? tenantId) => TenantId = tenantId;
}

public static class SettingKeys
{
    // SMS (OTPy)
    public const string SmsApiKey = "sms.otpy.apiKey";
    public const string SmsEnabled = "sms.enabled";
    // General
    public const string SiteName = "site.name";
    public const string HeroTitle = "site.hero.title";
    public const string HeroSubtitle = "site.hero.subtitle";
    public const string AnnouncementBar = "site.announcement";
    public const string SupportPhone = "site.support.phone";
    public const string SupportEmail = "site.support.email";
    public const string SupportTelegram = "site.support.telegram";
    public const string FooterText = "site.footer";
    public const string TutorialVideoUrl = "oauth.tutorial.video";
    public const string TermsText = "site.terms";
    // Google OAuth (shared redirect URI of the software)
    public const string GoogleRedirectUri = "oauth.redirectUri";
    // Subscriptions
    public const string TrialDays = "subscription.trialDays";
    public const string RegistrationOpen = "auth.registrationOpen";
    // Payment gateway
    public const string GatewayProvider = "payment.provider";      // zarinpal | simulator | none
    public const string GatewayEnabled = "payment.enabled";
    public const string GatewayMerchantId = "payment.merchantId";
    public const string GatewaySandbox = "payment.sandbox";
    public const string GatewayDescription = "payment.description";
    // Monitoring
    public const string MonitorEnabled = "monitor.enabled";
    public const string MonitorIntervalSeconds = "monitor.interval";
    public const string MonitorMinDiskGb = "monitor.minDiskGb";
}

public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<string> GetAsync(string key, string defaultValue, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default);
    Task<bool> HasValueAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, bool isSecret = false, CancellationToken ct = default);
}

// ------------------------------------------------------------------ SMS / Auth
public interface ISmsOtpClient
{
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    Task<OperationResult> SendAsync(string mobile, CancellationToken ct = default);
    Task<OperationResult<bool>> VerifyAsync(string mobile, string code, CancellationToken ct = default);
}

public enum AuthMode { Login, Register }

public sealed record OtpSendResult(bool Success, string? Error, int CooldownSeconds = 0);
public sealed record OtpVerifyResult(bool Success, string? Error, string? Ticket);
public sealed record SignInPrincipal(string Id, string DisplayName, string Role, IReadOnlyList<string> Permissions);

public interface IAuthService
{
    Task<OtpSendResult> SendOtpAsync(AuthMode mode, string nationalCode, string mobile, string? fullName, string? ip, CancellationToken ct = default);
    Task<OtpVerifyResult> VerifyOtpAsync(AuthMode mode, string nationalCode, string mobile, string code, string? fullName, string? ip, string? userAgent, CancellationToken ct = default);
    /// <summary>One-time redemption of the sign-in ticket created after a successful OTP / admin login.</summary>
    SignInPrincipal? RedeemTicket(string ticket);
    Task<OperationResult<string>> AdminLoginAsync(string username, string password, string? ip, string? userAgent, CancellationToken ct = default);
    Task<bool> IsPrincipalValidAsync(string role, string id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetStaffPermissionsAsync(string staffId, CancellationToken ct = default);
}

public static class IranianValidators
{
    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var chars = value.Trim().Select(c => c switch
        {
            >= '\u06F0' and <= '\u06F9' => (char)('0' + (c - '\u06F0')),
            >= '\u0660' and <= '\u0669' => (char)('0' + (c - '\u0660')),
            _ => c
        });
        return new string(chars.ToArray());
    }

    public static string? NormalizeMobile(string? value)
    {
        var digits = new string(NormalizeDigits(value).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0098")) digits = "0" + digits[4..];
        else if (digits.StartsWith("98") && digits.Length == 12) digits = "0" + digits[2..];
        else if (digits.Length == 10 && digits.StartsWith('9')) digits = "0" + digits;
        return digits.Length == 11 && digits.StartsWith("09") ? digits : null;
    }

    public static bool IsValidNationalCode(string? value)
    {
        var code = NormalizeDigits(value);
        if (code.Length != 10 || !code.All(char.IsDigit)) return false;
        if (code.Distinct().Count() == 1) return false;
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (code[i] - '0') * (10 - i);
        var remainder = sum % 11;
        var check = code[9] - '0';
        return remainder < 2 ? check == remainder : check == 11 - remainder;
    }
}

// ------------------------------------------------------------------ Users
public sealed record UserListItem(Guid Id, string FullName, string NationalCode, string Mobile, bool IsBlocked,
    DateTime CreatedAt, DateTime? LastLoginAt, string? PlanName, DateTime? SubscriptionEndsAt, OAuthConnectionState OAuthState);

public interface IUserService
{
    Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<UserListItem>> SearchAsync(string? query, string? filter, int page, int pageSize, CancellationToken ct = default);
    Task<OperationResult> UpdateProfileAsync(Guid id, string fullName, string? email, string? channelName, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> AdminUpdateAsync(Guid id, string fullName, string nationalCode, string mobile, string? email, string? note, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> SetBlockedAsync(Guid id, bool blocked, ActorInfo actor, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Subscriptions & plans
public sealed record SubscriptionInfo(bool IsActive, string PlanName, DateTime? StartsAt, DateTime? EndsAt, int DaysLeft, int TotalDays, SubscriptionSource? Source)
{
    public int Percent => TotalDays <= 0 ? 0 : Math.Clamp((int)Math.Round(DaysLeft * 100.0 / TotalDays), 0, 100);
    public static SubscriptionInfo None { get; } = new(false, "بدون اشتراک", null, null, 0, 0, null);
}

public interface ISubscriptionService
{
    Task<SubscriptionInfo> GetCurrentAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasActiveAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSubscription>> HistoryAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(bool onlyActive, CancellationToken ct = default);
    Task<SubscriptionPlan?> GetPlanAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> SavePlanAsync(SubscriptionPlan plan, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> DeletePlanAsync(Guid id, ActorInfo actor, CancellationToken ct = default);
    /// <summary>Adds time to the user's subscription; extends from the current end date when still active.</summary>
    Task GrantAsync(Guid userId, Guid? planId, string planName, int days, SubscriptionSource source, string? note, ActorInfo actor, CancellationToken ct = default);
    /// <summary>Manual override by an administrator: replaces the current subscription with the given plan and end date.</summary>
    Task<OperationResult> AdminSetAsync(Guid userId, Guid? planId, string planName, DateTime endsAtUtc, string? note, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> AdminCancelAsync(Guid userId, ActorInfo actor, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Payments
public sealed record GatewayStartResult(bool Success, string? RedirectUrl, string? Error, Guid? PaymentId = null);
public sealed record PaymentCallbackResult(bool Success, Guid? PaymentId, string Message, string? RefId = null);
public sealed record PaymentRow(Payment Payment, string UserName, string UserMobile);

public sealed class GatewaySettings
{
    public string Provider { get; set; } = "none";
    public bool Enabled { get; set; }
    public string MerchantId { get; set; } = string.Empty;
    public bool HasMerchantId { get; set; }
    public bool Sandbox { get; set; } = true;
    public string Description { get; set; } = "اشتراک LiveStudio";
}

public interface IPaymentService
{
    Task<GatewayStartResult> StartCheckoutAsync(Guid userId, Guid planId, string? ip, string baseUrl, CancellationToken ct = default);
    Task<PaymentCallbackResult> HandleCallbackAsync(string? authority, string? status, string? ip, CancellationToken ct = default);
    Task<Payment?> GetByAuthorityAsync(string authority, CancellationToken ct = default);
    Task<PagedResult<PaymentRow>> SearchAsync(Guid? userId, string? query, PaymentStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<PaymentRow?> GetAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> ManualMarkPaidAsync(Guid paymentId, string note, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> ManualCancelAsync(Guid paymentId, ActorInfo actor, CancellationToken ct = default);
    Task<GatewaySettings> GetGatewaySettingsAsync(CancellationToken ct = default);
    /// <param name="merchantIdOrNull">null/empty keeps the stored merchant id.</param>
    Task SaveGatewaySettingsAsync(GatewaySettings settings, string? merchantIdOrNull, ActorInfo actor, CancellationToken ct = default);
    Task<bool> IsCheckoutAvailableAsync(CancellationToken ct = default);
}

// ------------------------------------------------------------------ Tickets
public sealed record TicketRow(Guid Id, long Number, string Subject, string Department, TicketPriority Priority, TicketStatus Status,
    DateTime CreatedAt, DateTime UpdatedAt, Guid UserId, string UserName, string UserMobile, int MessageCount);
public sealed record TicketDetail(Ticket Ticket, AppUser User, IReadOnlyList<TicketMessage> Messages);

public interface ITicketService
{
    Task<OperationResult<Guid>> CreateAsync(Guid userId, string subject, string department, TicketPriority priority, string body, CancellationToken ct = default);
    Task<PagedResult<TicketRow>> SearchAsync(Guid? userId, string? query, TicketStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<TicketDetail?> GetAsync(Guid id, Guid? requiredUserId, CancellationToken ct = default);
    Task<OperationResult> ReplyAsync(Guid ticketId, Guid? requiredUserId, bool isStaff, string senderName, string body, CancellationToken ct = default);
    Task<OperationResult> SetStatusAsync(Guid ticketId, TicketStatus status, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> SetPriorityAsync(Guid ticketId, TicketPriority priority, ActorInfo actor, CancellationToken ct = default);
    Task<int> CountOpenAsync(Guid? userId = null, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Logs
public interface IActivityLogService
{
    Task LogAsync(ActorInfo actor, string action, string? details = null, string? userAgent = null, CancellationToken ct = default);
    Task<PagedResult<ActivityLog>> SearchAsync(string? query, ActorType? actorType, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Monitoring
public sealed record ServiceHealth(string Key, string Title, bool Healthy, string Detail, long? LatencyMs = null, bool Unknown = false);
public sealed record SystemSnapshot(DateTime At, IReadOnlyList<ServiceHealth> Services, long WorkingSetMb, long DiskFreeGb, long DiskTotalGb, TimeSpan Uptime, int ThreadCount);

public interface IMonitoringService
{
    SystemSnapshot? LastSnapshot { get; }
    Task<SystemSnapshot> CheckNowAsync(bool raiseAlerts, CancellationToken ct = default);
    Task<PagedResult<SystemAlert>> AlertsAsync(string? query, bool? resolved, AlertSeverity? severity, int page, int pageSize, CancellationToken ct = default);
    Task ResolveAsync(Guid alertId, ActorInfo actor, CancellationToken ct = default);
    Task ResolveAllAsync(ActorInfo actor, CancellationToken ct = default);
    Task<int> OpenAlertCountAsync(CancellationToken ct = default);
    Task RaiseAsync(AlertSeverity severity, string source, string title, string? message, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Content
public sealed class SiteContent
{
    public string SiteName { get; set; } = "LiveStudio";
    public string HeroTitle { get; set; } = string.Empty;
    public string HeroSubtitle { get; set; } = string.Empty;
    public string AnnouncementBar { get; set; } = string.Empty;
    public string SupportPhone { get; set; } = string.Empty;
    public string SupportEmail { get; set; } = string.Empty;
    public string SupportTelegram { get; set; } = string.Empty;
    public string FooterText { get; set; } = string.Empty;
    public string TermsText { get; set; } = string.Empty;
}

public interface IContentService
{
    Task<SiteContent> GetSiteContentAsync(CancellationToken ct = default);
    Task SaveSiteContentAsync(SiteContent content, ActorInfo actor, CancellationToken ct = default);
    Task<IReadOnlyList<FaqItem>> GetFaqAsync(bool onlyActive, CancellationToken ct = default);
    Task<OperationResult> SaveFaqAsync(FaqItem item, ActorInfo actor, CancellationToken ct = default);
    Task DeleteFaqAsync(Guid id, ActorInfo actor, CancellationToken ct = default);
    Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(bool onlyActive, CancellationToken ct = default);
    Task<OperationResult> SaveAnnouncementAsync(Announcement item, ActorInfo actor, CancellationToken ct = default);
    Task DeleteAnnouncementAsync(Guid id, ActorInfo actor, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Staff
public interface IStaffService
{
    Task<PagedResult<StaffUser>> SearchAsync(string? query, int page, int pageSize, CancellationToken ct = default);
    Task<StaffUser?> GetAsync(Guid id, CancellationToken ct = default);
    /// <param name="newPassword">Required for new users; empty keeps the existing password.</param>
    Task<OperationResult> SaveAsync(Guid? id, string username, string displayName, string? newPassword, IEnumerable<string> permissions, bool isActive, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid id, ActorInfo actor, CancellationToken ct = default);
}

// ------------------------------------------------------------------ Dashboard stats
public sealed record DailyPoint(DateTime Day, long Value);
public sealed record AdminStats(int TotalUsers, int NewUsers7Days, int ActiveSubscriptions, int ExpiringIn7Days, long RevenueThisMonth,
    long RevenueTotal, int OpenTickets, int OpenAlerts, int ConnectedChannels, IReadOnlyList<DailyPoint> Signups14Days, IReadOnlyList<DailyPoint> Revenue14Days);

public interface IAdminStatsService { Task<AdminStats> GetAsync(CancellationToken ct = default); }

// ------------------------------------------------------------------ Google OAuth (per customer / tenant)
public sealed record GoogleOAuthSettingsView(
    bool HasSettings, string ClientId, bool HasSecret, string ProjectId, string RedirectUri, OAuthConnectionState State,
    string? LastError, string? GoogleEmail, string? ChannelId, string? ChannelTitle, string? ChannelThumbnailUrl,
    DateTime? ValidatedAt, DateTime? ConnectedAt, DateTime? LastTestAt, string? TutorialVideoUrl);

public sealed record GoogleOAuthSaveRequest(string ClientId, string? ClientSecret, string ProjectId);
public sealed record OAuthCallbackResult(bool Success, Guid? UserId, string Message, bool Drive = false);

public interface IGoogleOAuthTenantService
{
    Task<GoogleOAuthSettingsView> GetAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Step 1 + 2: validates the inputs and stores them encrypted for this tenant.</summary>
    Task<OperationResult> SaveAsync(Guid userId, GoogleOAuthSaveRequest request, ActorInfo actor, CancellationToken ct = default);
    /// <summary>Step 3: builds the Google consent URL using the tenant's own client id.</summary>
    Task<OperationResult<string>> BeginAuthorizationAsync(Guid userId, ActorInfo actor, CancellationToken ct = default, bool includeDrive = false);
    /// <summary>Steps 5-7: validates state, exchanges the code, verifies identity + channel and marks the tenant as Connected.</summary>
    Task<OAuthCallbackResult> CompleteAuthorizationAsync(string? code, string? state, string? error, Guid? expectedUserId, string? ip, CancellationToken ct = default);
    Task<OperationResult> TestConnectionAsync(Guid userId, ActorInfo actor, CancellationToken ct = default);
    Task<OperationResult> DisconnectAsync(Guid userId, bool removeCredentials, ActorInfo actor, CancellationToken ct = default);
    Task<string> GetSharedRedirectUriAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Multistream destinations, live sessions (post-stream stats), personal presets
// ---------------------------------------------------------------------------

public sealed record DestinationRow(Guid Id, string Name, string Platform, string Host, bool IsEnabled);
/// <summary>Only the Api (egress start) receives the decrypted target.</summary>
public sealed record DestinationTarget(Guid Id, string Name, string FullUrl);

public interface IStreamDestinationService
{
    Task<IReadOnlyList<DestinationRow>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult> AddAsync(Guid userId, string name, string platform, string rtmpUrl, string streamKey, CancellationToken ct = default);
    Task<OperationResult> SetEnabledAsync(Guid userId, Guid id, bool enabled, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DestinationTarget>> GetEnabledTargetsAsync(Guid userId, CancellationToken ct = default);
}

public sealed record SessionRow(Guid Id, string BroadcastId, string Title, DateTime StartedAt, DateTime? EndedAt, int PeakViewers, long Views, long Likes, int Comments, string Destinations)
{
    public TimeSpan Duration => (EndedAt ?? DateTime.UtcNow) - StartedAt;
    public bool IsLive => EndedAt is null;
}

public interface IStreamSessionService
{
    Task StartAsync(Guid userId, string broadcastId, string title, string destinations, CancellationToken ct = default);
    Task<SessionRow?> RecordStatsAsync(Guid userId, string broadcastId, int concurrentViewers, long views, long likes, CancellationToken ct = default);
    Task AddCommentsAsync(Guid userId, string broadcastId, int count, CancellationToken ct = default);
    Task<SessionRow?> EndAsync(Guid userId, string broadcastId, CancellationToken ct = default);
    Task<SessionRow?> GetLatestAsync(Guid userId, string broadcastId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRow>> ListAsync(Guid userId, int take = 50, CancellationToken ct = default);
}

public sealed record PresetRow(Guid Id, string Name, string Json);

public interface IUserPresetService
{
    Task<IReadOnlyList<PresetRow>> ListAsync(Guid userId, string kind, CancellationToken ct = default);
    Task<OperationResult> SaveAsync(Guid userId, string kind, string name, string json, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
}

/// <summary>Live chat message of the connected YouTube broadcast (sent to the studio comments panel).</summary>
public sealed record YouTubeChatMessage(string Id, string Author, string Text, DateTimeOffset? PublishedAt, string? Amount, string? AuthorChannelId = null);
public sealed record YouTubeChatPage(IReadOnlyList<YouTubeChatMessage> Messages, string? NextPageToken, int PollingIntervalMillis);
public sealed record YouTubeLiveStats(int ConcurrentViewers, long Views, long Likes);


// ---- archive option / team / rundown -------------------------------------------------------------------
public interface IBroadcastOptionService
{
    Task SetAsync(Guid userId, string broadcastId, bool keepArchive, CancellationToken ct = default);
    /// <summary>Null when no option was stored (older broadcasts): treat as "keep".</summary>
    Task<bool?> GetKeepArchiveAsync(Guid userId, string broadcastId, CancellationToken ct = default);
    Task<bool> IsFinalizedAsync(Guid userId, string broadcastId, CancellationToken ct = default);
    Task MarkFinalizedAsync(Guid userId, string broadcastId, CancellationToken ct = default);
}

public sealed record TeamMemberRow(Guid Id, Guid MemberUserId, string FullName, string Mobile, string Role);
public sealed record TeamMembership(Guid OwnerUserId, string OwnerName, string Role);

public interface ITeamService
{
    Task<IReadOnlyList<TeamMemberRow>> ListMembersAsync(Guid ownerId, CancellationToken ct = default);
    Task<OperationResult> InviteAsync(Guid ownerId, string mobile, string role, CancellationToken ct = default);
    Task<OperationResult> ChangeRoleAsync(Guid ownerId, Guid id, string role, CancellationToken ct = default);
    Task<OperationResult> RemoveAsync(Guid ownerId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TeamMembership>> MembershipsAsync(Guid memberId, CancellationToken ct = default);
    /// <summary>Role of <paramref name="userId"/> inside the studio of <paramref name="ownerId"/>; the owner is always host. Null = no access.</summary>
    Task<string?> GetRoleAsync(Guid ownerId, Guid userId, CancellationToken ct = default);
}

public sealed record RundownRow(Guid Id, int Order, string AssetId, string Title, DateTime? PlannedAt, bool AutoPlay, bool Played);

public interface IRundownService
{
    Task<IReadOnlyList<RundownRow>> ListAsync(Guid userId, string broadcastId, CancellationToken ct = default);
    Task<OperationResult> AddAsync(Guid userId, string broadcastId, string assetId, string title, DateTime? plannedAtUtc, bool autoPlay, CancellationToken ct = default);
    Task<OperationResult> UpdateAsync(Guid userId, Guid id, DateTime? plannedAtUtc, bool autoPlay, CancellationToken ct = default);
    Task<OperationResult> MarkPlayedAsync(Guid userId, Guid id, bool played, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<OperationResult> MoveAsync(Guid userId, Guid id, int direction, CancellationToken ct = default);
}


// ---- Media Box storage (server disk for light files, Google Drive for videos) -------------------------------
public sealed record MediaAssetRow(Guid Id, string Name, string MimeType, string Category, long Size, string Storage, DateTime CreatedAt);
public sealed record MediaUsage(long UsedBytes, long QuotaBytes, long MaxFileBytes);
public sealed record MediaFileInfo(Guid Id, string Name, string MimeType, string Storage, string? StoredName, string? DriveFileId);

public interface IMediaStorageService
{
    Task<IReadOnlyList<MediaAssetRow>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<MediaUsage> UsageAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Stores an image / SVG / short audio file on the server. Videos are refused here by design.</summary>
    Task<OperationResult<MediaAssetRow>> AddFileAsync(Guid userId, string fileName, string category, Stream content, long length, CancellationToken ct = default);
    /// <summary>Registers a video that the browser uploaded to the customer's Drive folder (verified against Drive).</summary>
    Task<OperationResult<MediaAssetRow>> RegisterDriveFileAsync(Guid userId, string driveFileId, string name, string category, CancellationToken ct = default);
    Task<MediaFileInfo?> GetAsync(Guid userId, Guid id, CancellationToken ct = default);
    string GetPath(Guid userId, string storedName);
    Task<OperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
}

public sealed record DriveFileCheck(string MimeType, long Size);
public sealed record DriveStatus(bool OAuthConnected, bool Enabled, string? FolderName, string? Email);

public interface IDriveStorageService
{
    Task<DriveStatus> GetStatusAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Stops using Drive for new videos. Files already in Drive stay there; access can be revoked from the Google account.</summary>
    Task<OperationResult> DisableAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Starts a resumable upload (with CORS for <paramref name="origin"/>) and returns the session URL the browser uploads to directly.</summary>
    Task<OperationResult<string>> CreateUploadSessionAsync(Guid userId, string name, string mimeType, long size, string origin, CancellationToken ct = default);
    /// <summary>Opens the Drive file for streaming (Range supported). The caller disposes the response.</summary>
    Task<OperationResult<DriveFileCheck>> VerifyFileAsync(Guid userId, string driveFileId, CancellationToken ct = default);
    Task<HttpResponseMessage> OpenFileAsync(Guid userId, string driveFileId, string? range, CancellationToken ct = default);
    Task DeleteFileAsync(Guid userId, string driveFileId, CancellationToken ct = default);
}
