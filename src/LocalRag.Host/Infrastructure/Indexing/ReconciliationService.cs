using LocalRag.Application;
using LocalRag.Configuration;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Periodically treats filesystem watchers as hints and schedules source reconciliation scans.</summary>
public sealed partial class ReconciliationService(
    ISourceRegistry sources,
    IIndexCoordinator coordinator,
    MissingSourcePolicy missingSourcePolicy,
    IOptions<LocalRagOptions> options,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.Indexing.ReconciliationIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var source in await sources.ListAsync(stoppingToken))
            {
                try
                {
                    if (!Directory.Exists(source.CanonicalRootPath))
                    {
                        if (missingSourcePolicy.ShouldCleanup(source, DateTimeOffset.UtcNow))
                        {
                            await coordinator.RemoveSourceAsync(source.SourceId, stoppingToken);
                        }
                        else if (!string.Equals(source.LastError, MissingSourcePolicy.MissingRootMessage, StringComparison.Ordinal))
                        {
                            await sources.SetStatusAsync(source.SourceId, Domain.SourceStatus.Degraded, MissingSourcePolicy.MissingRootMessage, stoppingToken);
                        }
                        continue;
                    }

                    await coordinator.ReindexAsync(source.SourceId, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogReconciliationQueueFailed(logger, exception, source.SourceId);
                }
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to queue reconciliation for source {SourceId}")]
    private static partial void LogReconciliationQueueFailed(ILogger logger, Exception exception, string sourceId);
}
