using LiveStudio.Domain.Entities;
using LiveStudio.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Infrastructure.Persistence;

public sealed class LiveStudioDbContext(DbContextOptions<LiveStudioDbContext> options) : DbContext(options)
{
    // Legacy YouTube tables (unchanged schema so that existing databases keep working)
    public DbSet<YouTubeChannel> Channels => Set<YouTubeChannel>();
    public DbSet<LiveBroadcast> Broadcasts => Set<LiveBroadcast>();
    public DbSet<GoogleOAuthCredential> GoogleCredentials => Set<GoogleOAuthCredential>();

    // Platform tables
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<SubscriptionPlan> Plans => Set<SubscriptionPlan>();
    public DbSet<UserSubscription> Subscriptions => Set<UserSubscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<SystemSetting> Settings => Set<SystemSetting>();
    public DbSet<SystemAlert> Alerts => Set<SystemAlert>();
    public DbSet<FaqItem> Faqs => Set<FaqItem>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<TenantGoogleOAuth> TenantOAuth => Set<TenantGoogleOAuth>();
    public DbSet<StreamDestination> StreamDestinations => Set<StreamDestination>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<UserPreset> UserPresets => Set<UserPreset>();
    public DbSet<BroadcastOption> BroadcastOptions => Set<BroadcastOption>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<MediaAssetRecord> MediaAssets => Set<MediaAssetRecord>();
    public DbSet<DriveLink> DriveLinks => Set<DriveLink>();
    public DbSet<RundownItem> RundownItems => Set<RundownItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StreamDestination>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.UserId);
            b.Property(x => x.Name).HasMaxLength(60).IsRequired();
            b.Property(x => x.Platform).HasMaxLength(20).IsRequired();
            b.Property(x => x.RtmpUrl).HasMaxLength(400).IsRequired();
            b.Property(x => x.ProtectedStreamKey).IsRequired();
        });
        modelBuilder.Entity<StreamSession>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.StartedAt });
            b.Property(x => x.BroadcastId).HasMaxLength(128).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.Destinations).HasMaxLength(400);
        });
        modelBuilder.Entity<BroadcastOption>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.BroadcastId }).IsUnique();
            b.Property(x => x.BroadcastId).HasMaxLength(128).IsRequired();
        });
        modelBuilder.Entity<MediaAssetRecord>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.CreatedAt });
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.MimeType).HasMaxLength(100).IsRequired();
            b.Property(x => x.Category).HasMaxLength(30).IsRequired();
            b.Property(x => x.Storage).HasMaxLength(10).IsRequired();
            b.Property(x => x.StoredName).HasMaxLength(80);
            b.Property(x => x.DriveFileId).HasMaxLength(120);
        });
        modelBuilder.Entity<DriveLink>(b =>
        {
            b.HasKey(x => x.UserId);
            b.Property(x => x.FolderId).HasMaxLength(120);
            b.Property(x => x.FolderName).HasMaxLength(120);
            b.Property(x => x.Email).HasMaxLength(200);
        });
        modelBuilder.Entity<TeamMember>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.OwnerUserId, x.MemberUserId }).IsUnique();
            b.HasIndex(x => x.MemberUserId);
            b.Property(x => x.Role).HasMaxLength(20).IsRequired();
        });
        modelBuilder.Entity<RundownItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.BroadcastId, x.Order });
            b.Property(x => x.BroadcastId).HasMaxLength(128).IsRequired();
            b.Property(x => x.AssetId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Title).HasMaxLength(120);
        });
        modelBuilder.Entity<UserPreset>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.Kind });
            b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            b.Property(x => x.Name).HasMaxLength(60).IsRequired();
            b.Property(x => x.Json).HasMaxLength(4000).IsRequired();
        });
        modelBuilder.Entity<YouTubeChannel>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ChannelId).IsUnique();
            b.Property(x => x.ChannelId).HasMaxLength(128).IsRequired();
            b.Property(x => x.Title).HasMaxLength(256).IsRequired();
            b.Property(x => x.GoogleSubject).HasMaxLength(256).IsRequired();
        });
        modelBuilder.Entity<LiveBroadcast>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.YouTubeBroadcastId);
            b.Property(x => x.Title).HasMaxLength(256).IsRequired();
            b.Property(x => x.Description).HasMaxLength(5000);
        });
        modelBuilder.Entity<GoogleOAuthCredential>(b =>
        {
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(64);
            b.Property(x => x.ProtectedTokenPayload).IsRequired();
        });

        modelBuilder.Entity<AppUser>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.NationalCode).IsUnique();
            b.HasIndex(x => x.Mobile).IsUnique();
            b.Property(x => x.NationalCode).HasMaxLength(10).IsRequired();
            b.Property(x => x.Mobile).HasMaxLength(11).IsRequired();
            b.Property(x => x.FullName).HasMaxLength(120).IsRequired();
            b.Property(x => x.Email).HasMaxLength(200);
            b.Property(x => x.ChannelName).HasMaxLength(200);
            b.Property(x => x.AdminNote).HasMaxLength(2000);
        });
        modelBuilder.Entity<SubscriptionPlan>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            b.Property(x => x.Tagline).HasMaxLength(300);
            b.Property(x => x.BadgeText).HasMaxLength(40);
            b.Property(x => x.FeaturesText).HasMaxLength(4000);
        });
        modelBuilder.Entity<UserSubscription>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.UserId);
            b.Property(x => x.PlanName).HasMaxLength(100);
            b.Property(x => x.Note).HasMaxLength(1000);
            b.Property(x => x.CreatedBy).HasMaxLength(120);
        });
        modelBuilder.Entity<Payment>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => x.Authority);
            b.Property(x => x.PlanName).HasMaxLength(100);
            b.Property(x => x.Gateway).HasMaxLength(40);
            b.Property(x => x.Authority).HasMaxLength(120);
            b.Property(x => x.RefId).HasMaxLength(120);
            b.Property(x => x.CardPan).HasMaxLength(40);
            b.Property(x => x.FailureReason).HasMaxLength(500);
            b.Property(x => x.Description).HasMaxLength(300);
        });
        modelBuilder.Entity<Ticket>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => x.UserId);
            b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            b.Property(x => x.Department).HasMaxLength(40);
        });
        modelBuilder.Entity<TicketMessage>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.TicketId);
            b.Property(x => x.SenderName).HasMaxLength(120);
            b.Property(x => x.Body).HasMaxLength(5000).IsRequired();
        });
        modelBuilder.Entity<ActivityLog>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.At);
            b.Property(x => x.ActorId).HasMaxLength(64);
            b.Property(x => x.ActorName).HasMaxLength(120);
            b.Property(x => x.Action).HasMaxLength(120).IsRequired();
            b.Property(x => x.Details).HasMaxLength(2000);
            b.Property(x => x.Ip).HasMaxLength(64);
            b.Property(x => x.UserAgent).HasMaxLength(300);
        });
        modelBuilder.Entity<StaffUser>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Username).IsUnique();
            b.Property(x => x.Username).HasMaxLength(40).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(120);
            b.Property(x => x.PasswordHash).HasMaxLength(400).IsRequired();
            b.Property(x => x.PermissionsCsv).HasMaxLength(600);
        });
        modelBuilder.Entity<SystemSetting>(b =>
        {
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(120);
            b.Property(x => x.Value).HasMaxLength(8000);
        });
        modelBuilder.Entity<SystemAlert>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.At);
            b.Property(x => x.Source).HasMaxLength(60);
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.Message).HasMaxLength(1000);
        });
        modelBuilder.Entity<FaqItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Question).HasMaxLength(300).IsRequired();
            b.Property(x => x.Answer).HasMaxLength(3000).IsRequired();
        });
        modelBuilder.Entity<Announcement>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Body).HasMaxLength(2000);
            b.Property(x => x.Level).HasMaxLength(20);
        });
        modelBuilder.Entity<TenantGoogleOAuth>(b =>
        {
            b.HasKey(x => x.UserId);
            b.Property(x => x.ClientId).HasMaxLength(300);
            b.Property(x => x.ProtectedClientSecret).HasMaxLength(2000);
            b.Property(x => x.ProjectId).HasMaxLength(100);
            b.Property(x => x.RedirectUri).HasMaxLength(500);
            b.Property(x => x.LastError).HasMaxLength(600);
            b.Property(x => x.GoogleEmail).HasMaxLength(200);
            b.Property(x => x.ChannelId).HasMaxLength(128);
            b.Property(x => x.ChannelTitle).HasMaxLength(256);
            b.Property(x => x.ChannelThumbnailUrl).HasMaxLength(500);
        });
    }
}
