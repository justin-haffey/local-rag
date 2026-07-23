using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Periodically schedules durable snapshot reconciliation for every registered source.</summary>
public sealed partial class ReconciliationService(
    ISourceRegistry sources,
    ReconciliationScheduler scheduler,
    IIndexCoordinator coordinator,
    MissingSourcePolicy missingSourcePolicy,
    IOptions<LocalRagOptions> options,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.Indexing.ReconciliationIntervalMinutes);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var source in await sources.ListAsync(stoppingToken))
            {
                try
                {
                    if (source.LifecycleState == SourceLifecycleState.Removing)
                    {
                        await coordinator.RemoveSourceAsync(source.SourceId, stoppingToken);
                        continue;
                    }

                    if (!Directory.Exists(source.CanonicalRootPath))
                    {
                        if (missingSourcePolicy.ShouldCleanup(source, DateTimeOffset.UtcNow))
                        {
                            await coordinator.RemoveSourceAsync(source.SourceId, stoppingToken);
                            continue;
                        }

                        if (!string.Equals(source.LastError, MissingSourcePolicy.MissingRootMessage, StringComparison.Ordinal))
                        {
                            await sources.SetStatusAsync(
                                source.SourceId,
                                SourceStatus.Degraded,
                                MissingSourcePolicy.MissingRootMessage,
                                stoppingToken);
                        }
                    }

                    await scheduler.RequestAsync(source.SourceId, ReconciliationCause.Periodic, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
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
