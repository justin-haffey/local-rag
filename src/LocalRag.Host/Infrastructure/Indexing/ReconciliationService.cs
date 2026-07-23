using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Periodically schedules durable snapshot reconciliation for every registered source.</summary>
public sealed partial class ReconciliationService(
    ISourceRegistry sources,
    ReconciliationScheduler scheduler,
    IIndexCoordinator coordinator,
    MissingSourcePolicy missingSourcePolicy,
    IOptions<LocalRagOptions> options,
    HostMaintenanceCoordinator maintenance,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.Indexing.ReconciliationIntervalMinutes);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var operational = maintenance.TryAcquireOperational(stoppingToken);
            if (operational is null) continue;
            await using var operationalLease = operational;
            foreach (var source in await sources.ListAsync(operational.CancellationToken))
            {
                try
                {
                    if (source.LifecycleState == SourceLifecycleState.Removing)
                    {
                        await coordinator.RemoveSourceAsync(source.SourceId, operational.CancellationToken);
                        continue;
                    }

                    if (!Directory.Exists(source.CanonicalRootPath))
                    {
                        if (missingSourcePolicy.ShouldCleanup(source, DateTimeOffset.UtcNow))
                        {
                            await coordinator.RemoveSourceAsync(source.SourceId, operational.CancellationToken);
                            continue;
                        }

                        if (!string.Equals(source.LastError, MissingSourcePolicy.MissingRootMessage, StringComparison.Ordinal))
                        {
                            await sources.SetStatusAsync(
                                source.SourceId,
                                SourceStatus.Degraded,
                                MissingSourcePolicy.MissingRootMessage,
                                operational.CancellationToken);
                        }
                    }

                    await scheduler.RequestAsync(source.SourceId, ReconciliationCause.Periodic, operational.CancellationToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    LogReconciliationQueueFailed(logger, source.SourceId);
                }
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to queue reconciliation for source {SourceId}")]
    private static partial void LogReconciliationQueueFailed(ILogger logger, string sourceId);
}
