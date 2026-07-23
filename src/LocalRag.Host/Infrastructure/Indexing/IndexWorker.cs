using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class IndexWorker(
    IndexWorkChannel queue,
    IServiceProvider services,
    IReconciliationStore reconciliations,
    SourceOperationGate operationGate,
    ReconciliationDispatchSignal dispatchSignal,
    IOptions<LocalRagOptions> options,
    OperationalMetrics metrics,
    ILogger<IndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = options.Value.Indexing.MaxConcurrentReconciliations;
        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(_ => RunAsync(stoppingToken)));
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var sourceId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessSourceAsync(sourceId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                LogWorkerFailed(logger, sourceId);
            }
        }
    }

    private async Task ProcessSourceAsync(string sourceId, CancellationToken stoppingToken)
    {
        await using var sourceOperation = await operationGate.AcquireAsync(sourceId, stoppingToken);
        var leaseDuration = TimeSpan.FromSeconds(options.Value.Indexing.ReconciliationLeaseDurationSeconds);
        var lease = await reconciliations.TryLeaseAsync(sourceId, leaseDuration, sourceOperation.CancellationToken);
        if (lease is null) return;
        metrics.ReconciliationStarted();
        var started = DateTimeOffset.UtcNow;

        using var renewalStop = new CancellationTokenSource();
        using var leaseLost = new CancellationTokenSource();
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(
            sourceOperation.CancellationToken,
            stoppingToken,
            leaseLost.Token);
        var renewal = RenewLeaseAsync(lease, leaseDuration, leaseLost, renewalStop.Token);

        try
        {
            using var scope = services.CreateScope();
            var outcome = await scope.ServiceProvider
                .GetRequiredService<IndexCoordinator>()
                .ProcessAsync(lease, processing.Token);
            var completion = await reconciliations.CompleteAsync(lease, outcome.Result, processing.Token);
            if (!completion.Applied)
            {
                LogStaleCompletion(logger, sourceId, lease.Generation);
                return;
            }

            metrics.JobCompleted();
            metrics.ReconciliationFinished(
                "Succeeded",
                DateTimeOffset.UtcNow - started,
                outcome.Result.ChangedFiles,
                outcome.Result.DeletedFiles,
                outcome.Result.UnchangedFiles);
            if (completion.HasSuccessor)
            {
                try
                {
                    await queue.EnqueueAsync(sourceId, processing.Token);
                }
                catch (OperationCanceledException) when (processing.IsCancellationRequested)
                {
                    // Durable due-work dispatch will publish the successor after shutdown or lease cancellation.
                }
            }
            try
            {
                await reconciliations.PruneCompletedAsync(
                    options.Value.Indexing.ReconciliationHistoryLimit,
                    CancellationToken.None);
            }
            catch
            {
                LogHistoryPruneFailed(logger, sourceId);
            }
        }
        catch (OperationCanceledException)
        {
            if (await reconciliations.ReleaseAsync(lease, CancellationToken.None)) dispatchSignal.Notify();
            metrics.ReconciliationFinished(
                leaseLost.IsCancellationRequested ? "LeaseLost" : "Cancelled",
                DateTimeOffset.UtcNow - started,
                0,
                0,
                0);
        }
        catch (ReconciliationProcessingException exception)
        {
            var failure = await FailAsync(lease, exception.Code, CancellationToken.None);
            RecordFailureOutcome(failure, started);
        }
        catch
        {
            var failure = await FailAsync(lease, ReconciliationFailureCode.Unexpected, CancellationToken.None);
            RecordFailureOutcome(failure, started);
        }
        finally
        {
            renewalStop.Cancel();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<ReconciliationFailureResult> FailAsync(
        ReconciliationLease lease,
        ReconciliationFailureCode code,
        CancellationToken cancellationToken)
    {
        var indexing = options.Value.Indexing;
        var delay = TimeSpan.FromSeconds(indexing.RetryBaseDelaySeconds * Math.Pow(2, lease.Attempt));
        var result = await reconciliations.FailAsync(
            lease,
            new ReconciliationFailure(code),
            indexing.MaxRetryAttempts,
            delay,
            cancellationToken);
        if (!result.Applied) return result;

        if (result.IsTerminal) metrics.JobFailed();
        else
        {
            metrics.JobRetried();
            metrics.ReconciliationRetried();
            dispatchSignal.Notify();
        }
        if (result.HasSuccessor && result.IsTerminal)
        {
            await queue.EnqueueAsync(lease.SourceId, cancellationToken);
        }
        return result;
    }

    private void RecordFailureOutcome(ReconciliationFailureResult result, DateTimeOffset started)
    {
        var outcome = !result.Applied ? "LeaseLost" : result.IsTerminal ? "Failed" : "RetryScheduled";
        metrics.ReconciliationFinished(outcome, DateTimeOffset.UtcNow - started, 0, 0, 0);
    }

    private async Task RenewLeaseAsync(
        ReconciliationLease lease,
        TimeSpan leaseDuration,
        CancellationTokenSource leaseLost,
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.Indexing.ReconciliationLeaseRenewalSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (await reconciliations.RenewLeaseAsync(lease, leaseDuration, stoppingToken)) continue;
                leaseLost.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            leaseLost.Cancel();
            LogLeaseRenewalFailed(logger, lease.SourceId, lease.Generation);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Index worker failed for source {SourceId}")]
    private static partial void LogWorkerFailed(ILogger logger, string sourceId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Ignored stale reconciliation completion for source {SourceId} generation {Generation}")]
    private static partial void LogStaleCompletion(ILogger logger, string sourceId, long generation);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Lease renewal failed for source {SourceId} generation {Generation}")]
    private static partial void LogLeaseRenewalFailed(ILogger logger, string sourceId, long generation);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Completed reconciliation history pruning failed for source {SourceId}")]
    private static partial void LogHistoryPruneFailed(ILogger logger, string sourceId);
}
