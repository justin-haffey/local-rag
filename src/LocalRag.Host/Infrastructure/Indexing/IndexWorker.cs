using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class IndexWorker(IndexWorkChannel queue, IServiceProvider services, IndexJobStore jobs, IOptions<LocalRagOptions> options, OperationalMetrics metrics, ILogger<IndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Max(1, options.Value.Indexing.MaxConcurrentFiles);
        var workers = Enumerable.Range(0, workerCount).Select(_ => RunAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var sourceId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var job = await jobs.LeaseAsync(sourceId, stoppingToken);
                if (job is null) continue;
                using var scope = services.CreateScope();
                var succeeded = await scope.ServiceProvider.GetRequiredService<IndexCoordinator>().ProcessAsync(job, stoppingToken);
                if (succeeded) await CompleteAndWakeSuccessorAsync(job, stoppingToken);
                else await ScheduleRetryAsync(job, new InvalidOperationException("Indexing failed; see source status for details."), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogWorkerFailed(logger, exception, sourceId);
            }
        }
    }

    internal async Task CompleteAndWakeSuccessorAsync(IndexJob job, CancellationToken cancellationToken)
    {
        var hasPendingSuccessor = await jobs.CompleteAsync(job, cancellationToken);
        metrics.JobCompleted();
        if (hasPendingSuccessor) await queue.EnqueueAsync(job.SourceId, cancellationToken);
    }

    internal async Task ScheduleRetryAsync(IndexJob job, Exception exception, CancellationToken cancellationToken)
    {
        var indexing = options.Value.Indexing;
        var delay = TimeSpan.FromSeconds(indexing.RetryBaseDelaySeconds * Math.Pow(2, job.Attempt));
        var hasPendingSuccessor = await jobs.RetryOrFailAsync(
            job, exception, indexing.MaxRetryAttempts, delay, cancellationToken);
        if (job.Attempt + 1 < indexing.MaxRetryAttempts)
        {
            metrics.JobRetried();
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delay, cancellationToken); await queue.EnqueueAsync(job.SourceId, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }, CancellationToken.None);
        }
        else
        {
            metrics.JobFailed();
            if (hasPendingSuccessor) await queue.EnqueueAsync(job.SourceId, cancellationToken);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Index worker failed for source {SourceId}")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception, string sourceId);
}
