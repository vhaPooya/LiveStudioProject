namespace LiveStudio.Domain.Platform;

// ---------------------------------------------------------------------------
// All platform timestamps are UTC DateTime values (SQLite sorts / compares them
// correctly, unlike DateTimeOffset). All money amounts are whole Toman (long).
// ---------------------------------------------------------------------------

public enum SubscriptionStatus { Active = 0, Expired = 1, Cancelled = 2 }
public enum SubscriptionSource { Trial = 0, Payment = 1, Admin = 2 }
public enum PaymentStatus { Pending = 0, Paid = 1, Failed = 2, Cancelled = 3, Refunded = 4 }
public enum TicketStatus { Open = 0, Answered = 1, CustomerReply = 2, Closed = 3 }
public enum TicketPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }
public enum AlertSeverity { Info = 0, Warning = 1, Critical = 2 }
public enum OAuthConnectionState { NotConfigured = 0, Saved = 1, AwaitingConsent = 2, Connected = 3, Error = 4 }
public enum ActorType { System = 0, Customer = 1, Admin = 2, Staff = 3 }

/// <summary>A customer (tenant). One user = one tenant = one YouTube channel connection.</summary>
public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string NationalCode { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? ChannelName { get; set; }
    public bool IsBlocked { get; set; }
    public string? AdminNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public sealed class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    /// <summary>Price for one period of <see cref="DurationDays"/> days (Toman).</summary>
    public long Price { get; set; }
    /// <summary>Optional strike-through price shown as the original price.</summary>
    public long? OldPrice { get; set; }
    public int DurationDays { get; set; } = 30;
    public string? BadgeText { get; set; }
    public bool IsPopular { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsTrial { get; set; }
    public int SortOrder { get; set; }
    public int MaxMonthlyBroadcasts { get; set; }
    public int MaxResolutionHeight { get; set; } = 1080;
    public int StorageGb { get; set; }
    /// <summary>One feature per line.</summary>
    public string FeaturesText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class UserSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public DateTime StartsAt { get; set; } = DateTime.UtcNow;
    public DateTime EndsAt { get; set; } = DateTime.UtcNow;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public SubscriptionSource Source { get; set; } = SubscriptionSource.Payment;
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Number { get; set; }
    public Guid UserId { get; set; }
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public int DurationDays { get; set; }
    public long Amount { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string? Authority { get; set; }
    public string? RefId { get; set; }
    public string? CardPan { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? FailureReason { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
}

public sealed class Ticket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Number { get; set; }
    public Guid UserId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Department { get; set; } = "support";
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TicketMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public bool IsStaff { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ActivityLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public ActorType ActorType { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>Back-office staff account with granular permissions (the built-in super admin is not stored here).</summary>
public sealed class StaffUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    /// <summary>Comma separated permission keys, see <see cref="Permissions"/>.</summary>
    public string PermissionsCsv { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public sealed class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SystemAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.UtcNow;
    public AlertSeverity Severity { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public sealed class FaqItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Announcement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    /// <summary>info | success | warning | danger</summary>
    public string Level { get; set; } = "info";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Per-tenant Google OAuth client. The client secret is stored encrypted (ASP.NET Data Protection) and is
/// never returned to the browser after it has been saved.
/// </summary>
public sealed class TenantGoogleOAuth
{
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ProtectedClientSecret { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public OAuthConnectionState State { get; set; } = OAuthConnectionState.NotConfigured;
    public string? LastError { get; set; }
    public string? GoogleEmail { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelTitle { get; set; }
    public string? ChannelThumbnailUrl { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? LastTestAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Permission keys for back-office staff. The built-in super admin owns all of them.</summary>
public static class Permissions
{
    public const string Dashboard = "dashboard";
    public const string Users = "users";
    public const string Subscriptions = "subscriptions";
    public const string Plans = "plans";
    public const string Payments = "payments";
    public const string Gateway = "gateway";
    public const string Tickets = "tickets";
    public const string Monitoring = "monitoring";
    public const string Logs = "logs";
    public const string Content = "content";
    public const string Settings = "settings";
    public const string Staff = "staff";

    public static readonly IReadOnlyList<(string Key, string Title, string Description)> All =
    [
        (Dashboard, "داشبورد مدیریت", "مشاهده آمار کلی سامانه"),
        (Users, "کاربران", "مشاهده و ویرایش اطلاعات کاربران و مسدودسازی"),
        (Subscriptions, "اشتراک کاربران", "تغییر دستی نوع و مدت اشتراک کاربران"),
        (Plans, "طرح‌ها و قیمت‌ها", "ثبت و ویرایش طرح‌های اشتراک"),
        (Payments, "پرداخت‌ها", "مشاهده و پیگیری پرداخت‌های کاربران"),
        (Gateway, "درگاه پرداخت", "تنظیمات درگاه پرداخت"),
        (Tickets, "تیکت‌ها", "بررسی و پاسخ به تیکت‌های مشتریان"),
        (Monitoring, "پایش سامانه", "مشاهده وضعیت سرویس‌ها و هشدارها"),
        (Logs, "لاگ فعالیت‌ها", "مشاهده لاگ فعالیت کاربران و مدیران"),
        (Content, "محتوای وب‌سایت", "ویرایش متن‌ها، سوالات متداول و اعلان‌ها"),
        (Settings, "تنظیمات سامانه", "پیامک، OAuth مشترک و تنظیمات عمومی"),
        (Staff, "کاربران مدیریت", "تعریف کاربر مدیریتی و تعیین دسترسی")
    ];

    public static IReadOnlySet<string> Parse(string? csv) =>
        (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>An extra RTMP/RTMPS multistream target of a tenant. The stream key is stored encrypted and never returned.</summary>
public sealed class StreamDestination
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>twitch | facebook | aparat | custom ...</summary>
    public string Platform { get; set; } = "custom";
    public string RtmpUrl { get; set; } = string.Empty;
    public string ProtectedStreamKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One live session (from Go live to stop) with the numbers shown in the post-stream report.</summary>
public sealed class StreamSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string BroadcastId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public int PeakViewers { get; set; }
    public long LastViews { get; set; }
    public long LastLikes { get; set; }
    public int CommentCount { get; set; }
    public string Destinations { get; set; } = "YouTube";
}

/// <summary>A personal, reusable preset (for now: animation settings) of a tenant.</summary>
public sealed class UserPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    /// <summary>anim</summary>
    public string Kind { get; set; } = "anim";
    public string Name { get; set; } = string.Empty;
    public string Json { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-broadcast options chosen when the live was created (e.g. keep or drop the YouTube recording).</summary>
public sealed class BroadcastOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string BroadcastId { get; set; } = string.Empty;
    /// <summary>true: the recording stays on the channel; false: it is deleted from YouTube after the live ends.</summary>
    public bool KeepArchive { get; set; } = true;
    public bool Finalized { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A registered customer that may work inside another customer's studio with a restricted role.</summary>
public sealed class TeamMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public Guid MemberUserId { get; set; }
    public string Role { get; set; } = StudioRoles.Observer;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A planned video of a broadcast, played by the video operator (or automatically at the planned time).</summary>
public sealed class RundownItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string BroadcastId { get; set; } = string.Empty;
    public int Order { get; set; }
    public string AssetId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>Planned start (UTC). Null = manual.</summary>
    public DateTime? PlannedAt { get; set; }
    public bool AutoPlay { get; set; }
    public bool Played { get; set; }
}

/// <summary>Studio team roles. These are separate from the back-office <see cref="Permissions"/> of staff users.</summary>
public static class StudioRoles
{
    public const string Host = "host";
    public const string Editor = "editor";
    public const string Observer = "observer";
    public const string Moderator = "moderator";
    public const string VideoOperator = "video";

    public static readonly IReadOnlyList<(string Key, string Title, string Description)> All =
    [
        (Host, "میزبان", "دسترسی کامل به استودیو، شروع و پایان پخش"),
        (Editor, "تدوینگر", "بنر، Lower third، تایمر، قالب‌ها و صداهای جلوه‌ای از راه دور"),
        (Moderator, "مسئول کامنت‌ها", "بررسی کامنت‌ها، نمایش روی تصویر، حذف پیام و بیرون‌انداختن بیننده از چت"),
        (VideoOperator, "مسئول پخش ویدئو", "پخش ویدئوها طبق برنامه‌ی تعیین‌شده"),
        (Observer, "ناظر", "فقط مشاهده‌ی تصویر خروجی، چت و آمار")
    ];

    public static bool IsValid(string? role) => All.Any(x => x.Key == role);
    public static string Title(string? role) => All.FirstOrDefault(x => x.Key == role).Title ?? "—";

    public static IReadOnlyList<string> PermissionsOf(string? role) => role switch
    {
        Host => StudioPerms.All,
        Editor => [StudioPerms.View, StudioPerms.Layout, StudioPerms.Sfx],
        Moderator => [StudioPerms.View, StudioPerms.Chat, StudioPerms.Moderate],
        VideoOperator => [StudioPerms.View, StudioPerms.Video],
        Observer => [StudioPerms.View, StudioPerms.Chat],
        _ => []
    };
}

/// <summary>Fine-grained studio permissions (checked by the Web gateway and again by the host studio for remote commands).</summary>
public static class StudioPerms
{
    public const string View = "studio.view";
    public const string GoLive = "studio.golive";
    public const string Layout = "studio.layout";
    public const string Chat = "studio.chat";
    public const string Moderate = "studio.moderate";
    public const string Video = "studio.video";
    public const string Sfx = "studio.sfx";
    public const string Team = "studio.team";

    public static readonly IReadOnlyList<string> All = [View, GoLive, Layout, Chat, Moderate, Video, Sfx, Team];
}

/// <summary>A Media Box file kept outside the browser: light files on the server disk, videos on the customer's Google Drive.</summary>
public sealed class MediaAssetRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string Category { get; set; } = "overlay";
    public long Size { get; set; }
    /// <summary>"server" or "drive".</summary>
    public string Storage { get; set; } = "server";
    public string? StoredName { get; set; }
    public string? DriveFileId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Google Drive storage link of a customer (the OAuth tokens themselves live in the encrypted credential store).</summary>
public sealed class DriveLink
{
    public Guid UserId { get; set; }
    public bool Enabled { get; set; }
    public string? FolderId { get; set; }
    public string? FolderName { get; set; }
    public string? Email { get; set; }
    public DateTime? ConnectedAt { get; set; }
}
