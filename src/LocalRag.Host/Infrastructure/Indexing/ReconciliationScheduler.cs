using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Management;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Persists reconciliation intent before emitting a process-local wakeup hint.</summary>
public sealed class ReconciliationScheduler(
    IReconciliationStore store,
    IndexWorkChannel wakeups,
    OperationalMetrics metrics,
    ReconciliationDispatchSignal? dispatchSignal = null,
    HostMaintenanceCoordinator? maintenance = null)
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

        var operational = maintenance?.TryAcquireOperational(cancellationToken);
        if (maintenance is not null && operational is null)
        {
            throw new InvalidOperationException("Local RAG maintenance is in progress.");
        }
        await using var _ = operational;
        var effectiveToken = operational?.CancellationToken ?? cancellationToken;
        var result = await store.RequestAsync(
            new ReconciliationRequest(sourceId, causes, targetChunkProfileFingerprint, forceContentProcessing),
            effectiveToken);
        dispatchSignal?.Notify();
        metrics.ReconciliationRequested(causes);
        if ((causes & ReconciliationCause.WatcherOverflow) != 0) metrics.WatcherOverflowed();
        if (result.WakeRequired)
        {
            await wakeups.EnqueueAsync(sourceId, effectiveToken);
        }

        return result;
    }

    internal ValueTask WakeAsync(string sourceId, CancellationToken cancellationToken) =>
        wakeups.EnqueueAsync(sourceId, cancellationToken);
}
