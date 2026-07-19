using System.Collections.Concurrent;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class SourceWatcherRegistry(IndexWorkChannel queue, ISourceRegistry sources, IOptions<LocalRagOptions> options, ILogger<SourceWatcherRegistry> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces = new(StringComparer.Ordinal);
    private readonly IndexingOptions _options = options.Value.Indexing;

    public void Track(SourceRecord source)
    {
        Untrack(source.SourceId);
        var watcher = new FileSystemWatcher(source.CanonicalRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        FileSystemEventHandler changed = (_, _) => Debounce(source.SourceId);
        RenamedEventHandler renamed = (_, _) => Debounce(source.SourceId);
        ErrorEventHandler error = (_, eventArgs) =>
        {
            LogWatcherFailed(logger, eventArgs.GetException(), source.SourceId);
            _ = sources.SetStatusAsync(source.SourceId, SourceStatus.Degraded, "File watcher overflowed or failed; reindex has been queued.", CancellationToken.None);
            Debounce(source.SourceId);
        };
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        _watchers[source.SourceId] = watcher;
    }

    public void Untrack(string sourceId)
    {
        if (_watchers.TryRemove(sourceId, out var watcher)) watcher.Dispose();
        if (_debounces.TryRemove(sourceId, out var debounce)) debounce.Cancel();
    }

    private void Debounce(string sourceId)
    {
        var next = new CancellationTokenSource();
        var previous = _debounces.AddOrUpdate(sourceId, next, (_, current) =>
        {
            current.Cancel();
            return next;
        });
        _ = QueueAfterQuietPeriodAsync(sourceId, previous.Token);
    }

    private async Task QueueAfterQuietPeriodAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.DebounceMilliseconds + _options.StabilityIntervalMilliseconds, cancellationToken);
            await queue.EnqueueAsync(sourceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        foreach (var sourceId in _watchers.Keys) Untrack(sourceId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Watcher failed for source {SourceId}; source requires reindex.")]
    private static partial void LogWatcherFailed(ILogger logger, Exception exception, string sourceId);
}
