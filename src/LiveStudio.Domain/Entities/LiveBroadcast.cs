namespace LiveStudio.Domain.Entities;

public enum BroadcastStatus { Created, Ready, Testing, Live, Complete, Revoked, Error }

public sealed class LiveBroadcast
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChannelId { get; set; }
    public string YouTubeBroadcastId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public BroadcastStatus Status { get; set; } = BroadcastStatus.Created;
    public DateTimeOffset? ScheduledStartAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
