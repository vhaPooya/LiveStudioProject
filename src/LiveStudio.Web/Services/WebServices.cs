using System.Globalization;
using System.Security.Claims;
using LiveStudio.Application.Platform;
using LiveStudio.Domain.Platform;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace LiveStudio.Web.Services;

/// <summary>Remote address / user agent of the browser that opened the circuit.</summary>
public sealed class ClientInfo
{
    public string Ip { get; set; } = "unknown";
    public string UserAgent { get; set; } = string.Empty;
}

public sealed class ClientInfoCircuitHandler(IHttpContextAccessor accessor, ClientInfo info) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext;
        if (context is not null)
        {
            info.Ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            info.UserAgent = context.Request.Headers.UserAgent.ToString();
        }
        return Task.CompletedTask;
    }
}

public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
    public const string Staff = "Staff";
}

public static class PrincipalFactory
{
    public const string Scheme = "LiveStudioAuth";

    public static ClaimsPrincipal Create(SignInPrincipal p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id),
            new(ClaimTypes.Name, p.DisplayName),
            new(ClaimTypes.Role, p.Role)
        };
        claims.AddRange(p.Permissions.Select(x => new Claim("perm", x)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }
}

/// <summary>Convenience access to the signed-in user for components.</summary>
public sealed class CurrentUser(AuthenticationStateProvider auth, ClientInfo client)
{
    public async Task<ClaimsPrincipal> PrincipalAsync() => (await auth.GetAuthenticationStateAsync()).User;

    public async Task<Guid?> UserIdAsync()
    {
        var user = await PrincipalAsync();
        return user.IsInRole(Roles.Customer) && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    }

    public async Task<string> NameAsync() => (await PrincipalAsync()).FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public async Task<ActorInfo> ActorAsync()
    {
        var user = await PrincipalAsync();
        var type = user.IsInRole(Roles.Admin) ? ActorType.Admin : user.IsInRole(Roles.Staff) ? ActorType.Staff : ActorType.Customer;
        return new ActorInfo(type, user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous", user.FindFirstValue(ClaimTypes.Name) ?? "ناشناس", client.Ip);
    }

    public async Task<bool> HasPermissionAsync(string permission)
    {
        var user = await PrincipalAsync();
        return user.IsInRole(Roles.Admin) || (user.IsInRole(Roles.Staff) && user.HasClaim("perm", permission));
    }
}

/// <summary>HttpClient for the YouTube/LiveKit Api, pre-authorised for the signed-in tenant.</summary>
public sealed class TenantApi(IHttpClientFactory factory, AuthenticationStateProvider auth, IConfiguration configuration)
{
    private HttpClient? _client;

    public HttpClient Client => _client ??= Create();

    private HttpClient Create()
    {
        // In Blazor Server the authentication state task is already completed when a component runs.
        var state = auth.GetAuthenticationStateAsync().GetAwaiter().GetResult();
        var client = factory.CreateClient("LiveStudioApi");
        client.DefaultRequestHeaders.Add("X-Internal-Key", configuration["Platform:InternalApiKey"] ?? string.Empty);
        var id = state.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (state.User.IsInRole(Roles.Customer) && Guid.TryParse(id, out _)) client.DefaultRequestHeaders.Add("X-Tenant-Id", id);
        return client;
    }

    /// <summary>Client acting for another tenant. Only <see cref="StudioAccess"/> may call this, after it verified the membership.</summary>
    internal HttpClient ClientFor(Guid tenantId)
    {
        var client = factory.CreateClient("LiveStudioApi");
        client.DefaultRequestHeaders.Add("X-Internal-Key", configuration["Platform:InternalApiKey"] ?? string.Empty);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        return client;
    }
}

/// <summary>What the signed-in user may do inside a studio (their own, or a team studio they were invited to).</summary>
public sealed record StudioContext(Guid OwnerId, Guid UserId, string UserName, string Role, HttpClient Api)
{
    public bool IsOwner => OwnerId == UserId;
    public IReadOnlyList<string> Perms => StudioRoles.PermissionsOf(Role);
    public bool Can(string perm) => Perms.Contains(perm);
}

/// <summary>Resolves the studio context: the owner is always host; invited members get the role the owner granted.</summary>
public sealed class StudioAccess(CurrentUser user, ITeamService team, TenantApi api)
{
    public async Task<StudioContext?> ResolveAsync(Guid? ownerId)
    {
        var uid = await user.UserIdAsync();
        if (uid is null) return null;
        var owner = ownerId ?? uid.Value;
        var role = await team.GetRoleAsync(owner, uid.Value);
        if (role is null) return null;
        var client = owner == uid.Value ? api.Client : api.ClientFor(owner);
        return new StudioContext(owner, uid.Value, await user.NameAsync(), role, client);
    }
}

/// <summary>Jalali dates and Persian number formatting used by every panel.</summary>
public static class Fa
{
    private static readonly PersianCalendar Calendar = new();
    private static readonly TimeZoneInfo Tehran = FindTehran();

