using LocalRag.Application;
using LocalRag.Domain;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class StartupInitializationService(
    ISourceRegistry sources,
    IIndexStateStore indexState,
    IChunkProfileStateStore chunkProfiles,
    IndexJobStore jobs,
    SourceWatcherRegistry watchers,
    IIndexCoordinator coordinator,
    MissingSourcePolicy missingSourcePolicy,
    ILogger<StartupInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sources.InitializeAsync(cancellationToken);
        await indexState.InitializeAsync(cancellationToken);
        await chunkProfiles.InitializeAsync(cancellationToken);
        await jobs.InitializeAsync(cancellationToken);
        await jobs.RecoverAsync(cancellationToken);
        foreach (var source in await sources.ListAsync(cancellationToken))
        {
            if (!Directory.Exists(source.CanonicalRootPath))
            {
                if (missingSourcePolicy.ShouldCleanup(source, DateTimeOffset.UtcNow))
                {
                    try
                    {
                        await coordinator.RemoveSourceAsync(source.SourceId, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        LogMissingSourceCleanupFailed(logger, exception, source.SourceId);
                    }
                }
                else if (!string.Equals(source.LastError, MissingSourcePolicy.MissingRootMessage, StringComparison.Ordinal))
                {
                    await sources.SetStatusAsync(source.SourceId, SourceStatus.Degraded, MissingSourcePolicy.MissingRootMessage, cancellationToken);
                }
                continue;
            }
            watchers.Track(source);
            await coordinator.QueueInitialIndexAsync(source.SourceId, cancellationToken);
        }
        LogInitialized(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Local RAG state initialized.")]
    private static partial void LogInitialized(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not remove stale source {SourceId}; cleanup will be retried.")]
    private static partial void LogMissingSourceCleanupFailed(ILogger logger, Exception exception, string sourceId);
}
