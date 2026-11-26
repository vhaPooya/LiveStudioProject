using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Infrastructure.LiveKit;

public sealed record LiveKitSession(
    string Url,
    string Token,
    string RoomName,
    string ParticipantIdentity,
    DateTimeOffset ExpiresAt);

public sealed record LiveKitHealth(bool Configured, bool Reachable, int ActiveRooms, string? Error);

/// <summary>Issues short-lived, room-scoped browser credentials. API keys and secrets never leave this service.</summary>
public sealed class LiveKitService(IConfiguration configuration, ILogger<LiveKitService> logger)
{
    private string ServerUrl => (configuration["LiveKit:ServerUrl"] ?? string.Empty).TrimEnd('/');
    private string PublicUrl => configuration["LiveKit:PublicUrl"] ?? string.Empty;
    private string ApiKey => configuration["LiveKit:ApiKey"] ?? string.Empty;
    private string ApiSecret => configuration["LiveKit:ApiSecret"] ?? string.Empty;

    private bool IsConfigured =>
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out _) &&
        Uri.TryCreate(PublicUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ApiSecret);

    public async Task<LiveKitSession> CreateSessionAsync(string broadcastId, CancellationToken ct, string? role = null, string? displayName = null)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > 128 ||
            broadcastId.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("شناسه پخش معتبر نیست.", nameof(broadcastId));

        var roomName = $"broadcast-{broadcastId}";
        // Team members get an identity that carries their studio role; the token is signed by the server, so it cannot be forged.
        var identity = role is { Length: > 0 } && role != "host" && role.All(char.IsLetter) ? $"team-{role}-{Guid.NewGuid():N}" : $"host-{Guid.NewGuid():N}";
        var participantName = string.IsNullOrWhiteSpace(displayName) ? "LiveStudio Host" : displayName.Trim()[..Math.Min(displayName.Trim().Length, 40)];
        var isTeam = identity.StartsWith("team-", StringComparison.Ordinal);
        var roomClient = new RoomServiceClient(ServerUrl, ApiKey, ApiSecret);
        var rooms = await roomClient.ListRooms(new ListRoomsRequest());
        if (!rooms.Rooms.Any(x => string.Equals(x.Name, roomName, StringComparison.Ordinal)))
        {
            await roomClient.CreateRoom(new CreateRoomRequest
            {
                Name = roomName,
                EmptyTimeout = 300,
                MaxParticipants = 10
            });
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);
        var token = new AccessToken(ApiKey, ApiSecret)
            .WithIdentity(identity)
            .WithName(participantName)
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = !isTeam,
                CanSubscribe = true,
                CanPublishData = true
            })
            .WithTtl(TimeSpan.FromMinutes(20))
            .ToJwt();

        return new LiveKitSession(PublicUrl, token, roomName, identity, expiresAt);
    }

    public async Task<LiveKitHealth> GetHealthAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            return new LiveKitHealth(false, false, 0, "تنظیمات LiveKit کامل نیست.");
        try
        {
            var client = new RoomServiceClient(ServerUrl, ApiKey, ApiSecret);
            var rooms = await client.ListRooms(new ListRoomsRequest());
            return new LiveKitHealth(true, true, rooms.Rooms.Count, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("LiveKit health check failed: {ErrorType}", ex.GetType().Name);
            return new LiveKitHealth(true, false, 0, "ارتباط با LiveKit برقرار نشد.");
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("تنظیمات LiveKit را در بخش LiveKit در appsettings یا متغیرهای محیطی کامل کنید.");
    }
}
