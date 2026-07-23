using LocalRag.Domain;

namespace LocalRag.Infrastructure.Diagnostics;

/// <summary>Dependency-free, bounded-cardinality operational counters exposed only by the authenticated loopback host.</summary>
public sealed class OperationalMetrics
{
    private static readonly string[] RequestCauses =
    [
        "Initial", "FileHint", "WatcherOverflow", "WatcherError", "Startup", "Periodic", "Manual", "Retry", "Other"
    ];

    private static readonly string[] RunOutcomes =
    [
        "Succeeded", "RetryScheduled", "Failed", "Cancelled", "LeaseLost", "Other"
    ];

    private readonly long[] _reconciliationRequestsByCause = new long[RequestCauses.Length];
    private readonly long[] _reconciliationOutcomes = new long[RunOutcomes.Length];
    private long _completedJobs;
    private long _failedJobs;
    private long _retriedJobs;
    private long _indexedFiles;
    private long _searches;
    private long _reconciliationRequests;
    private long _watcherOverflows;
    private long _reconciliationRuns;
    private long _reconciliationRetries;
    private long _expiredLeasesRecovered;
    private long _reconciliationDurationMilliseconds;
    private long _lastReconciliationDurationMilliseconds;
    private long _reconciliationChangedFiles;
    private long _reconciliationDeletedFiles;
    private long _reconciliationUnchangedFiles;
    private long _dirtySources;
    private long _degradedSources;

    public void JobCompleted() => Interlocked.Increment(ref _completedJobs);
    public void JobFailed() => Interlocked.Increment(ref _failedJobs);
    public void JobRetried() => Interlocked.Increment(ref _retriedJobs);
    public void FileIndexed() => Interlocked.Increment(ref _indexedFiles);
    public void SearchExecuted() => Interlocked.Increment(ref _searches);

    public void ReconciliationRequested(string cause)
    {
        Interlocked.Increment(ref _reconciliationRequests);
        IncrementRequestCause(cause);
    }

    public void ReconciliationRequested(ReconciliationCause causes)
    {
        Interlocked.Increment(ref _reconciliationRequests);
        var recorded = false;
        foreach (var cause in Enum.GetValues<ReconciliationCause>())
        {
            if (cause == ReconciliationCause.None || !causes.HasFlag(cause)) continue;
            IncrementRequestCause(cause.ToString());
            recorded = true;
        }

        if (!recorded || (causes & ~KnownCauses()) != ReconciliationCause.None) IncrementRequestCause("Other");
    }

    public void WatcherOverflowed() => Interlocked.Increment(ref _watcherOverflows);
    public void ReconciliationStarted() => Interlocked.Increment(ref _reconciliationRuns);
    public void ReconciliationRetried() => Interlocked.Increment(ref _reconciliationRetries);
    public void ReconciliationLeaseRecovered(int count = 1) =>
        Interlocked.Add(ref _expiredLeasesRecovered, Math.Max(0, count));

    public void ReconciliationFinished(
        string outcome,
        TimeSpan duration,
        int changedFiles,
        int deletedFiles,
        int unchangedFiles)
    {
        Interlocked.Increment(ref _reconciliationOutcomes[BoundedIndex(outcome, RunOutcomes)]);
        var durationMilliseconds = Math.Max(0L, (long)duration.TotalMilliseconds);
        Interlocked.Add(ref _reconciliationDurationMilliseconds, durationMilliseconds);
        Interlocked.Exchange(ref _lastReconciliationDurationMilliseconds, durationMilliseconds);
        Interlocked.Add(ref _reconciliationChangedFiles, Math.Max(0, changedFiles));
        Interlocked.Add(ref _reconciliationDeletedFiles, Math.Max(0, deletedFiles));
        Interlocked.Add(ref _reconciliationUnchangedFiles, Math.Max(0, unchangedFiles));
    }

    public void SetRecoveryGauges(int dirtySources, int degradedSources)
    {
        Interlocked.Exchange(ref _dirtySources, Math.Max(0, dirtySources));
        Interlocked.Exchange(ref _degradedSources, Math.Max(0, degradedSources));
    }

    public object Snapshot() => new
    {
        completedJobs = Interlocked.Read(ref _completedJobs),
        failedJobs = Interlocked.Read(ref _failedJobs),
        retriedJobs = Interlocked.Read(ref _retriedJobs),
        indexedFiles = Interlocked.Read(ref _indexedFiles),
        searches = Interlocked.Read(ref _searches),
        reconciliation = new
        {
            requests = Interlocked.Read(ref _reconciliationRequests),
            requestsByCause = SnapshotLabels(RequestCauses, _reconciliationRequestsByCause),
            watcherOverflows = Interlocked.Read(ref _watcherOverflows),
            runs = Interlocked.Read(ref _reconciliationRuns),
            outcomes = SnapshotLabels(RunOutcomes, _reconciliationOutcomes),
            retries = Interlocked.Read(ref _reconciliationRetries),
            expiredLeasesRecovered = Interlocked.Read(ref _expiredLeasesRecovered),
            durationMilliseconds = Interlocked.Read(ref _reconciliationDurationMilliseconds),
            lastDurationMilliseconds = Interlocked.Read(ref _lastReconciliationDurationMilliseconds),
            changedFiles = Interlocked.Read(ref _reconciliationChangedFiles),
            deletedFiles = Interlocked.Read(ref _reconciliationDeletedFiles),
            unchangedFiles = Interlocked.Read(ref _reconciliationUnchangedFiles),
            dirtySources = Interlocked.Read(ref _dirtySources),
            degradedSources = Interlocked.Read(ref _degradedSources)
        }
    };

    private static int BoundedIndex(string value, string[] allowed)
    {
        for (var index = 0; index < allowed.Length - 1; index++)
        {
            if (string.Equals(value, allowed[index], StringComparison.OrdinalIgnoreCase)) return index;
        }

        return allowed.Length - 1;
    }

    private void IncrementRequestCause(string cause) =>
        Interlocked.Increment(ref _reconciliationRequestsByCause[BoundedIndex(cause, RequestCauses)]);

    private static ReconciliationCause KnownCauses() =>
        ReconciliationCause.Initial |
        ReconciliationCause.FileHint |
        ReconciliationCause.WatcherOverflow |
        ReconciliationCause.WatcherError |
        ReconciliationCause.Startup |
        ReconciliationCause.Periodic |
        ReconciliationCause.Manual |
        ReconciliationCause.Retry;

    private static Dictionary<string, long> SnapshotLabels(string[] labels, long[] values)
    {
        var snapshot = new Dictionary<string, long>(labels.Length, StringComparer.Ordinal);
        for (var index = 0; index < labels.Length; index++) snapshot[labels[index]] = Interlocked.Read(ref values[index]);
        return snapshot;
    }
}
