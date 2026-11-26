
using System.Security.Cryptography;
using System.Text;
using LiveStudio.Application.Abstractions;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using LiveStudio.Infrastructure;
using LiveStudio.Infrastructure.Persistence;
using LiveStudio.Infrastructure.YouTube;
using LiveStudio.Infrastructure.LiveKit;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------
// Services
// --------------------------------------------------

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services.AddInfrastructure(builder.Configuration);

// CORS
var allowedOrigins =
    builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:5200"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Dashboard", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// --------------------------------------------------
// Build Application
// --------------------------------------------------

var app = builder.Build();

// --------------------------------------------------
// Database Initialization
// --------------------------------------------------

await DatabaseInitializer.InitializeAsync(app.Services, app.Logger);

if (string.IsNullOrWhiteSpace(builder.Configuration["Platform:InternalApiKey"]))
    throw new InvalidOperationException("Platform:InternalApiKey is not configured. It must be identical in the Web and Api projects.");

// --------------------------------------------------
// Middleware
// --------------------------------------------------

app.UseExceptionHandler();
app.UseCors("Dashboard");

// --------------------------------------------------
// Trusted-caller + tenant context.
// Only the Web application (holding the internal key) may call the YouTube / LiveKit endpoints and it
// states which customer (tenant) it acts for. Blocked users and expired subscriptions are rejected here too.
// --------------------------------------------------
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var guarded = path.StartsWithSegments("/api/v1/youtube") || path.StartsWithSegments("/api/v1/livekit");
    if (!guarded) { await next(); return; }

    var expected = Encoding.UTF8.GetBytes(builder.Configuration["Platform:InternalApiKey"] ?? string.Empty);
    var provided = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Internal-Key"].ToString());
    var keyOk = expected.Length > 0 && provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
    if (!keyOk)
    {
        await Results.Problem("Unauthorized caller.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(ctx);
        return;
    }
    if (path.StartsWithSegments("/api/v1/livekit/status")) { await next(); return; }

    if (!Guid.TryParse(ctx.Request.Headers["X-Tenant-Id"].ToString(), out var tenantId))
    {
        await Results.Problem("شناسه کاربر ارسال نشده است.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(ctx);
        return;
    }

    var factory = ctx.RequestServices.GetRequiredService<IDbContextFactory<LiveStudioDbContext>>();
    await using (var db = await factory.CreateDbContextAsync(ctx.RequestAborted))
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenantId, ctx.RequestAborted);
        if (user is null || user.IsBlocked)
        {
            await Results.Problem("حساب کاربری فعال نیست.", statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(ctx);
            return;
        }

        var mutating = !HttpMethods.IsGet(ctx.Request.Method);
        if (mutating)
        {
            var now = DateTime.UtcNow;
            var active = await db.Subscriptions.AsNoTracking().AnyAsync(x => x.UserId == tenantId && x.Status == SubscriptionStatus.Active && x.EndsAt > now, ctx.RequestAborted);
            if (!active)
            {
                await Results.Problem("اشتراک شما فعال نیست. برای ادامه، اشتراک را تمدید کنید.", statusCode: StatusCodes.Status402PaymentRequired).ExecuteAsync(ctx);
                return;
            }
        }
    }
    ctx.RequestServices.GetRequiredService<ITenantContext>().Set(tenantId);
    await next();
});

// OpenAPI
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// --------------------------------------------------
// Health Check
// --------------------------------------------------

app.MapHealthChecks("/health");

// --------------------------------------------------
// System Status
// --------------------------------------------------

app.MapGet("/api/v1/system/status", () =>
{
    return Results.Ok(new
    {
        application = "LiveStudio",
        version = "0.1.0",
        environment = app.Environment.EnvironmentName,
        database = "SQLite",
        utcNow = DateTimeOffset.UtcNow
    });
});

// --------------------------------------------------
// YouTube Connection Status
// --------------------------------------------------

app.MapGet("/api/v1/youtube/connection",
    async (
        IYouTubeLiveService youtube,
        CancellationToken ct) =>
    {
        return Results.Ok(
            await youtube.GetConnectionStatusAsync(ct));
    });

// --------------------------------------------------
// YouTube Channel Details
// --------------------------------------------------

