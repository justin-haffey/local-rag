using System.Collections.Concurrent;
using LocalRag.Application;
using LocalRag.Domain;

namespace LocalRag.Host.Tests;

/// <summary>Explicit in-memory intent behavior for isolated index-state test doubles; real durability uses SQLite.</summary>
internal abstract class TestVectorWriteIntents : IVectorWriteIntentStore
{
    private readonly ConcurrentDictionary<(string Source, string Chunk), PendingVectorWrite> _pending = new();
    public Task StageVectorWritesAsync(string sourceId, string fileId, IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var id in chunkIds) _pending.TryAdd((sourceId, id), new(sourceId, fileId, id));
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<PendingVectorWrite>> GetPendingVectorWritesAsync(string sourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PendingVectorWrite>>(_pending.Values.Where(value => value.SourceId == sourceId).ToArray());
    }
    public Task ClearPendingVectorWritesAsync(string sourceId, IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var id in chunkIds) _pending.TryRemove((sourceId, id), out _);
        return Task.CompletedTask;
    }
    protected void CommitVectorIntents(IndexedFile file, IReadOnlyList<ChunkRecord> chunks)
    {
        foreach (var chunk in chunks) _pending.TryRemove((file.SourceId, chunk.ChunkId), out _);
    }
}
