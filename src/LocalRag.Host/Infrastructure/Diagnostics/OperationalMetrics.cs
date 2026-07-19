namespace LocalRag.Infrastructure.Diagnostics;

/// <summary>Small dependency-free operational counters exposed only through the authenticated loopback host.</summary>
public sealed class OperationalMetrics
{
    private long _completedJobs;
    private long _failedJobs;
    private long _retriedJobs;
    private long _indexedFiles;
    private long _searches;

    public void JobCompleted() => Interlocked.Increment(ref _completedJobs);
    public void JobFailed() => Interlocked.Increment(ref _failedJobs);
    public void JobRetried() => Interlocked.Increment(ref _retriedJobs);
    public void FileIndexed() => Interlocked.Increment(ref _indexedFiles);
    public void SearchExecuted() => Interlocked.Increment(ref _searches);
    public object Snapshot() => new { completedJobs = Interlocked.Read(ref _completedJobs), failedJobs = Interlocked.Read(ref _failedJobs), retriedJobs = Interlocked.Read(ref _retriedJobs), indexedFiles = Interlocked.Read(ref _indexedFiles), searches = Interlocked.Read(ref _searches) };
}
