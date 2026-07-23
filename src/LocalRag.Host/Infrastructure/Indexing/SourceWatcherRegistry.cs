using System.Collections.Concurrent;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

public sealed partial class SourceWatcherRegistry(
    ReconciliationScheduler scheduler,
    IOptions<LocalRagOptions> options,
    ILogger<SourceWatcherRegistry> logger,
    HostMaintenanceCoordinator? maintenance = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces = new(StringComparer.Ordinal);
    private readonly IndexingOptions _options = options.Value.Indexing;

    public void Track(SourceRecord source)
    {
        if (maintenance is not null && !maintenance.IsReady) return;
        if (source.LifecycleState != SourceLifecycleState.Active) return;
        if (_watchers.ContainsKey(source.SourceId)) return;
        var watcher = new FileSystemWatcher(source.CanonicalRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false
        };
        FileSystemEventHandler changed = (_, _) => Debounce(source.SourceId);
        RenamedEventHandler renamed = (_, _) => Debounce(source.SourceId);
        ErrorEventHandler error = (_, eventArgs) =>
            _ = NotifyErrorAsync(source.SourceId, eventArgs.GetException(), CancellationToken.None);
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        if (!_watchers.TryAdd(source.SourceId, watcher))
        {
            watcher.Dispose();
            return;
        }
        watcher.EnableRaisingEvents = true;
    }

    public void Untrack(string sourceId)
    {
        if (_watchers.TryRemove(sourceId, out var watcher)) watcher.Dispose();
        if (_debounces.TryRemove(sourceId, out var debounce))
        {
            debounce.Cancel();
            debounce.Dispose();
        }
    }

    public void UntrackAll()
    {
        foreach (var sourceId in _watchers.Keys) Untrack(sourceId);
    }

    internal async Task NotifyErrorAsync(
        string sourceId,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (maintenance is not null && !maintenance.IsReady) return;
        var cause = ReconciliationCause.WatcherError;
        if (exception is InternalBufferOverflowException) cause |= ReconciliationCause.WatcherOverflow;
        LogWatcherFailed(logger, sourceId, cause);
        try
        {
            await scheduler.RequestAsync(sourceId, cause, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            LogWatcherRecoveryRequestFailed(logger, sourceId);
        }
    }

    private void Debounce(string sourceId)
    {
        if (maintenance is not null && !maintenance.IsReady) return;
        var next = new CancellationTokenSource();
        _debounces.AddOrUpdate(sourceId, next, (_, current) =>
        {
            current.Cancel();
            current.Dispose();
            return next;
        });
        _ = QueueAfterQuietPeriodAsync(sourceId, next.Token);
    }

    private async Task QueueAfterQuietPeriodAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.DebounceMilliseconds + _options.StabilityIntervalMilliseconds, cancellationToken);
            await scheduler.RequestAsync(sourceId, ReconciliationCause.FileHint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            LogWatcherRecoveryRequestFailed(logger, sourceId);
        }
    }

    public void Dispose()
    {
        UntrackAll();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Watcher failed for source {SourceId}; queued causes {Causes}.")]
    private static partial void LogWatcherFailed(
        ILogger logger,
        string sourceId,
        ReconciliationCause causes);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not persist watcher recovery for source {SourceId}; periodic dispatch will retry durable work.")]
    private static partial void LogWatcherRecoveryRequestFailed(ILogger logger, string sourceId);
}
