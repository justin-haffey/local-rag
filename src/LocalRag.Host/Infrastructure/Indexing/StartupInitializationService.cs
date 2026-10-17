using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class StartupInitializationService(
    ISourceRegistry sources,
    IIndexStateStore indexState,
    IChunkProfileStateStore chunkProfiles,
    IVectorStoreIdentityStore vectorStoreIdentity,
    IndexJobStore legacyJobs,
    IReconciliationStore reconciliations,
    ReconciliationScheduler scheduler,
    SourceWatcherRegistry watchers,
    IndexCoordinator coordinator,
    MissingSourcePolicy missingSourcePolicy,
    ResetStateStore resetState,
    HostMaintenanceCoordinator maintenance,
    IOptions<LocalRagOptions> options,
    ILogger<StartupInitializationService> logger,
    SourceIndexingStateStore? indexingStates = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var resetRecoveryRequired = resetState.HasIncompleteReset;
        if (resetRecoveryRequired) maintenance.MarkFailed();
        await sources.InitializeAsync(cancellationToken);
        await indexState.InitializeAsync(cancellationToken);
        await chunkProfiles.InitializeAsync(cancellationToken);
        await legacyJobs.InitializeAsync(cancellationToken);
        await reconciliations.InitializeAsync(cancellationToken);
        if (resetRecoveryRequired)
        {
            LogResetRecoveryRequired(logger);
            return;
        }
        await vectorStoreIdentity.InitializeAsync(cancellationToken);
        await vectorStoreIdentity.EnforceAsync(
            SqliteVectorStoreIdentityStore.CreateConfiguredIdentity(options.Value),
            cancellationToken);

        foreach (var sourceId in await reconciliations.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken))
        {
            await scheduler.WakeAsync(sourceId, cancellationToken);
        }

        foreach (var source in await sources.ListAsync(cancellationToken))
        {
            if (source.LifecycleState == SourceLifecycleState.Removing)
            {
                try
                {
                    await coordinator.RemoveSourceAsync(source.SourceId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    LogMissingSourceCleanupFailed(logger, source.SourceId);
                }
                continue;
            }

            if (indexingStates is not null && source.Status == SourceStatus.Paused && (await indexingStates.GetAsync(source.SourceId, cancellationToken)).UpdatedUtc is null)
                await indexingStates.SetAsync(source.SourceId, "Paused", "Paused", cancellationToken);

            if (indexingStates is not null && !(await indexingStates.GetAsync(source.SourceId, cancellationToken)).AllowsWork)
            {
                await indexingStates.SetAsync(source.SourceId, "Paused", "Paused", cancellationToken);
                await sources.SetStatusAsync(source.SourceId, SourceStatus.Paused, null, cancellationToken);
                continue;
            }

            if (indexingStates is not null && (await indexingStates.GetAsync(source.SourceId, cancellationToken)).State == "Resuming" && source.Status == SourceStatus.Paused)
                await sources.SetStatusAsync(source.SourceId, SourceStatus.Pending, null, cancellationToken);

            if (!Directory.Exists(source.CanonicalRootPath))
            {
                if (missingSourcePolicy.ShouldCleanup(source, DateTimeOffset.UtcNow))
                {
                    try
                    {
                        await coordinator.RemoveSourceAsync(source.SourceId, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        LogMissingSourceCleanupFailed(logger, source.SourceId);
                    }
                    continue;
                }

                if (!string.Equals(source.LastError, MissingSourcePolicy.MissingRootMessage, StringComparison.Ordinal))
                {
                    await sources.SetStatusAsync(
                        source.SourceId,
                        SourceStatus.Degraded,
                        MissingSourcePolicy.MissingRootMessage,
                        cancellationToken);
                }
            }
            else
            {
                watchers.Track(source);
            }

            await coordinator.QueueStartupAsync(source.SourceId, cancellationToken);
        }
        LogInitialized(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Local RAG state initialized.")]
    private static partial void LogInitialized(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not remove stale source {SourceId}; cleanup will be retried.")]
    private static partial void LogMissingSourceCleanupFailed(ILogger logger, string sourceId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Local RAG reset recovery is required; indexing startup is suspended.")]
    private static partial void LogResetRecoveryRequired(ILogger logger);
}
