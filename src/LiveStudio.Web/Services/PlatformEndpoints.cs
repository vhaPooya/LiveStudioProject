using System.Security.Claims;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace LiveStudio.Web.Services;

public sealed record TicketBody(string? Ticket);

public static class PlatformEndpoints
{
    public static void Map(WebApplication app)
    {
        // Completes a sign-in that the Blazor circuit already authorised (OTP / admin password verified) by
        // redeeming the one-time ticket and issuing the auth cookie.
        app.MapPost("/auth/session", async ([FromBody] TicketBody body, HttpContext http, IAuthService auth) =>
        {
            var principal = auth.RedeemTicket(body.Ticket ?? string.Empty);
            if (principal is null) return Results.BadRequest(new { error = "نشست ورود منقضی شده است؛ دوباره تلاش کنید." });
            await http.SignInAsync(PrincipalFactory.Scheme, PrincipalFactory.Create(principal),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14) });
            return Results.Ok(new { redirect = principal.Role == Roles.Customer ? "/panel" : "/admin" });
        }).DisableAntiforgery();

        app.MapGet("/auth/logout", async (HttpContext http, IActivityLogService log) =>
        {
            var user = http.User;
            var wasStaff = user.IsInRole(Roles.Admin) || user.IsInRole(Roles.Staff);
            if (user.Identity?.IsAuthenticated == true)
            {
                var type = user.IsInRole(Roles.Admin) ? ActorType.Admin : user.IsInRole(Roles.Staff) ? ActorType.Staff : ActorType.Customer;
                await log.LogAsync(new ActorInfo(type, user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "?", user.FindFirstValue(ClaimTypes.Name) ?? "?", http.Connection.RemoteIpAddress?.ToString()), "auth.logout");
            }
            await http.SignOutAsync(PrincipalFactory.Scheme);
            return Results.Redirect(wasStaff ? "/admin/login" : "/");
        });

        // ---- Google OAuth (shared redirect URI of the software; the client id/secret are the customer's own)
        app.MapGet("/oauth/google/start", async (HttpContext http, IGoogleOAuthTenantService oauth) =>
        {
            var userId = UserId(http);
            if (userId is null) return Results.Redirect("/login");
            var actor = new ActorInfo(ActorType.Customer, userId.ToString()!, http.User.FindFirstValue(ClaimTypes.Name) ?? "", http.Connection.RemoteIpAddress?.ToString());
            var drive = http.Request.Query["drive"] == "1";
            var result = await oauth.BeginAuthorizationAsync(userId.Value, actor, default, drive);
            return result.Success
                ? Results.Redirect(result.Value!)
                : Results.Redirect($"/panel/settings?tab={(drive ? "drive" : "oauth")}&oauth=error&message={Uri.EscapeDataString(result.Error ?? "خطا")}");
        }).RequireAuthorization("Customer");

        app.MapGet("/oauth/google/callback", async (string? code, string? state, string? error, HttpContext http, IGoogleOAuthTenantService oauth) =>
        {
            var result = await oauth.CompleteAuthorizationAsync(code, state, error, UserId(http), http.Connection.RemoteIpAddress?.ToString());
            return Results.Redirect($"/panel/settings?tab={(result.Drive ? "drive" : "oauth")}&oauth={(result.Success ? "ok" : "error")}&message={Uri.EscapeDataString(result.Message)}");
        }).RequireAuthorization("Customer");

        MapMedia(app);

        // ---- Payment gateway callback (anonymous on purpose: verification must happen even if the session expired)
        app.MapGet("/payment/callback", async (HttpContext http, IPaymentService payments) =>
        {
            var query = http.Request.Query;
            var authority = query["Authority"].ToString();
            var status = query["Status"].ToString();
            var result = await payments.HandleCallbackAsync(authority, status, http.Connection.RemoteIpAddress?.ToString());
            return Results.Redirect($"/panel/subscription?pay={(result.Success ? "ok" : "fail")}&message={Uri.EscapeDataString(result.Message)}");
        }).AllowAnonymous();
    }

    public sealed record DriveSessionBody(string? Name, string? MimeType, long Size);
    public sealed record DriveRegisterBody(string? FileId, string? Name, string? Category);

    private static void MapMedia(WebApplication app)
    {
        var g = app.MapGroup("/media-api").RequireAuthorization("Customer");
        // Custom header = CSRF guard for every state-changing call (a cross-site form cannot set it).
        static bool Csrf(HttpContext h) => h.Request.Headers.ContainsKey("X-LS-Upload");

        g.MapGet("/list", async (HttpContext http, IMediaStorageService media, IDriveStorageService drive) =>
        {
            var uid = UserId(http)!.Value;
            var rows = await media.ListAsync(uid);
            var usage = await media.UsageAsync(uid);
            var st = await drive.GetStatusAsync(uid);
            return Results.Json(new { items = rows, usage, drive = new { enabled = st.Enabled } });
        });

        g.MapPost("/upload", async (HttpContext http, IMediaStorageService media, string category) =>
        {
            if (!Csrf(http)) return Results.StatusCode(403);
            var uid = UserId(http)!.Value;
            var name = Uri.UnescapeDataString(http.Request.Headers["X-LS-Name"].ToString());
            var len = http.Request.ContentLength ?? 0;
            var r = await media.AddFileAsync(uid, name, category, http.Request.Body, len, http.RequestAborted);
            return r.Success ? Results.Json(r.Value) : Results.Json(new { error = r.Error }, statusCode: 400);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(25 * 1024 * 1024));

        g.MapPost("/drive/session", async (HttpContext http, [FromBody] DriveSessionBody body, IDriveStorageService drive) =>
        {
            if (!Csrf(http)) return Results.StatusCode(403);
            var uid = UserId(http)!.Value;
            var origin = http.Request.Headers.Origin.ToString();
            var r = await drive.CreateUploadSessionAsync(uid, body.Name ?? "video", body.MimeType ?? "", body.Size, origin, http.RequestAborted);
            return r.Success ? Results.Json(new { url = r.Value }) : Results.Json(new { error = r.Error }, statusCode: 400);
        });

        g.MapPost("/drive/register", async (HttpContext http, [FromBody] DriveRegisterBody body, IMediaStorageService media) =>
        {
            if (!Csrf(http)) return Results.StatusCode(403);
            var r = await media.RegisterDriveFileAsync(UserId(http)!.Value, body.FileId ?? "", body.Name ?? "video", body.Category ?? "video-bg", http.RequestAborted);
            return r.Success ? Results.Json(r.Value) : Results.Json(new { error = r.Error }, statusCode: 400);
        });

        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IMediaStorageService media) =>
        {
            if (!Csrf(http)) return Results.StatusCode(403);
            await media.DeleteAsync(UserId(http)!.Value, id);
            return Results.NoContent();
        });

        // Same-origin delivery (the compositor canvas must never be tainted by cross-origin media).
        g.MapGet("/files/{id:guid}", async (Guid id, HttpContext http, IMediaStorageService media, IDriveStorageService drive) =>
        {
            var uid = UserId(http)!.Value;
            var f = await media.GetAsync(uid, id);
            if (f is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'unsafe-inline'";
            http.Response.Headers.CacheControl = "private, max-age=3600";
            if (f.Storage == "server" && f.StoredName is not null)
            {
                var path = media.GetPath(uid, f.StoredName);
                return File.Exists(path) ? Results.File(path, f.MimeType, enableRangeProcessing: true) : Results.NotFound();
            }
            if (f.Storage == "drive" && f.DriveFileId is not null)
            {
                HttpResponseMessage up;
                try { up = await drive.OpenFileAsync(uid, f.DriveFileId, http.Request.Headers.Range.ToString(), http.RequestAborted); }
                catch (InvalidOperationException) { return Results.StatusCode(503); }
                if (!up.IsSuccessStatusCode && (int)up.StatusCode != 206) { var code = (int)up.StatusCode; up.Dispose(); return Results.StatusCode(code == 404 ? 404 : 502); }
                http.Response.StatusCode = (int)up.StatusCode;
                http.Response.ContentType = f.MimeType;
                http.Response.Headers.AcceptRanges = "bytes";
                if (up.Content.Headers.ContentLength is { } cl) http.Response.ContentLength = cl;
                if (up.Content.Headers.ContentRange is { } cr) http.Response.Headers.ContentRange = cr.ToString();
                using (up) await using (var stream = await up.Content.ReadAsStreamAsync(http.RequestAborted))
                    await stream.CopyToAsync(http.Response.Body, http.RequestAborted);
                return Results.Empty;
            }
            return Results.NotFound();
        });
    }

    private static Guid? UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
