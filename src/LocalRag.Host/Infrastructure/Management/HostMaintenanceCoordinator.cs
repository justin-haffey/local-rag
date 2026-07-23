namespace LocalRag.Infrastructure.Management;

public sealed class HostMaintenanceCoordinator
{
    private readonly object _sync = new();
    private readonly HashSet<CancellationTokenSource> _active = [];
    private TaskCompletionSource _drained = CompletedSignal();
    private bool _maintenance;
    private bool _failed;
    private long _generation;

    public bool IsReady
    {
        get
        {
            lock (_sync) return !_maintenance && !_failed;
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync) return _generation;
        }
    }

    public OperationalLease? TryAcquireOperational(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_maintenance || _failed) return null;
            if (_active.Count == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _active.Add(source);
            return new OperationalLease(this, source, _generation);
        }
    }

    public async Task<MaintenanceLease?> TryAcquireMaintenanceAsync(
        TimeSpan drainTimeout,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource[] active;
        Task drained;
        long generation;
        lock (_sync)
        {
            if (_maintenance) return null;
            _maintenance = true;
            _generation++;
            generation = _generation;
            active = [.. _active];
            drained = _active.Count == 0 ? Task.CompletedTask : _drained.Task;
        }

        foreach (var source in active) source.Cancel();
        try
        {
            await drained.WaitAsync(drainTimeout, cancellationToken);
            return new MaintenanceLease(this, generation);
        }
        catch
        {
            lock (_sync)
            {
                _failed = true;
                _maintenance = false;
            }
            throw;
        }
    }

    public void MarkFailed()
    {
        lock (_sync) _failed = true;
    }

    public void ClearFailure()
    {
        lock (_sync) _failed = false;
    }

    private void ReleaseOperational(CancellationTokenSource source)
    {
        lock (_sync)
        {
            _active.Remove(source);
            if (_active.Count == 0) _drained.TrySetResult();
        }
        source.Dispose();
    }

    private void ReleaseMaintenance(bool succeeded)
    {
        lock (_sync)
        {
            _failed = !succeeded;
            _maintenance = false;
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    public sealed class OperationalLease : IAsyncDisposable
    {
        private HostMaintenanceCoordinator? _owner;
        private CancellationTokenSource? _source;

        internal OperationalLease(HostMaintenanceCoordinator owner, CancellationTokenSource source, long generation)
        {
            _owner = owner;
            _source = source;
            Generation = generation;
        }

        public long Generation { get; }
        public CancellationToken CancellationToken => _source?.Token ?? throw new ObjectDisposedException(nameof(OperationalLease));

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var source = Interlocked.Exchange(ref _source, null);
            if (owner is not null && source is not null) owner.ReleaseOperational(source);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class MaintenanceLease : IAsyncDisposable
    {
        private HostMaintenanceCoordinator? _owner;
        private bool _succeeded;

        internal MaintenanceLease(HostMaintenanceCoordinator owner, long generation)
        {
            _owner = owner;
            Generation = generation;
        }

        public long Generation { get; }
        public void Complete() => _succeeded = true;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseMaintenance(_succeeded);
            return ValueTask.CompletedTask;
        }
    }
}
