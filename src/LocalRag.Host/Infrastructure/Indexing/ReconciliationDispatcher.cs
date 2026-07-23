using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Dispatches due SQLite work; the channel is only a bounded wakeup optimization.</summary>
public sealed partial class ReconciliationDispatcher(
    IReconciliationStore store,
    ReconciliationScheduler scheduler,
    ReconciliationDispatchSignal signal,
    IOptions<LocalRagOptions> options,
    OperationalMetrics metrics,
    HostMaintenanceCoordinator maintenance,
    ILogger<ReconciliationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.Indexing.ReconciliationDispatchPollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = poll;
            try
            {
                var operational = maintenance.TryAcquireOperational(stoppingToken);
                if (operational is null)
                {
                    await signal.WaitAsync(delay, stoppingToken);
                    continue;
                }
                await using var operationalLease = operational;
                var operationToken = operational.CancellationToken;
                var now = DateTimeOffset.UtcNow;
                var recovered = await store.RecoverExpiredLeasesAsync(now, operationToken);
                foreach (var _ in recovered) metrics.ReconciliationLeaseRecovered();
                var due = await store.GetDueSourceIdsAsync(now, operationToken);
                foreach (var sourceId in recovered.Concat(due).Distinct(StringComparer.Ordinal))
                {
                    await scheduler.WakeAsync(sourceId, operationToken);
                }
                var states = await store.ListAsync(operationToken);
                metrics.SetRecoveryGauges(
                    states.Count(state => state.DesiredGeneration > state.CompletedGeneration),
                    states.Count(state => state.State == ReconciliationState.Degraded));
                var delayStarted = DateTimeOffset.UtcNow;
                var nextDue = await store.GetNextDueUtcAsync(delayStarted, operationToken);
                if (nextDue is not null)
                {
                    var untilDue = nextDue.Value - delayStarted;
                    delay = untilDue <= TimeSpan.Zero
                        ? TimeSpan.Zero
                        : untilDue < poll ? untilDue : poll;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch
            {
                LogDispatchFailed(logger);
            }

            try
            {
                await signal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Durable reconciliation dispatch failed; it will be retried.")]
    private static partial void LogDispatchFailed(ILogger logger);
}

/// <summary>Coalesces durable schedule changes so the dispatcher can recompute its next due time immediately.</summary>
public sealed class ReconciliationDispatchSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Notify() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _)) return;
        if (maximumDelay <= TimeSpan.Zero) return;

        using var signalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _channel.Reader.ReadAsync(signalCancellation.Token).AsTask();
        var delay = Task.Delay(maximumDelay, cancellationToken);
        try
        {
            await await Task.WhenAny(signal, delay);
        }
        finally
        {
            signalCancellation.Cancel();
            try
            {
                await signal;
            }
            catch (OperationCanceledException) when (signalCancellation.IsCancellationRequested)
            {
            }
        }
    }
}
