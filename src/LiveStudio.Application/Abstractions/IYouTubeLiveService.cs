namespace LiveStudio.Application.Abstractions;

public sealed record YouTubeConnectionStatus(bool IsConfigured, bool IsConnected, string? ChannelTitle);
public sealed record YouTubeChannelDetails(
    string ChannelId,
    string Title,
    string? Description,
    string? CustomUrl,
    string? ThumbnailUrl,
    DateTimeOffset? PublishedAt,
    string? Country,
    long? SubscriberCount,
    bool IsSubscriberCountHidden,
    long? ViewCount,
    long? VideoCount);
public sealed record CreateBroadcastRequest(string Title, string? Description, DateTimeOffset? ScheduledStartAt, bool KeepArchive = true);
public sealed record BroadcastSummary(string Id, string Title, string Status, DateTimeOffset? ScheduledStartAt, string? StreamId = null, string? IngestionAddress = null, string? BackupIngestionAddress = null, string? StreamKey = null);
public sealed record BroadcastStartResult(string BroadcastId, string Status, string Message);

public interface IYouTubeLiveService
{
    Task<YouTubeConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken);
    Task<YouTubeChannelDetails> GetChannelDetailsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BroadcastSummary>> GetBroadcastsAsync(CancellationToken cancellationToken);
    Task<BroadcastSummary> CreateBroadcastAsync(CreateBroadcastRequest request, CancellationToken cancellationToken);
    Task<BroadcastStartResult> TransitionBroadcastAsync(string broadcastId, string transition, CancellationToken cancellationToken);
    Task<BroadcastSummary> GetBroadcastAsync(string broadcastId, CancellationToken cancellationToken);
}
