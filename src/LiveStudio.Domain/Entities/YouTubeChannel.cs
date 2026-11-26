namespace LiveStudio.Domain.Entities;

public sealed class YouTubeChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GoogleSubject { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
}