app.MapGet("/api/v1/youtube/channel",
    async (IYouTubeLiveService youtube, CancellationToken ct) =>
    {
        try
        {
            return Results.Ok(await youtube.GetChannelDetailsAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    });

// --------------------------------------------------
// YouTube Broadcasts - List
// --------------------------------------------------

app.MapGet("/api/v1/youtube/broadcasts",
    async (
        IYouTubeLiveService youtube,
        CancellationToken ct) =>
    {
        try
        {
            return Results.Ok(await youtube.GetBroadcastsAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    });

// --------------------------------------------------
// YouTube Broadcasts - Create
// --------------------------------------------------

app.MapPost("/api/v1/youtube/broadcasts",
    async (
        CreateBroadcastRequest request,
        IYouTubeLiveService youtube,
        IBroadcastOptionService options,
        ITenantContext tenant,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["title"] = ["Title is required."]
                });
        }

        try
        {
            var result = await youtube.CreateBroadcastAsync(
                request,
                ct);

            if (tenant.TenantId is { } ownerId) await options.SetAsync(ownerId, result.Id, request.KeepArchive, ct);
            // Never return the YouTube RTMP stream key to the browser.
            return Results.Ok(new
            {
                result.Id, result.Title, result.Status, result.ScheduledStartAt, result.StreamId
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    });

// --------------------------------------------------
// YouTube Broadcast lifecycle controls
// --------------------------------------------------

app.MapGet("/api/v1/youtube/broadcasts/{id}", async (string id, IYouTubeLiveService youtube, CancellationToken ct) =>
{
    try { return Results.Ok(await youtube.GetBroadcastAsync(id, ct)); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
});

app.MapPost("/api/v1/youtube/broadcasts/{id}/transition/{status}", async (string id, string status, IYouTubeLiveService youtube, CancellationToken ct) =>
{
    try { return Results.Ok(await youtube.TransitionBroadcastAsync(id, status.ToLowerInvariant(), ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
});

// --------------------------------------------------
// LiveKit - health and browser session
// --------------------------------------------------

app.MapGet("/api/v1/livekit/status", async (LiveKitService liveKit, CancellationToken ct) =>
    Results.Ok(await liveKit.GetHealthAsync(ct)));

app.MapPost("/api/v1/youtube/broadcasts/{id}/finalize", async (
    string id,
    YouTubeLiveService youtube,
    IBroadcastOptionService options,
    ITenantContext tenant,
    CancellationToken ct) =>
{
    try
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("نشست کاربر مشخص نیست؛ دوباره وارد شوید.");
        var broadcast = await youtube.GetBroadcastAsync(id, ct); // ownership check
        var status = broadcast.Status?.ToLowerInvariant();
        if (status != "complete")
        {
            var result = await youtube.TransitionBroadcastAsync(id, "complete", ct);
            status = result.Status?.ToLowerInvariant();
        }
        var keep = await options.GetKeepArchiveAsync(tenantId, id, ct) ?? true;
        var deleted = false;
        if (!keep && !await options.IsFinalizedAsync(tenantId, id, ct))
        {
            await youtube.DeleteBroadcastVideoAsync(id, ct);
            deleted = true;
        }
        await options.MarkFinalizedAsync(tenantId, id, ct);
        return Results.Ok(new { status, keepArchive = keep, archiveDeleted = deleted });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube finalize failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("پایان پخش در یوتیوب ناموفق بود.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/youtube/broadcasts/{id}/chat/ban", async (string id, ChatBanRequest request, YouTubeLiveService youtube, CancellationToken ct) =>
{
    try { await youtube.BanChatUserAsync(id, request.AuthorChannelId, request.DurationSeconds, ct); return Results.Ok(new { ok = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube chat ban failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("حذف بیننده از چت ناموفق بود.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/youtube/chat/messages/delete", async (ChatDeleteRequest request, YouTubeLiveService youtube, CancellationToken ct) =>
{
    try { await youtube.DeleteChatMessageAsync(request.MessageId, ct); return Results.Ok(new { ok = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube chat delete failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("حذف پیام ناموفق بود.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/livekit/sessions", async (
    CreateLiveKitSessionRequest request,
    IYouTubeLiveService youtube,
    LiveKitService liveKit,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.BroadcastId))
        return Results.BadRequest(new { error = "شناسه پخش الزامی است." });
    try
    {
        // The session is issued only for a broadcast visible to the connected YouTube account.
        await youtube.GetBroadcastAsync(request.BroadcastId, ct);
        return Results.Ok(await liveKit.CreateSessionAsync(request.BroadcastId, ct, request.Role, request.DisplayName));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("LiveKit session creation failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("ساخت نشست LiveKit ناموفق بود.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// --------------------------------------------------
// YouTube RTMP via the existing LiveKit Egress worker
// --------------------------------------------------

app.MapPost("/api/v1/youtube/broadcasts/{id}/egress/start", async (
    string id,
    YouTubeLiveService youtube,
    LiveKitEgressService egress,
    IStreamDestinationService destinations,
    IStreamSessionService sessions,
    ITenantContext tenant,
    CancellationToken ct) =>
{
    try
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("نشست کاربر مشخص نیست؛ دوباره وارد شوید.");
        // Verify that the broadcast belongs to the connected YouTube account.
        var broadcast = await youtube.GetBroadcastAsync(id, ct);
        var rtmpUrl = await youtube.GetRtmpIngestionUrlAsync(id, ct);
        var roomName = $"broadcast-{id}";

        // YouTube + every enabled multistream destination of this tenant (keys are decrypted here only).
        var extra = await destinations.GetEnabledTargetsAsync(tenantId, ct);
        var urls = new List<string> { rtmpUrl };
        urls.AddRange(extra.Select(x => x.FullUrl));
        var status = await egress.StartRoomStreamAsync(roomName, urls, ct);
        if (status.IsRunning)
            await sessions.StartAsync(tenantId, id, broadcast.Title, string.Join("، ", new[] { "YouTube" }.Concat(extra.Select(x => x.Name))), ct);

        // Never return the RTMP URL, stream key, or YouTube ingestion details.
        return Results.Ok(new
        {
            broadcastId = broadcast.Id,
            roomName,
            status.IsRunning,
            status.EgressId,
            status.Status,
            status.Error,
            destinationCount = urls.Count
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube Egress start failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("شروع خروجی YouTube از طریق LiveKit Egress ناموفق بود.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/youtube/broadcasts/{id}/egress", async (
    string id,
    IYouTubeLiveService youtube,
    LiveKitEgressService egress,
    CancellationToken ct) =>
{
    try
    {
        await youtube.GetBroadcastAsync(id, ct); // ownership check
        var status = await egress.GetRoomStatusAsync($"broadcast-{id}", ct);
        return Results.Ok(status);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube Egress status check failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("دریافت وضعیت خروجی YouTube ناموفق بود.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/youtube/broadcasts/{id}/egress/stop", async (
    string id,
    IYouTubeLiveService youtube,
    LiveKitEgressService egress,
    IStreamSessionService sessions,
    ITenantContext tenant,
    CancellationToken ct) =>
{
    try
    {
        await youtube.GetBroadcastAsync(id, ct); // ownership check
        var status = await egress.StopRoomStreamAsync($"broadcast-{id}", ct);
        if (tenant.TenantId is { } tenantId) await sessions.EndAsync(tenantId, id, ct);
        return Results.Ok(status);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube Egress stop failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("توقف خروجی YouTube ناموفق بود.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// --------------------------------------------------
// Live chat (comments overlay) and live statistics
// --------------------------------------------------

app.MapGet("/api/v1/youtube/broadcasts/{id}/chat", async (
    string id,
    string? pageToken,
    YouTubeLiveService youtube,
    IStreamSessionService sessions,
    ITenantContext tenant,
    CancellationToken ct) =>
{
    try
    {
        var page = await youtube.GetChatPageAsync(id, pageToken, ct);
        if (tenant.TenantId is { } tenantId) await sessions.AddCommentsAsync(tenantId, id, page.Messages.Count, ct);
        return Results.Ok(page);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube chat read failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("دریافت کامنت‌های پخش ناموفق بود.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/youtube/broadcasts/{id}/stats", async (
    string id,
    YouTubeLiveService youtube,
    IStreamSessionService sessions,
    ITenantContext tenant,
    CancellationToken ct) =>
{
    try
    {
        var stats = await youtube.GetLiveStatsAsync(id, ct);
        SessionRow? session = null;
        if (tenant.TenantId is { } tenantId)
            session = await sessions.RecordStatsAsync(tenantId, id, stats.ConcurrentViewers, stats.Views, stats.Likes, ct);
        return Results.Ok(new { stats.ConcurrentViewers, stats.Views, stats.Likes, peakViewers = session?.PeakViewers ?? stats.ConcurrentViewers, comments = session?.Comments ?? 0 });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception ex)
    {
        app.Logger.LogError("YouTube stats read failed: {ErrorType}", ex.GetType().Name);
        return Results.Problem("دریافت آمار پخش ناموفق بود.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// --------------------------------------------------
// Run
// --------------------------------------------------

app.Run();

public sealed record CreateLiveKitSessionRequest(string BroadcastId, string? Role = null, string? DisplayName = null);
public sealed record ChatBanRequest(string AuthorChannelId, int? DurationSeconds);
public sealed record ChatDeleteRequest(string MessageId);

public partial class Program;
