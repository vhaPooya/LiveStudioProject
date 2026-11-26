using LiveStudio.Application.Platform;

namespace LiveStudio.Web.Services;

/// <summary>Runs the platform health checks periodically so that admins get alerts without opening the page.</summary>
public sealed class HealthMonitorWorker(IMonitoringService monitoring, ISettingsService settings, ILogger<HealthMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 60;
            try
            {
                interval = Math.Clamp(await settings.GetIntAsync(SettingKeys.MonitorIntervalSeconds, 60, stoppingToken), 15, 3600);
                if (await settings.GetBoolAsync(SettingKeys.MonitorEnabled, true, stoppingToken))
                    await monitoring.CheckNowAsync(true, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogWarning("Health monitor cycle failed: {Message}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