    private static TimeZoneInfo FindTehran()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tehran);

    public static string Date(DateTime? utc, bool withTime = false)
    {
        if (utc is null) return "—";
        var d = ToLocal(utc.Value);
        var text = $"{Calendar.GetYear(d):0000}/{Calendar.GetMonth(d):00}/{Calendar.GetDayOfMonth(d):00}";
        return withTime ? $"{text} {d:HH:mm}" : text;
    }

    public static string Money(long toman) => toman.ToString("N0", CultureInfo.InvariantCulture) + " تومان";
    public static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string TicketStatusText(TicketStatus s) => s switch
    {
        Domain.Platform.TicketStatus.Open => "باز", Domain.Platform.TicketStatus.Answered => "پاسخ داده شد",
        Domain.Platform.TicketStatus.CustomerReply => "پاسخ مشتری", _ => "بسته"
    };
    public static string TicketStatusClass(TicketStatus s) => s switch
    {
        Domain.Platform.TicketStatus.Open => "b-blue", Domain.Platform.TicketStatus.Answered => "b-green",
        Domain.Platform.TicketStatus.CustomerReply => "b-amber", _ => "b-gray"
    };
    public static string Priority(TicketPriority p) => p switch { TicketPriority.Low => "کم", TicketPriority.Normal => "عادی", TicketPriority.High => "زیاد", _ => "فوری" };
    public static string PriorityClass(TicketPriority p) => p switch { TicketPriority.Low => "b-gray", TicketPriority.Normal => "b-blue", TicketPriority.High => "b-amber", _ => "b-red" };
    public static string Department(string d) => d switch { "billing" => "مالی", "technical" => "فنی", "sales" => "فروش", _ => "پشتیبانی" };
    public static string PaymentStatusText(PaymentStatus s) => s switch
    {
        Domain.Platform.PaymentStatus.Paid => "موفق", Domain.Platform.PaymentStatus.Pending => "در انتظار",
        Domain.Platform.PaymentStatus.Failed => "ناموفق", Domain.Platform.PaymentStatus.Cancelled => "لغو شده", _ => "برگشت‌خورده"
    };
    public static string PaymentStatusClass(PaymentStatus s) => s switch
    {
        Domain.Platform.PaymentStatus.Paid => "b-green", Domain.Platform.PaymentStatus.Pending => "b-amber",
        Domain.Platform.PaymentStatus.Failed => "b-red", Domain.Platform.PaymentStatus.Cancelled => "b-gray", _ => "b-blue"
    };
    public static string OAuthState(OAuthConnectionState s) => s switch
    {
        OAuthConnectionState.Connected => "متصل", OAuthConnectionState.Saved => "ذخیره‌شده", OAuthConnectionState.AwaitingConsent => "در انتظار تأیید",
        OAuthConnectionState.Error => "خطا", _ => "تنظیم نشده"
    };
    public static string OAuthStateClass(OAuthConnectionState s) => s switch
    {
        OAuthConnectionState.Connected => "b-green", OAuthConnectionState.Saved => "b-blue", OAuthConnectionState.AwaitingConsent => "b-amber",
        OAuthConnectionState.Error => "b-red", _ => "b-gray"
    };
    public static string Severity(AlertSeverity s) => s switch { AlertSeverity.Critical => "بحرانی", AlertSeverity.Warning => "هشدار", _ => "اطلاع" };
    public static string SeverityClass(AlertSeverity s) => s switch { AlertSeverity.Critical => "b-red", AlertSeverity.Warning => "b-amber", _ => "b-blue" };
    public static string Actor(ActorType t) => t switch { ActorType.Customer => "مشتری", ActorType.Admin => "مدیر", ActorType.Staff => "کارمند", _ => "سیستم" };
    public static string Duration(int days) => days switch { 30 or 31 => "ماهانه", 90 or 91 or 92 => "سه‌ماهه", 180 or 182 or 183 => "شش‌ماهه", 365 or 366 => "سالانه", _ => days + " روزه" };
    public static string Initials(string name) => string.IsNullOrWhiteSpace(name) ? "؟" : name.Trim()[0].ToString();
}
