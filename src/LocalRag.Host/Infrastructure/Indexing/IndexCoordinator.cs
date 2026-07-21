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
    IndexWorkChannel queue,
    IndexJobStore jobs,
    IChunkProfileProvider chunkProfile,
    IChunkProfileStateStore chunkProfiles,
    ILogger<IndexCoordinator> logger) : IIndexCoordinator
{
    public async Task QueueInitialIndexAsync(string sourceId, CancellationToken cancellationToken)
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
        await jobs.QueueAsync(sourceId, chunkProfile.Fingerprint, transitionRequired, cancellationToken);
        await queue.EnqueueAsync(sourceId, cancellationToken);
    }

    public Task ReindexAsync(string sourceId, CancellationToken cancellationToken) => QueueInitialIndexAsync(sourceId, cancellationToken);

    public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        watchers.Untrack(sourceId);
        await vectors.DeleteSourceAsync(sourceId, cancellationToken);
        await sources.RemoveAsync(sourceId, cancellationToken);
    }

    internal Task<bool> ProcessAsync(string sourceId, CancellationToken cancellationToken) =>
        ProcessAsync(new IndexJob("direct", sourceId, 0), cancellationToken);

    internal async Task<bool> ProcessAsync(IndexJob job, CancellationToken cancellationToken)
    {
        var sourceId = job.SourceId;
        var source = await sources.GetAsync(sourceId, cancellationToken);
        if (source is null || source.Status == SourceStatus.Paused) return true;
        if (job.ForceContentProcessing && job.TargetChunkProfileFingerprint is not null)
        {
            var state = await chunkProfiles.GetAsync(sourceId, cancellationToken);
            if (state is
                {
                    Status: ChunkProfileStatus.Ready,
                    PendingFingerprint: null
                } && state.ActiveFingerprint == job.TargetChunkProfileFingerprint)
            {
                await sources.SetStatusAsync(sourceId, SourceStatus.Ready, null, cancellationToken);
                return true;
            }
            await chunkProfiles.BeginTransitionAsync(sourceId, job.TargetChunkProfileFingerprint, cancellationToken);
        }
        if (!Directory.Exists(source.CanonicalRootPath))
        {
            if (job.ForceContentProcessing && job.TargetChunkProfileFingerprint is not null)
            {
                await chunkProfiles.FailTransitionAsync(
                    sourceId,
                    job.TargetChunkProfileFingerprint,
                    MissingSourcePolicy.MissingRootMessage,
                    cancellationToken);
            }
            await sources.SetStatusAsync(sourceId, SourceStatus.Degraded, MissingSourcePolicy.MissingRootMessage, cancellationToken);
            return false;
        }

        await sources.SetStatusAsync(sourceId, SourceStatus.Indexing, null, cancellationToken);
        try
        {
            await vectors.EnsureReadyAsync(cancellationToken);
            watchers.Track(source);
            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var path in Directory.EnumerateFiles(source.CanonicalRootPath, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (!filePolicy.IsEligible(source.CanonicalRootPath, path, info)) continue;
                var relativePath = Path.GetRelativePath(source.CanonicalRootPath, path);
                observed.Add(relativePath);
                await fileIndexing.IndexAsync(source, path, relativePath, info, cancellationToken, job.ForceContentProcessing);
            }

            var existing = await indexState.GetChunksForSourceAsync(sourceId, cancellationToken);
            foreach (var removed in existing.GroupBy(chunk => chunk.RelativePath).Where(group => !observed.Contains(group.Key)))
            {
                await vectors.DeleteAsync(removed.Select(chunk => chunk.ChunkId).ToArray(), cancellationToken);
                await indexState.DeleteFileAsync(sourceId, removed.Key, cancellationToken);
            }

            if (job.ForceContentProcessing && job.TargetChunkProfileFingerprint is not null)
            {
                await chunkProfiles.CompleteTransitionAsync(sourceId, job.TargetChunkProfileFingerprint, cancellationToken);
            }
            await sources.SetStatusAsync(sourceId, SourceStatus.Ready, null, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIndexingFailed(logger, exception, sourceId);
            if (job.ForceContentProcessing && job.TargetChunkProfileFingerprint is not null)
            {
                await chunkProfiles.FailTransitionAsync(
                    sourceId,
                    job.TargetChunkProfileFingerprint,
                    exception.Message,
                    cancellationToken);
            }
            await sources.SetStatusAsync(sourceId, SourceStatus.Degraded, exception.Message, cancellationToken);
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Indexing failed for source {SourceId}")]
    private static partial void LogIndexingFailed(ILogger logger, Exception exception, string sourceId);
}
