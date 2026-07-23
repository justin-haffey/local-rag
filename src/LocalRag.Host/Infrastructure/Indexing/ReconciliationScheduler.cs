using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Persists reconciliation intent before emitting a process-local wakeup hint.</summary>
public sealed class ReconciliationScheduler(
    IReconciliationStore store,
    IndexWorkChannel wakeups,
    OperationalMetrics metrics,
    ReconciliationDispatchSignal? dispatchSignal = null)
{
    public Task<ReconciliationRequestResult> RequestAsync(
        string sourceId,
        ReconciliationCause causes,
        CancellationToken cancellationToken) =>
        RequestAsync(sourceId, causes, null, false, cancellationToken);

    public async Task<ReconciliationRequestResult> RequestAsync(
        string sourceId,
        ReconciliationCause causes,
        string? targetChunkProfileFingerprint = null,
        bool forceContentProcessing = false,
        CancellationToken cancellationToken = default)
    {
        if (causes == ReconciliationCause.None)
        {
            throw new ArgumentOutOfRangeException(nameof(causes), "At least one bounded reconciliation cause is required.");
        }

        var result = await store.RequestAsync(
            new ReconciliationRequest(sourceId, causes, targetChunkProfileFingerprint, forceContentProcessing),
            cancellationToken);
        dispatchSignal?.Notify();
        metrics.ReconciliationRequested(causes);
        if ((causes & ReconciliationCause.WatcherOverflow) != 0) metrics.WatcherOverflowed();
        if (result.WakeRequired)
        {
            await wakeups.EnqueueAsync(sourceId, cancellationToken);
        }

        return result;
    }

    internal ValueTask WakeAsync(string sourceId, CancellationToken cancellationToken) =>
        wakeups.EnqueueAsync(sourceId, cancellationToken);
}
