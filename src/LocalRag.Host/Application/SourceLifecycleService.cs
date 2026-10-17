using System.Collections.Concurrent;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Management;

namespace LocalRag.Application;

/// <summary>Shared pause/resume application boundary. Pause returns only after the source mutation gate drains.</summary>
public sealed class SourceLifecycleService(ISourceRegistry sources, SourceIndexingStateStore states,
    SourceOperationGate gate, SourceWatcherRegistry watchers, IIndexCoordinator coordinator, HostMaintenanceCoordinator maintenance)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _transitions = new(StringComparer.Ordinal);

    public async Task<SourceIndexingState> SetPausedAsync(string sourceId, bool paused, CancellationToken ct)
    {
        var operational = maintenance.TryAcquireOperational(ct) ?? throw new InvalidOperationException("Maintenance is in progress.");
        await using var operation = operational;
        var transition = _transitions.GetOrAdd(sourceId, _ => new(1, 1));
        await transition.WaitAsync(operation.CancellationToken);
        try
        {
            var source = await sources.GetAsync(sourceId, operation.CancellationToken) ?? throw new ArgumentException("Source not found.");
            if (source.LifecycleState != SourceLifecycleState.Active) throw new InvalidOperationException("Source removal is in progress.");
            var state = await states.GetAsync(sourceId, operation.CancellationToken);
            if (state.UpdatedUtc is null && source.Status == SourceStatus.Paused)
            {
                await states.SetAsync(sourceId, "Paused", "Paused", operation.CancellationToken);
                state = await states.GetAsync(sourceId, operation.CancellationToken);
            }
            if (paused)
            {
                if (state.State == "Paused") return state;
                await states.SetAsync(sourceId, "Paused", "Pausing", operation.CancellationToken);
                watchers.Untrack(sourceId);
                gate.CancelActive(sourceId);
                await using var drained = await gate.AcquireAsync(sourceId, operation.CancellationToken);
                source = await sources.GetAsync(sourceId, drained.CancellationToken) ?? throw new ArgumentException("Source was removed.");
                if (source.LifecycleState != SourceLifecycleState.Active) throw new InvalidOperationException("Source removal is in progress.");
                watchers.Untrack(sourceId);
                await sources.SetStatusAsync(sourceId, SourceStatus.Paused, null, drained.CancellationToken);
                await states.SetAsync(sourceId, "Paused", "Paused", drained.CancellationToken);
            }
            else
            {
                if (state.State == "Running" || (state.State == "Resuming" && source.Status != SourceStatus.Failed && source.Status != SourceStatus.Degraded)) return state;
                await using var drained = await gate.AcquireAsync(sourceId, operation.CancellationToken);
                source = await sources.GetAsync(sourceId, drained.CancellationToken) ?? throw new ArgumentException("Source was removed.");
                if (source.LifecycleState != SourceLifecycleState.Active) throw new InvalidOperationException("Source removal is in progress.");
                await states.SetAsync(sourceId, "Running", "Resuming", drained.CancellationToken);
                await sources.SetStatusAsync(sourceId, SourceStatus.Pending, null, drained.CancellationToken);
                try { await coordinator.ReindexAsync(sourceId, drained.CancellationToken); }
                catch
                {
                    await sources.SetStatusAsync(sourceId, SourceStatus.Degraded, "Resume could not be queued. Restore host dependencies and retry Resume.", CancellationToken.None);
                    throw;
                }
            }
            return await states.GetAsync(sourceId, operation.CancellationToken);
        }
        finally { transition.Release(); }
    }
}
