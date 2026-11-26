using LiveStudio.Application.Abstractions;
using LiveStudio.Application.Platform;
using LiveStudio.Infrastructure.LiveKit;
using LiveStudio.Infrastructure.Persistence;
using LiveStudio.Infrastructure.Platform;
using LiveStudio.Infrastructure.YouTube;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // SQLite - the Web and Api processes share one file (see PlatformPaths).
        var connectionString = PlatformPaths.ResolveConnectionString(configuration);
        services.AddDbContextFactory<LiveStudioDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<LiveStudioDbContext>>().CreateDbContext());

        services.AddHttpClient("GoogleOAuth", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("YouTube", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("Otpy", c => { c.BaseAddress = new Uri(new PlatformOptions(configuration).OtpyBaseUrl); c.Timeout = TimeSpan.FromSeconds(15); });
        services.AddHttpClient("Gateway", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("Monitor", c => c.Timeout = TimeSpan.FromSeconds(6));
        services.AddMemoryCache();

        // The key ring MUST be identical for every process that reads or writes encrypted data.
        services.AddDataProtection()
            .SetApplicationName("LiveStudio")
            .PersistKeysToFileSystem(new DirectoryInfo(PlatformPaths.ResolveKeysDirectory(configuration)));

        // Platform services (stateless - they use the DbContext factory, so they are safe as singletons)
        services.AddSingleton<PlatformOptions>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IActivityLogService, ActivityLogService>();
        services.AddSingleton<ISmsOtpClient, OtpyClient>();
        services.AddSingleton<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IMonitoringService, MonitoringService>();
        services.AddSingleton<IPaymentService, PaymentService>();
        services.AddSingleton<ITicketService, TicketService>();
        services.AddSingleton<IContentService, ContentService>();
        services.AddSingleton<IStaffService, StaffService>();
        services.AddSingleton<IAdminStatsService, AdminStatsService>();
        services.AddSingleton<IStreamDestinationService, StreamDestinationService>();
        services.AddSingleton<IStreamSessionService, StreamSessionService>();
        services.AddSingleton<IUserPresetService, UserPresetService>();
        services.AddSingleton<IBroadcastOptionService, BroadcastOptionService>();
        services.AddSingleton<ITeamService, TeamService>();
        services.AddSingleton<IRundownService, RundownService>();
        services.AddSingleton<IMediaStorageService, MediaStorageService>();
        services.AddSingleton<IDriveStorageService, DriveStorageService>();

        // Google / YouTube (per tenant)
        services.AddSingleton<GoogleTokenBroker>();
        services.AddSingleton<IGoogleOAuthTenantService, GoogleOAuthTenantService>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<YouTubeLiveService>();
        services.AddScoped<IYouTubeLiveService>(sp => sp.GetRequiredService<YouTubeLiveService>());

        services.AddScoped<LiveKitService>();
        services.AddScoped<LiveKitEgressService>();
        return services;
    }
}
