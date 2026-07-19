using LocalRag.Application;
using LocalRag.Domain;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class StartupInitializationService(
    ISourceRegistry sources,
    IIndexStateStore indexState,
    IndexJobStore jobs,
    SourceWatcherRegistry watchers,
    IIndexCoordinator coordinator,
    ILogger<StartupInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sources.InitializeAsync(cancellationToken);
        await indexState.InitializeAsync(cancellationToken);
        await jobs.InitializeAsync(cancellationToken);
        await jobs.RecoverAsync(cancellationToken);
        foreach (var source in await sources.ListAsync(cancellationToken))
        {
            if (!Directory.Exists(source.CanonicalRootPath))
            {
                await sources.SetStatusAsync(source.SourceId, SourceStatus.Degraded, "Source root is no longer accessible.", cancellationToken);
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
}
