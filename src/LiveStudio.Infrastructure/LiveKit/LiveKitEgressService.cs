using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.LiveKit;

public sealed record LiveKitEgressStatus(
    bool IsRunning,
    string? EgressId,
    string Status,
    string? Error);

/// <summary>Controls the already-deployed LiveKit Egress worker. RTMP credentials stay server-side.</summary>
public sealed class LiveKitEgressService(
    IConfiguration configuration,
    ILogger<LiveKitEgressService> logger)
{
    private string ServerUrl => (configuration["LiveKit:ServerUrl"] ?? string.Empty).TrimEnd('/');
    private string ApiKey => configuration["LiveKit:ApiKey"] ?? string.Empty;
    private string ApiSecret => configuration["LiveKit:ApiSecret"] ?? string.Empty;

    private EgressServiceClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) ||
            string.IsNullOrWhiteSpace(ApiKey) ||
            string.IsNullOrWhiteSpace(ApiSecret))
            throw new InvalidOperationException("تنظیمات LiveKit برای کنترل Egress کامل نیست.");

        return new EgressServiceClient(ServerUrl, ApiKey, ApiSecret);
    }

    public Task<LiveKitEgressStatus> StartRoomStreamAsync(string roomName, string rtmpUrl, CancellationToken ct)
        => StartRoomStreamAsync(roomName, new[] { rtmpUrl }, ct);

    /// <summary>Starts one egress that pushes the program to every RTMP/RTMPS target (YouTube + extra destinations).</summary>
    public async Task<LiveKitEgressStatus> StartRoomStreamAsync(string roomName, IReadOnlyList<string> rtmpUrls, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            throw new ArgumentException("نام اتاق الزامی است.", nameof(roomName));
        if (rtmpUrls.Count == 0 || rtmpUrls.Count > 8)
            throw new ArgumentException("نشانی RTMP مقصد معتبر نیست.", nameof(rtmpUrls));
        foreach (var candidate in rtmpUrls)
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var target) || (target.Scheme != "rtmp" && target.Scheme != "rtmps"))
                throw new ArgumentException("نشانی RTMP مقصد معتبر نیست.", nameof(rtmpUrls));

        var client = CreateClient();
        var active = await client.ListEgress(new ListEgressRequest { RoomName = roomName, Active = true });
        var existing = active.Items.FirstOrDefault();
        if (existing is not null)
            return ToStatus(existing);

        var request = new StartEgressRequest
        {
            RoomName = roomName,
            Template = new TemplateSource { Layout = "single-speaker" },
            Preset = (EncodingOptionsPreset)2, // H264_1080P_30
            Outputs =
            {
                new Output
                {
                    Stream = new StreamOutput
                    {
                        Protocol = StreamProtocol.Rtmp
                    }
                }
            }
        };
        request.Outputs[0].Stream.Urls.AddRange(rtmpUrls);

        try
        {
            var result = await client.StartEgress(request);
            logger.LogInformation("Started LiveKit Egress {EgressId} for room {RoomName}.", result.EgressId, roomName);
            return ToStatus(result);
        }
        catch (Exception ex)
        {
            logger.LogError("Could not start LiveKit Egress for room {RoomName}: {ErrorType}", roomName, ex.GetType().Name);
            throw;
        }
    }

    public async Task<LiveKitEgressStatus> GetRoomStatusAsync(string roomName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            throw new ArgumentException("نام اتاق الزامی است.", nameof(roomName));

        var response = await CreateClient().ListEgress(new ListEgressRequest { RoomName = roomName });
        var latest = response.Items.OrderByDescending(x => x.StartedAt).FirstOrDefault();
        return latest is null
            ? new LiveKitEgressStatus(false, null, "NOT_STARTED", null)
            : ToStatus(latest);
    }

    public async Task<LiveKitEgressStatus> StopRoomStreamAsync(string roomName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            throw new ArgumentException("نام اتاق الزامی است.", nameof(roomName));

        var client = CreateClient();
        var response = await client.ListEgress(new ListEgressRequest { RoomName = roomName, Active = true });
        EgressInfo? last = null;
        foreach (var item in response.Items)
            last = await client.StopEgress(new StopEgressRequest { EgressId = item.EgressId });

        return last is null
            ? await GetRoomStatusAsync(roomName, ct)
            : ToStatus(last);
    }

    private static LiveKitEgressStatus ToStatus(EgressInfo info)
    {
        var status = info.Status.ToString().ToUpperInvariant();
        if (status.StartsWith("EGRESS", StringComparison.Ordinal) &&
            !status.StartsWith("EGRESS_", StringComparison.Ordinal))
            status = "EGRESS_" + status["EGRESS".Length..];
        var running = status.Contains("STARTING", StringComparison.Ordinal) ||
                      status.Contains("ACTIVE", StringComparison.Ordinal) ||
                      status.Contains("ENDING", StringComparison.Ordinal);
        // Egress error strings can contain destination details; never forward them to the browser.
        return new LiveKitEgressStatus(running, info.EgressId, status,
            string.IsNullOrWhiteSpace(info.Error) ? null : "Egress reported an error. Check the LiveKit Egress container logs.");
    }
}
