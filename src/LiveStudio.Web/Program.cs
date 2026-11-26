using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using LiveStudio.Infrastructure;
using LiveStudio.Infrastructure.Persistence;
using LiveStudio.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHttpClient("LiveStudioApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5080");
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ---- Authentication: one cookie scheme for customers, staff and the built-in admin.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(PrincipalFactory.Scheme).AddCookie(PrincipalFactory.Scheme, options =>
{
    options.Cookie.Name = ".LiveStudio.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.Events.OnRedirectToLogin = ctx =>
    {
        var target = ctx.Request.Path.StartsWithSegments("/admin") ? "/admin/login" : "/login";
        ctx.Response.Redirect($"{target}?returnUrl={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async ctx =>
    {
        var services = ctx.HttpContext.RequestServices;
        var role = ctx.Principal?.FindFirstValue(ClaimTypes.Role);
        var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (role is null || id is null) { ctx.RejectPrincipal(); return; }

        var cache = services.GetRequiredService<IMemoryCache>();
        var auth = services.GetRequiredService<IAuthService>();
        var key = $"auth:valid:{role}:{id}";
        if (!cache.TryGetValue(key, out bool valid))
        {
            valid = await auth.IsPrincipalValidAsync(role, id);
            cache.Set(key, valid, TimeSpan.FromSeconds(45));
        }
        if (!valid) { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(PrincipalFactory.Scheme); return; }

        if (role == Roles.Staff)
        {
            var permKey = $"auth:perms:{id}";
            if (!cache.TryGetValue(permKey, out IReadOnlyList<string>? current) || current is null)
            {
                current = await auth.GetStaffPermissionsAsync(id);
                cache.Set(permKey, current, TimeSpan.FromSeconds(45));
            }
            var claimed = ctx.Principal!.FindAll("perm").Select(c => c.Value).OrderBy(x => x).ToList();
            if (!claimed.SequenceEqual(current.OrderBy(x => x)))
            {
                var name = ctx.Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
                ctx.ReplacePrincipal(PrincipalFactory.Create(new SignInPrincipal(id, name, role, current)));
                ctx.ShouldRenew = true;
            }
        }
    };
});

var authorization = builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Customer", p => p.RequireRole(Roles.Customer))
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin, Roles.Staff));
foreach (var (key, _, _) in Permissions.All)
{
    var permission = key;
    authorization.AddPolicy($"perm:{permission}", p => p.RequireAssertion(ctx =>
        ctx.User.IsInRole(Roles.Admin) || (ctx.User.IsInRole(Roles.Staff) && ctx.User.HasClaim("perm", permission))));
}

builder.Services.AddScoped<ClientInfo>();
builder.Services.AddScoped<CircuitHandler, ClientInfoCircuitHandler>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<TenantApi>();
builder.Services.AddScoped<StudioAccess>();
builder.Services.AddHostedService<HealthMonitorWorker>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // The app runs behind a reverse proxy (Caddy/Nginx) inside the Docker network.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddHealthChecks();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services, app.Logger);
if (string.IsNullOrWhiteSpace(app.Configuration["Platform:InternalApiKey"]))
    throw new InvalidOperationException("Platform:InternalApiKey is not configured. It must be identical in the Web and Api projects.");
if (!app.Environment.IsDevelopment())
{
    // Fail fast on unsafe production configuration.
    var problems = new List<string>();
    var internalKey = app.Configuration["Platform:InternalApiKey"] ?? string.Empty;
    if (internalKey.Length < 24 || internalKey.Contains("livestudio-2026", StringComparison.OrdinalIgnoreCase) || internalKey.Contains("CHANGE", StringComparison.OrdinalIgnoreCase))
        problems.Add("Platform__InternalApiKey must be a unique random value of at least 24 characters (identical in Web and Api).");
    var adminPassword = app.Configuration["Admin:Password"] ?? string.Empty;
    if (adminPassword.Length < 10 || adminPassword.Equals("LiveStudio@Admin2026", StringComparison.Ordinal))
        problems.Add("Admin__Password must be set to a strong password (at least 10 characters).");
    if (app.Configuration.GetValue<bool>("Platform:AllowPaymentSimulator"))
        problems.Add("Platform__AllowPaymentSimulator must be false in production.");
    if (!(app.Configuration["Platform:PublicBaseUrl"] ?? string.Empty).StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        problems.Add("Platform__PublicBaseUrl must be the public https:// address of the site (it is used for the Google OAuth redirect).");
    if (problems.Count > 0)
        throw new InvalidOperationException("Unsafe production configuration:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", problems));
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); }
if (app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "SAMEORIGIN";
    // The studio needs camera, microphone and screen capture from this origin only.
    h["Permissions-Policy"] = "camera=(self), microphone=(self), display-capture=(self), geolocation=(), payment=(self)";
    await next();
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/healthz").AllowAnonymous();
PlatformEndpoints.Map(app);
app.MapRazorComponents<LiveStudio.Web.Components.App>().AddInteractiveServerRenderMode();
app.Run();
