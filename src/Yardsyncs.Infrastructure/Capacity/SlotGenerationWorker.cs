using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Yardsyncs.Infrastructure.Capacity;

/// <summary>
/// Keeps every active terminal's slots generated up to its booking horizon.
/// Config: SlotGeneration:Enabled (default true), SlotGeneration:IntervalHours (default 6).
/// </summary>
public sealed class SlotGenerationWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<SlotGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("SlotGeneration:Enabled", true))
        {
            logger.LogInformation("Slot generation worker is disabled");
            return;
        }

        var interval = TimeSpan.FromHours(config.GetValue("SlotGeneration:IntervalHours", 6.0));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<SlotGenerationService>();
                await service.GenerateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Slot generation run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
