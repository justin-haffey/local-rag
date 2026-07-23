using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Processing;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class IndexCoordinator(
    ISourceRegistry sources,
    IIndexStateStore indexState,
    IVectorStore vectors,
    FileIndexingService fileIndexing,
    FilePolicy filePolicy,
    SourceWatcherRegistry watchers,
    ReconciliationScheduler scheduler,
    IReconciliationStore reconciliations,
    SourceOperationGate operationGate,
    IChunkProfileProvider chunkProfile,
    IChunkProfileStateStore chunkProfiles,
    ILogger<IndexCoordinator> logger) : IIndexCoordinator
{
    public Task QueueInitialIndexAsync(string sourceId, CancellationToken cancellationToken) =>
        QueueAsync(sourceId, ReconciliationCause.Initial, cancellationToken);

    public Task ReindexAsync(string sourceId, CancellationToken cancellationToken) =>
        QueueAsync(sourceId, ReconciliationCause.Manual, cancellationToken);

    internal Task QueueStartupAsync(string sourceId, CancellationToken cancellationToken) =>
        QueueAsync(sourceId, ReconciliationCause.Startup, cancellationToken);

    private async Task QueueAsync(
        string sourceId,
        ReconciliationCause cause,
        CancellationToken cancellationToken)
    {
        var existingChunks = await indexState.GetChunksForSourceAsync(sourceId, cancellationToken);
        var state = await chunkProfiles.GetOrCreateAsync(
            sourceId,
            chunkProfile.Fingerprint,
            existingChunks.Count > 0,
            cancellationToken);
        var transitionRequired = state.ActiveFingerprint != chunkProfile.Fingerprint ||
            state.Status != ChunkProfileStatus.Ready || state.PendingFingerprint is not null;
        if (transitionRequired)
        {
            await chunkProfiles.BeginTransitionAsync(sourceId, chunkProfile.Fingerprint, cancellationToken);
        }

        await scheduler.RequestAsync(
            sourceId,
            cause,
            chunkProfile.Fingerprint,
            transitionRequired,
            cancellationToken);
    }

    public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        var tombstone = await reconciliations.TombstoneAsync(sourceId, cancellationToken);
        if (tombstone is null) return;

        operationGate.CancelActive(sourceId);
        await using var removal = await operationGate.AcquireAsync(sourceId, cancellationToken);
        var current = await sources.GetAsync(sourceId, removal.CancellationToken);
        if (current is null ||
            current.LifecycleState != SourceLifecycleState.Removing ||
            current.LifecycleEpoch != tombstone.Epoch)
        {
            throw new InvalidOperationException("The source removal fence changed before cleanup completed.");
        }

        watchers.Untrack(sourceId);
        await vectors.DeleteSourceAsync(sourceId, removal.CancellationToken);
        await sources.RemoveAsync(sourceId, removal.CancellationToken);
    }

    internal async Task<bool> ProcessAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessCoreAsync(sourceId, lease: null, cancellationToken);
            await sources.SetStatusAsync(sourceId, SourceStatus.Ready, null, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal Task<ReconciliationExecutionResult> ProcessAsync(
        ReconciliationLease lease,
        CancellationToken cancellationToken) =>
        ProcessCoreAsync(lease.SourceId, lease, cancellationToken);

    private async Task<ReconciliationExecutionResult> ProcessCoreAsync(
        string sourceId,
        ReconciliationLease? lease,
        CancellationToken cancellationToken)
    {
        var source = await sources.GetAsync(sourceId, cancellationToken);
        if (source is null)
        {
            throw new ReconciliationProcessingException(ReconciliationFailureCode.Cancelled);
        }
        if (source.Status == SourceStatus.Paused)
        {
            throw new OperationCanceledException("Paused sources cannot be reconciled.");
        }

        var forceContentProcessing = lease?.ForceContentProcessing ?? false;
        var targetFingerprint = lease?.TargetChunkProfileFingerprint;
        if (forceContentProcessing && targetFingerprint is not null)
        {
            var profileState = await chunkProfiles.GetAsync(sourceId, cancellationToken);
            if (profileState is not
                {
                    Status: ChunkProfileStatus.Ready,
                    PendingFingerprint: null
                } || profileState.ActiveFingerprint != targetFingerprint)
            {
                await chunkProfiles.BeginTransitionAsync(sourceId, targetFingerprint, cancellationToken);
            }
            else
            {
                forceContentProcessing = false;
            }
        }

        if (!Directory.Exists(source.CanonicalRootPath))
        {
            var failure = new ReconciliationFailure(ReconciliationFailureCode.SourceMissing);
            if (forceContentProcessing && targetFingerprint is not null)
            {
                await chunkProfiles.FailTransitionAsync(sourceId, targetFingerprint, failure.SafeSummary, cancellationToken);
            }
            await sources.SetStatusAsync(sourceId, SourceStatus.Degraded, MissingSourcePolicy.MissingRootMessage, cancellationToken);
            throw new ReconciliationProcessingException(failure.Code);
        }

        await sources.SetStatusAsync(sourceId, SourceStatus.Indexing, null, cancellationToken);
        try
        {
            await EnsureLifecycleCurrentAsync(lease, cancellationToken);
            await vectors.EnsureReadyAsync(cancellationToken);
            watchers.Track(source);
            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = 0;
            var unchanged = 0;
            var embeddings = 0;
            var upserts = 0;
            var enumeration = CreateEnumerationOptions();
            foreach (var path in Directory.EnumerateFiles(source.CanonicalRootPath, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (!filePolicy.IsEligible(source.CanonicalRootPath, path, info)) continue;
                var relativePath = Path.GetRelativePath(source.CanonicalRootPath, path);
                observed.Add(relativePath);
                var outcome = await fileIndexing.IndexAsync(
                    source,
                    path,
                    relativePath,
                    info,
                    cancellationToken,
                    forceContentProcessing,
                    lease);
                if (outcome.Changed) changed++;
                else unchanged++;
                embeddings += outcome.EmbeddingCount;
                upserts += outcome.UpsertCount;
            }

            var deleted = 0;
            var existing = await indexState.GetChunksForSourceAsync(sourceId, cancellationToken);
            foreach (var removed in existing.GroupBy(chunk => chunk.RelativePath).Where(group => !observed.Contains(group.Key)))
            {
                await EnsureLifecycleCurrentAsync(lease, cancellationToken);
                await vectors.DeleteAsync(removed.Select(chunk => chunk.ChunkId).ToArray(), cancellationToken);
                await EnsureLifecycleCurrentAsync(lease, cancellationToken);
                await indexState.DeleteFileAsync(sourceId, removed.Key, cancellationToken);
                deleted++;
            }

            if (forceContentProcessing && targetFingerprint is not null)
            {
                await chunkProfiles.CompleteTransitionAsync(sourceId, targetFingerprint, cancellationToken);
            }
            if (lease is not null && !await chunkProfiles.IsQueryVisibleAsync(sourceId, cancellationToken))
            {
                throw new ReconciliationProcessingException(ReconciliationFailureCode.StateCorrupt);
            }

            return new ReconciliationExecutionResult(
                new ReconciliationResult(changed, deleted, unchanged),
                embeddings,
                upserts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReconciliationProcessingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = Classify(exception);
            var failure = new ReconciliationFailure(code);
            LogIndexingFailed(logger, sourceId, code);
            if (forceContentProcessing && targetFingerprint is not null)
            {
                await chunkProfiles.FailTransitionAsync(sourceId, targetFingerprint, failure.SafeSummary, cancellationToken);
            }
            await sources.SetStatusAsync(sourceId, SourceStatus.Degraded, failure.SafeSummary, cancellationToken);
            throw new ReconciliationProcessingException(code, exception);
        }
    }

    private async Task EnsureLifecycleCurrentAsync(
        ReconciliationLease? lease,
        CancellationToken cancellationToken)
    {
        if (lease is null) return;
        if (!await reconciliations.IsLifecycleCurrentAsync(lease.SourceId, lease.LifecycleEpoch, cancellationToken))
        {
            throw new OperationCanceledException("The source lifecycle changed during reconciliation.");
        }
    }

    internal static EnumerationOptions CreateEnumerationOptions() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static ReconciliationFailureCode Classify(Exception exception) => exception switch
    {
        DirectoryNotFoundException => ReconciliationFailureCode.SourceMissing,
        UnauthorizedAccessException => ReconciliationFailureCode.FileUnstable,
        IOException => ReconciliationFailureCode.FileUnstable,
        HttpRequestException or TimeoutException => ReconciliationFailureCode.DependencyUnavailable,
        _ => ReconciliationFailureCode.Unexpected
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Indexing failed for source {SourceId} with {FailureCode}")]
    private static partial void LogIndexingFailed(
        ILogger logger,
        string sourceId,
        ReconciliationFailureCode failureCode);
}

internal sealed record ReconciliationExecutionResult(
    ReconciliationResult Result,
    int EmbeddingCount,
    int UpsertCount);

internal sealed class ReconciliationProcessingException : Exception
{
    public ReconciliationProcessingException(ReconciliationFailureCode code, Exception? innerException = null)
        : base(new ReconciliationFailure(code).SafeSummary, innerException)
    {
        Code = code;
    }

    public ReconciliationFailureCode Code { get; }
}
