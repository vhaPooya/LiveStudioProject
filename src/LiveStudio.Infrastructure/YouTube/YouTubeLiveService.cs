using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveStudio.Application.Abstractions;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Entities;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace LiveStudio.Infrastructure.YouTube;

public sealed class YouTubeLiveService : IYouTubeLiveService
{
    private readonly ITenantContext _tenant;
    private readonly LiveStudioDbContext _db;
    private readonly GoogleTokenBroker _broker;
    private readonly IHttpClientFactory _httpFactory;

    public YouTubeLiveService(ITenantContext tenant, LiveStudioDbContext db, GoogleTokenBroker broker, IHttpClientFactory httpFactory)
    {
        _tenant = tenant;
        _db = db;
        _broker = broker;
        _httpFactory = httpFactory;
    }

    private Guid TenantId => _tenant.TenantId ?? throw new InvalidOperationException("نشست کاربر مشخص نیست؛ دوباره وارد شوید.");

    public async Task<YouTubeConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)
    {
        var id = TenantId;
        var row = await _db.TenantOAuth.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id, ct);
        if (row is null || string.IsNullOrEmpty(row.ProtectedClientSecret)) return new(false, false, null);
        var connected = row.State == OAuthConnectionState.Connected;
        return new(true, connected, connected ? row.ChannelTitle : null);
    }

    public async Task<YouTubeChannelDetails> GetChannelDetailsAsync(CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/youtube/v3/channels?part=snippet%2Cstatistics&mine=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpFactory.CreateClient("YouTube").SendAsync(request, ct);
        await EnsureYouTubeSuccess(response, ct);

        var payload = await response.Content.ReadFromJsonAsync<ChannelListResponse>(cancellationToken: ct)
            ?? new ChannelListResponse();
        var channel = payload.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("No YouTube channel was found for the connected Google account.");

        return new YouTubeChannelDetails(
            channel.Id,
            channel.Snippet.Title,
            channel.Snippet.Description,
            channel.Snippet.CustomUrl,
            channel.Snippet.Thumbnails?.High?.Url
                ?? channel.Snippet.Thumbnails?.Medium?.Url
                ?? channel.Snippet.Thumbnails?.Default?.Url,
            ParseDate(channel.Snippet.PublishedAt),
            channel.Snippet.Country,
            ParseCount(channel.Statistics.SubscriberCount),
            channel.Statistics.HiddenSubscriberCount,
            ParseCount(channel.Statistics.ViewCount),
            ParseCount(channel.Statistics.VideoCount));
    }

    public async Task<IReadOnlyList<BroadcastSummary>> GetBroadcastsAsync(CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/youtube/v3/liveBroadcasts?part=snippet%2Cstatus%2CcontentDetails&broadcastStatus=all&maxResults=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpFactory.CreateClient("YouTube").SendAsync(request, ct);
        await EnsureYouTubeSuccess(response, ct);
        var payload = await response.Content.ReadFromJsonAsync<BroadcastListResponse>(cancellationToken: ct)
            ?? new BroadcastListResponse();
        return payload.Items.Select(x => new BroadcastSummary(x.Id, x.Snippet.Title,
            x.Status.LifeCycleStatus, ParseDate(x.Snippet.ScheduledStartTime))).ToArray();
    }

    public async Task<BroadcastSummary> CreateBroadcastAsync(CreateBroadcastRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 100)
            throw new ArgumentException("Broadcast title must contain 1 to 100 characters.", nameof(request));
        if (request.Description?.Length > 5000)
            throw new ArgumentException("Broadcast description cannot exceed 5000 characters.", nameof(request));

        var token = await GetValidAccessTokenAsync(ct);
        var scheduled = (request.ScheduledStartAt ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUniversalTime();
        var body = new
        {
            snippet = new { title = request.Title.Trim(), description = request.Description ?? string.Empty, scheduledStartTime = scheduled.ToString("O") },
            status = new { privacyStatus = "private", selfDeclaredMadeForKids = false },
            contentDetails = new { enableAutoStart = false, enableAutoStop = false, latencyPreference = "normal" }
        };
        var broadcast = await SendYouTubeAsync<BroadcastItem>(HttpMethod.Post,
            "https://www.googleapis.com/youtube/v3/liveBroadcasts?part=snippet%2Cstatus%2CcontentDetails", body, token, ct);

        // A broadcast without a bound ingestion stream can never receive encoder video.
        var streamBody = new
        {
            snippet = new { title = $"{request.Title.Trim()} - LiveStudio Encoder", description = "Managed by LiveStudio" },
            cdn = new { frameRate = "variable", ingestionType = "rtmp", resolution = "variable" },
            contentDetails = new { isReusable = false }
        };
        var stream = await SendYouTubeAsync<StreamItem>(HttpMethod.Post,
            "https://www.googleapis.com/youtube/v3/liveStreams?part=snippet%2Ccdn%2CcontentDetails", streamBody, token, ct);
        await SendYouTubeAsync<JsonElement>(HttpMethod.Post,
            $"https://www.googleapis.com/youtube/v3/liveBroadcasts/bind?id={Uri.EscapeDataString(broadcast.Id)}&part=id%2CcontentDetails&streamId={Uri.EscapeDataString(stream.Id)}", null, token, ct);

        var connected = await _db.TenantOAuth.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == TenantId, ct);
        var channel = connected?.ChannelId is null ? null : await _db.Channels.FirstOrDefaultAsync(x => x.ChannelId == connected.ChannelId, ct);
        if (channel is not null)
        {
            _db.Broadcasts.Add(new LiveBroadcast { ChannelId = channel.Id, YouTubeBroadcastId = broadcast.Id,
                Title = broadcast.Snippet.Title, Description = request.Description, ScheduledStartAt = ParseDate(broadcast.Snippet.ScheduledStartTime), Status = BroadcastStatus.Created });
            await _db.SaveChangesAsync(ct);
        }
        return new BroadcastSummary(broadcast.Id, broadcast.Snippet.Title, broadcast.Status.LifeCycleStatus,
            ParseDate(broadcast.Snippet.ScheduledStartTime), stream.Id, stream.Cdn.IngestionInfo.IngestionAddress,
            stream.Cdn.IngestionInfo.BackupIngestionAddress, stream.Cdn.IngestionInfo.StreamName);
    }

    /// <summary>
    /// Resolves the RTMP destination for a YouTube broadcast on the server.
    /// The stream key is never returned from an API endpoint or sent to the browser.
    /// </summary>
    public async Task<string> GetRtmpIngestionUrlAsync(string broadcastId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > 128)
            throw new ArgumentException("شناسه پخش معتبر نیست.", nameof(broadcastId));

        var token = await GetValidAccessTokenAsync(ct);
        var broadcastPayload = await SendYouTubeAsync<BroadcastListResponse>(
            HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/liveBroadcasts?part=contentDetails&id={Uri.EscapeDataString(broadcastId)}",
            null, token, ct);

        var broadcast = broadcastPayload.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("پخش یوتیوب پیدا نشد.");

        var streamId = broadcast.ContentDetails.BoundStreamId;
        if (string.IsNullOrWhiteSpace(streamId))
            throw new InvalidOperationException("برای این پخش، استریم ورودی یوتیوب متصل نشده است.");

        var streamPayload = await SendYouTubeAsync<StreamListResponse>(
            HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/liveStreams?part=cdn&id={Uri.EscapeDataString(streamId)}",
            null, token, ct);

        var stream = streamPayload.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("استریم ورودی یوتیوب پیدا نشد.");
        var address = stream.Cdn.IngestionInfo.IngestionAddress?.TrimEnd('/');
        var streamKey = stream.Cdn.IngestionInfo.StreamName;

        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(streamKey))
            throw new InvalidOperationException("اطلاعات RTMP یوتیوب کامل نیست.");

        return $"{address}/{streamKey}";
    }

    public async Task<BroadcastSummary> GetBroadcastAsync(string broadcastId, CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(ct);
        var payload = await SendYouTubeAsync<BroadcastListResponse>(HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/liveBroadcasts?part=snippet%2Cstatus%2CcontentDetails&id={Uri.EscapeDataString(broadcastId)}", null, token, ct);
        var result = payload.Items.FirstOrDefault() ?? throw new InvalidOperationException("Broadcast was not found.");
        return new BroadcastSummary(result.Id, result.Snippet.Title, result.Status.LifeCycleStatus, ParseDate(result.Snippet.ScheduledStartTime));
    }

    public async Task<BroadcastStartResult> TransitionBroadcastAsync(string broadcastId, string transition, CancellationToken ct)
    {
        if (transition is not ("testing" or "live" or "complete"))
            throw new ArgumentException("Transition must be testing, live or complete.", nameof(transition));
        var token = await GetValidAccessTokenAsync(ct);
        var item = await SendYouTubeAsync<BroadcastItem>(HttpMethod.Post,
            $"https://www.googleapis.com/youtube/v3/liveBroadcasts/transition?broadcastStatus={transition}&id={Uri.EscapeDataString(broadcastId)}&part=id%2Cstatus", null, token, ct);
        var local = await _db.Broadcasts.FirstOrDefaultAsync(x => x.YouTubeBroadcastId == broadcastId, ct);
        if (local is not null)
        {
            local.Status = transition switch { "testing" => BroadcastStatus.Testing, "live" => BroadcastStatus.Live, _ => BroadcastStatus.Complete };
            await _db.SaveChangesAsync(ct);
        }
        return new BroadcastStartResult(item.Id, item.Status.LifeCycleStatus, $"Broadcast transitioned to {item.Status.LifeCycleStatus}.");
    }

    // ---- Live chat (comments overlay) and live statistics -------------------------------------------------
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ChatIds = new();

    /// <summary>Reads the next page of live-chat messages. The browser passes back NextPageToken, so no message is returned twice.</summary>
    public async Task<YouTubeChatPage> GetChatPageAsync(string broadcastId, string? pageToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > 128)
            throw new ArgumentException("شناسه پخش معتبر نیست.", nameof(broadcastId));
        var token = await GetValidAccessTokenAsync(ct);
        var cacheKey = $"{TenantId}:{broadcastId}";
        if (!ChatIds.TryGetValue(cacheKey, out var chatId))
        {
            var b = await SendYouTubeAsync<BroadcastListResponse>(HttpMethod.Get,
                $"https://www.googleapis.com/youtube/v3/liveBroadcasts?part=snippet&id={Uri.EscapeDataString(broadcastId)}", null, token, ct);
            chatId = b.Items.FirstOrDefault()?.Snippet.LiveChatId;
            if (string.IsNullOrWhiteSpace(chatId)) throw new InvalidOperationException("چت زنده برای این پخش هنوز فعال نشده است.");
            ChatIds[cacheKey] = chatId;
        }
        var url = $"https://www.googleapis.com/youtube/v3/liveChat/messages?liveChatId={Uri.EscapeDataString(chatId)}&part=snippet%2CauthorDetails&maxResults=50";
        if (!string.IsNullOrWhiteSpace(pageToken)) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
        var page = await SendYouTubeAsync<ChatListResponse>(HttpMethod.Get, url, null, token, ct);
        var messages = new List<YouTubeChatMessage>();
        foreach (var item in page.Items)
        {
            var type = item.Snippet.Type;
            if (type is not ("textMessageEvent" or "superChatEvent")) continue;
            var text = type == "superChatEvent" ? item.Snippet.SuperChatDetails?.UserComment : item.Snippet.DisplayMessage;
            if (string.IsNullOrWhiteSpace(text) && type != "superChatEvent") continue;
            messages.Add(new YouTubeChatMessage(item.Id, item.AuthorDetails.DisplayName ?? "", text ?? "", ParseDate(item.Snippet.PublishedAt),
                type == "superChatEvent" ? item.Snippet.SuperChatDetails?.AmountDisplayString : null, item.AuthorDetails.ChannelId));
        }
        return new YouTubeChatPage(messages, page.NextPageToken, Math.Clamp(page.PollingIntervalMillis, 3000, 60000));
    }

    private async Task<string> GetLiveChatIdAsync(string broadcastId, string token, CancellationToken ct)
    {
        var cacheKey = $"{TenantId}:{broadcastId}";
        if (ChatIds.TryGetValue(cacheKey, out var chatId)) return chatId;
        var b = await SendYouTubeAsync<BroadcastListResponse>(HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/liveBroadcasts?part=snippet&id={Uri.EscapeDataString(broadcastId)}", null, token, ct);
        chatId = b.Items.FirstOrDefault()?.Snippet.LiveChatId;
        if (string.IsNullOrWhiteSpace(chatId)) throw new InvalidOperationException("چت زنده برای این پخش هنوز فعال نشده است.");
        ChatIds[cacheKey] = chatId;
        return chatId;
    }

    /// <summary>Removes a viewer from the live chat (temporary timeout or permanent ban). YouTube cannot eject a viewer from watching.</summary>
    public async Task BanChatUserAsync(string broadcastId, string authorChannelId, int? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorChannelId) || authorChannelId.Length > 64) throw new ArgumentException("شناسه بیننده معتبر نیست.", nameof(authorChannelId));
        var token = await GetValidAccessTokenAsync(ct);
        var chatId = await GetLiveChatIdAsync(broadcastId, token, ct);
        object snippet = durationSeconds is > 0
            ? new { liveChatId = chatId, type = "temporary", banDurationSeconds = Math.Min(durationSeconds.Value, 86400), bannedUserDetails = new { channelId = authorChannelId } }
            : new { liveChatId = chatId, type = "permanent", bannedUserDetails = new { channelId = authorChannelId } };
        await SendYouTubeAsync<JsonElement>(HttpMethod.Post, "https://www.googleapis.com/youtube/v3/liveChat/bans?part=snippet", new { snippet }, token, ct);
    }

    public async Task DeleteChatMessageAsync(string messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 200) throw new ArgumentException("شناسه پیام معتبر نیست.", nameof(messageId));
        var token = await GetValidAccessTokenAsync(ct);
        await SendNoContentAsync(HttpMethod.Delete, $"https://www.googleapis.com/youtube/v3/liveChat/messages?id={Uri.EscapeDataString(messageId)}", token, ct);
    }

    /// <summary>Deletes the recording (the broadcast's video) from the channel.</summary>
    public async Task DeleteBroadcastVideoAsync(string broadcastId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > 128) throw new ArgumentException("شناسه پخش معتبر نیست.", nameof(broadcastId));
        var token = await GetValidAccessTokenAsync(ct);
        await SendNoContentAsync(HttpMethod.Delete, $"https://www.googleapis.com/youtube/v3/videos?id={Uri.EscapeDataString(broadcastId)}", token, ct);
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpFactory.CreateClient("YouTube").SendAsync(request, ct);
        await EnsureYouTubeSuccess(response, ct);
    }

    public async Task<YouTubeLiveStats> GetLiveStatsAsync(string broadcastId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > 128)
            throw new ArgumentException("شناسه پخش معتبر نیست.", nameof(broadcastId));
        var token = await GetValidAccessTokenAsync(ct);
        var payload = await SendYouTubeAsync<VideoListResponse>(HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails%2Cstatistics&id={Uri.EscapeDataString(broadcastId)}", null, token, ct);
        var v = payload.Items.FirstOrDefault() ?? throw new InvalidOperationException("ویدئوی پخش پیدا نشد.");
        return new YouTubeLiveStats((int)(ParseCount(v.LiveStreamingDetails?.ConcurrentViewers) ?? 0), ParseCount(v.Statistics?.ViewCount) ?? 0, ParseCount(v.Statistics?.LikeCount) ?? 0);
    }

    private sealed class ChatListResponse
    {
        [JsonPropertyName("items")] public List<ChatItem> Items { get; set; } = [];
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
        [JsonPropertyName("pollingIntervalMillis")] public int PollingIntervalMillis { get; set; } = 8000;
    }
    private sealed class ChatItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("snippet")] public ChatSnippet Snippet { get; set; } = new();
        [JsonPropertyName("authorDetails")] public ChatAuthor AuthorDetails { get; set; } = new();
    }
    private sealed class ChatSnippet
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }
        [JsonPropertyName("displayMessage")] public string? DisplayMessage { get; set; }
        [JsonPropertyName("superChatDetails")] public SuperChat? SuperChatDetails { get; set; }
    }
    private sealed class SuperChat
    {
        [JsonPropertyName("amountDisplayString")] public string? AmountDisplayString { get; set; }
        [JsonPropertyName("userComment")] public string? UserComment { get; set; }
    }
    private sealed class ChatAuthor { [JsonPropertyName("displayName")] public string? DisplayName { get; set; } [JsonPropertyName("channelId")] public string? ChannelId { get; set; } }
    private sealed class VideoListResponse { [JsonPropertyName("items")] public List<VideoItem> Items { get; set; } = []; }
    private sealed class VideoItem
    {
        [JsonPropertyName("liveStreamingDetails")] public LiveDetails? LiveStreamingDetails { get; set; }
        [JsonPropertyName("statistics")] public VideoStats? Statistics { get; set; }
    }
    private sealed class LiveDetails { [JsonPropertyName("concurrentViewers")] public string? ConcurrentViewers { get; set; } }
    private sealed class VideoStats
    {
        [JsonPropertyName("viewCount")] public string? ViewCount { get; set; }
        [JsonPropertyName("likeCount")] public string? LikeCount { get; set; }
    }

    private async Task<T> SendYouTubeAsync<T>(HttpMethod method, string url, object? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpFactory.CreateClient("YouTube").SendAsync(request, ct);
        await EnsureYouTubeSuccess(response, ct);
        if (typeof(T) == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return (T)(object)document.RootElement.Clone();
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("YouTube returned an empty response.");
    }

    private Task<string> GetValidAccessTokenAsync(CancellationToken ct) => _broker.GetValidAccessTokenAsync(TenantId, ct);

    private static async Task EnsureYouTubeSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync(ct);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "دسترسی یوتیوب منقضی یا لغو شده است. از تنظیمات، کانال را دوباره متصل کنید.",
            HttpStatusCode.Forbidden => "YouTube denied this operation. Check OAuth scopes, channel permissions and API quota.",
            HttpStatusCode.NotFound => "The requested YouTube resource was not found.",
            HttpStatusCode.TooManyRequests => "YouTube rate limit reached. Retry later.",
            _ => $"YouTube API request failed ({(int)response.StatusCode}): {SafeError(content)}"
        };
        throw new InvalidOperationException(message);
    }

    private static string SafeError(string value) => value.Length > 700 ? value[..700] : value;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var result) ? result : null;
    private static long? ParseCount(string? value) => long.TryParse(value, System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;

    private sealed class ChannelListResponse { [JsonPropertyName("items")] public List<ChannelItem> Items { get; set; } = []; }
    private sealed class ChannelItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("snippet")] public ChannelSnippet Snippet { get; set; } = new();
        [JsonPropertyName("statistics")] public ChannelStatistics Statistics { get; set; } = new();
        [JsonIgnore] public string Title => Snippet.Title;
        [JsonIgnore] public string? ThumbnailUrl => Snippet.Thumbnails?.Default?.Url;
    }
    private sealed class ChannelSnippet
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("customUrl")] public string? CustomUrl { get; set; }
        [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("thumbnails")] public ThumbnailSet? Thumbnails { get; set; }
    }
    private sealed class ChannelStatistics
    {
        [JsonPropertyName("subscriberCount")] public string? SubscriberCount { get; set; }
        [JsonPropertyName("hiddenSubscriberCount")] public bool HiddenSubscriberCount { get; set; }
        [JsonPropertyName("viewCount")] public string? ViewCount { get; set; }
        [JsonPropertyName("videoCount")] public string? VideoCount { get; set; }
    }
    private sealed class ThumbnailSet
    {
        [JsonPropertyName("default")] public Thumbnail? Default { get; set; }
        [JsonPropertyName("medium")] public Thumbnail? Medium { get; set; }
        [JsonPropertyName("high")] public Thumbnail? High { get; set; }
    }
    private sealed class Thumbnail { [JsonPropertyName("url")] public string? Url { get; set; } }
    private sealed class StreamItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("cdn")] public StreamCdn Cdn { get; set; } = new();
    }
    private sealed class StreamCdn { [JsonPropertyName("ingestionInfo")] public IngestionInfo IngestionInfo { get; set; } = new(); }
    private sealed class IngestionInfo
    {
        [JsonPropertyName("ingestionAddress")] public string IngestionAddress { get; set; } = string.Empty;
        [JsonPropertyName("backupIngestionAddress")] public string? BackupIngestionAddress { get; set; }
        [JsonPropertyName("streamName")] public string StreamName { get; set; } = string.Empty;
    }
    private sealed class BroadcastListResponse { [JsonPropertyName("items")] public List<BroadcastItem> Items { get; set; } = []; }
    private sealed class BroadcastItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("snippet")] public BroadcastSnippet Snippet { get; set; } = new();
        [JsonPropertyName("status")] public BroadcastStatusResponse Status { get; set; } = new();
        [JsonPropertyName("contentDetails")] public BroadcastContentDetails ContentDetails { get; set; } = new();
    }
    private sealed class BroadcastContentDetails
    {
        [JsonPropertyName("boundStreamId")] public string? BoundStreamId { get; set; }
    }
    private sealed class StreamListResponse { [JsonPropertyName("items")] public List<StreamItem> Items { get; set; } = []; }
    private sealed class BroadcastSnippet { [JsonPropertyName("liveChatId")] public string? LiveChatId { get; set; } [JsonPropertyName("title")] public string Title { get; set; } = string.Empty; [JsonPropertyName("scheduledStartTime")] public string? ScheduledStartTime { get; set; } }
    private sealed class BroadcastStatusResponse { [JsonPropertyName("lifeCycleStatus")] public string LifeCycleStatus { get; set; } = "unknown"; }
}
