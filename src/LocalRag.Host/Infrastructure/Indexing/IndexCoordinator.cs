using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class IndexCoordinator(
    ISourceRegistry sources,
    IIndexStateStore indexState,
    IEmbeddingService embeddings,
    IChunker chunker,
    IVectorStore vectors,
    FilePolicy filePolicy,
    SourceWatcherRegistry watchers,
    IndexWorkChannel queue,
    IndexJobStore jobs,
    IOptions<LocalRagOptions> options,
    OperationalMetrics metrics,
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
                await ProcessFileAsync(source, path, relativePath, info, cancellationToken);
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

    private async Task ProcessFileAsync(SourceRecord source, string path, string relativePath, FileInfo info, CancellationToken cancellationToken)
    {
        var existingFile = await indexState.GetFileAsync(source.SourceId, relativePath, cancellationToken);
        if (existingFile is not null && existingFile.SizeBytes == info.Length && existingFile.LastModifiedUtc.UtcDateTime == info.LastWriteTimeUtc)
        {
            return;
        }

        var initialLength = info.Length;
        var initialWriteTime = info.LastWriteTimeUtc;
        if (existingFile is not null)
        {
            await Task.Delay(options.Value.Indexing.StabilityIntervalMilliseconds, cancellationToken);
            info.Refresh();
            if (info.Length != initialLength || info.LastWriteTimeUtc != initialWriteTime)
            {
                throw new IOException($"File '{relativePath}' changed during the configured stability interval.");
            }
        }
        var raw = await File.ReadAllTextAsync(path, cancellationToken);
        var content = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hash = Hash(content);
        if (existingFile?.ContentHash == hash) return;

        var file = new IndexedFile(
            existingFile?.FileId ?? Hash($"{source.SourceId}\n{relativePath}"), source.SourceId, relativePath, hash, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
        var previous = existingFile is null ? Array.Empty<ChunkRecord>() : await indexState.GetChunksForFileAsync(existingFile.FileId, cancellationToken);
        var chunks = chunker.Chunk(source, file, content);
        var previousIds = previous.Select(chunk => chunk.ChunkId).ToHashSet(StringComparer.Ordinal);
        var toEmbed = chunks.Where(chunk => !previousIds.Contains(chunk.ChunkId)).ToArray();
        var documents = new List<VectorDocument>(toEmbed.Length);
        foreach (var chunk in toEmbed)
        {
            documents.Add(new VectorDocument(chunk, await embeddings.EmbedPassageAsync(chunk.Content, cancellationToken)));
        }
        await vectors.UpsertAsync(documents, cancellationToken);
        var currentIds = chunks.Select(chunk => chunk.ChunkId).ToHashSet(StringComparer.Ordinal);
        await vectors.DeleteAsync(previous.Where(chunk => !currentIds.Contains(chunk.ChunkId)).Select(chunk => chunk.ChunkId).ToArray(), cancellationToken);
        await indexState.SaveFileAndChunksAsync(file, chunks, cancellationToken);
        metrics.FileIndexed();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Indexing failed for source {SourceId}")]
    private static partial void LogIndexingFailed(ILogger logger, Exception exception, string sourceId);
}
