using System.Collections.Concurrent;
using LocalRag.Application;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Provides deterministic, process-local source locks for profile cutover and retrieval linearization.</summary>
public sealed class ChunkProfileOperationGate : IChunkProfileOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<string> sourceIds,
        CancellationToken cancellationToken)
    {
        var locks = sourceIds.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(sourceId => _locks.GetOrAdd(sourceId, static _ => new SemaphoreSlim(1, 1)))
            .ToArray();
        var acquired = new List<SemaphoreSlim>(locks.Length);
        try
        {
            foreach (var item in locks)
            {
                await item.WaitAsync(cancellationToken);
                acquired.Add(item);
            }
            return new Lease(acquired);
        }
        catch
        {
            for (var index = acquired.Count - 1; index >= 0; index--) acquired[index].Release();
            throw;
        }
    }

    private sealed class Lease(IReadOnlyList<SemaphoreSlim> locks) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            for (var index = locks.Count - 1; index >= 0; index--) locks[index].Release();
            return ValueTask.CompletedTask;
        }
    }
}
