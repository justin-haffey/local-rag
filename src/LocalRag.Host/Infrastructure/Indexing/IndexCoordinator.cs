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
    ILogger<IndexCoordinator> logger) : IIndexCoordinator
{
    public async Task QueueInitialIndexAsync(string sourceId, CancellationToken cancellationToken)
    {
        await jobs.QueueAsync(sourceId, cancellationToken);
        await queue.EnqueueAsync(sourceId, cancellationToken);
    }

    public Task ReindexAsync(string sourceId, CancellationToken cancellationToken) => QueueInitialIndexAsync(sourceId, cancellationToken);

    public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        watchers.Untrack(sourceId);
        await vectors.DeleteSourceAsync(sourceId, cancellationToken);
        await sources.RemoveAsync(sourceId, cancellationToken);
    }

    internal async Task<bool> ProcessAsync(string sourceId, CancellationToken cancellationToken)
    {
        var source = await sources.GetAsync(sourceId, cancellationToken);
        if (source is null || source.Status == SourceStatus.Paused) return true;
        if (!Directory.Exists(source.CanonicalRootPath))
        {
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
                await fileIndexing.IndexAsync(source, path, relativePath, info, cancellationToken);
            }

            var existing = await indexState.GetChunksForSourceAsync(sourceId, cancellationToken);
            foreach (var removed in existing.GroupBy(chunk => chunk.RelativePath).Where(group => !observed.Contains(group.Key)))
            {
                await vectors.DeleteAsync(removed.Select(chunk => chunk.ChunkId).ToArray(), cancellationToken);
                await indexState.DeleteFileAsync(sourceId, removed.Key, cancellationToken);
            }

            await sources.SetStatusAsync(sourceId, SourceStatus.Ready, null, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIndexingFailed(logger, exception, sourceId);
            await sources.SetStatusAsync(sourceId, SourceStatus.Degraded, exception.Message, cancellationToken);
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Indexing failed for source {SourceId}")]
    private static partial void LogIndexingFailed(ILogger logger, Exception exception, string sourceId);
}
