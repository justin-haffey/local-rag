using System.Collections.Concurrent;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Serializes the complete reconciliation/removal mutation window for one source.</summary>
public sealed class SourceOperationGate
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<SourceOperationLease> AcquireAsync(string sourceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var entry = _entries.GetOrAdd(sourceId, static _ => new Entry());
        await entry.Semaphore.WaitAsync(cancellationToken);

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (entry.Sync)
        {
            entry.ActiveOperation = operationCancellation;
        }

        return new SourceOperationLease(entry, operationCancellation);
    }

    /// <summary>Requests cancellation of the operation currently holding the source gate.</summary>
    public void CancelActive(string sourceId)
    {
        if (!_entries.TryGetValue(sourceId, out var entry)) return;
        lock (entry.Sync)
        {
            entry.ActiveOperation?.Cancel();
        }
    }

    internal sealed class Entry
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public CancellationTokenSource? ActiveOperation { get; set; }
    }

    public sealed class SourceOperationLease : IAsyncDisposable
    {
        private Entry? _entry;
        private CancellationTokenSource? _operationCancellation;

        internal SourceOperationLease(Entry entry, CancellationTokenSource operationCancellation)
        {
            _entry = entry;
            _operationCancellation = operationCancellation;
        }

        public CancellationToken CancellationToken =>
            _operationCancellation?.Token ?? throw new ObjectDisposedException(nameof(SourceOperationLease));

        public ValueTask DisposeAsync()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            var operationCancellation = Interlocked.Exchange(ref _operationCancellation, null);
            if (entry is null || operationCancellation is null) return ValueTask.CompletedTask;

            lock (entry.Sync)
            {
                if (ReferenceEquals(entry.ActiveOperation, operationCancellation))
                {
                    entry.ActiveOperation = null;
                }
            }

            operationCancellation.Dispose();
            entry.Semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
