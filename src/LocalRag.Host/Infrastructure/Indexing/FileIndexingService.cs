using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Extracts, chunks, embeds, and persists one eligible file.</summary>
public sealed class FileIndexingService(
    IIndexStateStore indexState,
    IEmbeddingService embeddings,
    IChunker chunker,
    IVectorStore vectors,
    ContentExtractionService contentExtraction,
    IOptions<LocalRagOptions> options,
    OperationalMetrics metrics)
{
    public async Task IndexAsync(SourceRecord source, string path, string relativePath, FileInfo info, CancellationToken cancellationToken)
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

        var content = await contentExtraction.ExtractAsync(path, cancellationToken);
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
}
