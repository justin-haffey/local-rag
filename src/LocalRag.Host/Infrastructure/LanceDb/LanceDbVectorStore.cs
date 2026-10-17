using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.LanceDb;

public sealed class LanceDbVectorStore : IVectorStore, IManagementVectorStore, IDisposable
{
    private const int MaintenanceDocumentThreshold = 1_000;
    private readonly LanceDbOptions _options;
    private readonly InstallationOwnershipStore _ownershipStore;
    private readonly ILanceDbIpcCodec _codec;
    private readonly Func<ILanceDbNativeApi> _nativeFactory;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private ILanceDbNativeApi? _native;
    private LanceDbSafeHandle? _database;
    private FileStream? _pathLock;
    private string? _ownershipId;
    private long _documentsSinceMaintenance;
    private bool _disposed;
    private readonly object _lifetime = new();
    private int _activeOperations;

    public string ResetTarget => $"lancedb:{_options.Table}";
    public string ResetDescription => "the owned Local RAG LanceDB table";

    public LanceDbVectorStore(IOptions<LocalRagOptions> options, InstallationOwnershipStore ownershipStore)
        : this(
            options.Value.VectorStore.LanceDb,
            ownershipStore,
            new LanceDbIpcCodec(),
            () => new LanceDbNativeApi(Path.Combine(
                AppContext.BaseDirectory,
                "lancedb-native.manifest.json")))
    {
    }

    internal LanceDbVectorStore(
        LanceDbOptions options,
        InstallationOwnershipStore ownershipStore,
        ILanceDbIpcCodec codec,
        Func<ILanceDbNativeApi> nativeFactory)
    {
        _options = options;
        _ownershipStore = ownershipStore;
        _codec = codec;
        _nativeFactory = nativeFactory;
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        if (_database is not null)
        {
            await Task.Run(() => _native!.EnsureReady(_database, cancellationToken), cancellationToken);
            return;
        }

        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_database is not null) return;
            var path = Environment.ExpandEnvironmentVariables(_options.Path);
            Directory.CreateDirectory(path);
            try
            {
                _pathLock = new FileStream(
                    Path.Combine(path, ".localrag-exclusive.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                throw new LanceDbConfigurationException(
                    "The embedded LanceDB path is already owned by another LocalRAG host.");
            }

            try
            {
                _ownershipId = await _ownershipStore.GetOrCreateAsync(cancellationToken);
                _native = _nativeFactory();
                _database = _native.Open(path, _options.Table, _ownershipId);
                await Task.Run(() => _native.EnsureReady(_database, cancellationToken), cancellationToken);
            }
            catch
            {
                _database?.Dispose();
                _database = null;
                _native?.Dispose();
                _native = null;
                _pathLock.Dispose();
                _pathLock = null;
                throw;
            }
        }
        finally
        {
            _initialization.Release();
        }
    }

    public async Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        if (documents.Count == 0) return;
        await EnsureReadyAsync(cancellationToken);
        foreach (var batch in documents.Chunk(Math.Clamp(_options.BatchSize, 1, LanceDbIpcCodec.MaxRows)))
        {
            var ipc = _codec.EncodeDocuments(batch);
            await Task.Run(() => _native!.Upsert(_database!, ipc, cancellationToken), cancellationToken);
        }
        var pending = Interlocked.Add(ref _documentsSinceMaintenance, documents.Count);
        if (pending >= MaintenanceDocumentThreshold &&
            Interlocked.CompareExchange(ref _documentsSinceMaintenance, 0, pending) == pending)
        {
            try
            {
                await Task.Run(() => _native!.Optimize(_database!, cancellationToken), cancellationToken);
            }
            catch
            {
                Interlocked.Add(ref _documentsSinceMaintenance, pending);
                throw;
            }
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        var distinct = chunkIds.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return;
        await EnsureReadyAsync(cancellationToken);
        foreach (var batch in distinct.Chunk(LanceDbIpcCodec.MaxRows))
        {
            var ipc = _codec.EncodeChunkIds(batch);
            await Task.Run(() => _native!.DeleteChunks(_database!, ipc, cancellationToken), cancellationToken);
        }
    }

    public async Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source ID is required.", nameof(sourceId));
        await EnsureReadyAsync(cancellationToken);
        await Task.Run(() => _native!.DeleteSource(_database!, sourceId, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchRequest request,
        IReadOnlyList<float> queryVector,
        CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        ValidateSearch(request, queryVector);
        await EnsureReadyAsync(cancellationToken);
        var alpha = request.Mode switch
        {
            SearchMode.Lexical => 0f,
            SearchMode.Vector => 1f,
            _ => (float)request.Alpha
        };
        var bytes = await Task.Run(
            () => _native!.Search(
                _database!,
                new LanceDbSearchWireRequest(
                    request.Query,
                    request.Language,
                    request.PathPrefix,
                    _codec.EncodeChunkIds(request.SourceIds ?? []),
                    queryVector,
                    request.Limit,
                    alpha,
                    (uint)request.Mode),
                cancellationToken),
            cancellationToken);
        return _codec.DecodeResults(bytes)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.ChunkId, StringComparer.Ordinal)
            .Take(request.Limit)
            .ToArray();
    }

    public async Task<bool> VerifyOwnershipAsync(string ownershipId, CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        await EnsureReadyAsync(cancellationToken);
        return await Task.Run(
            () => _native!.VerifyOwnership(_database!, ownershipId, cancellationToken),
            cancellationToken);
    }

    public async Task ResetOwnedStoreAsync(string ownershipId, CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        await EnsureReadyAsync(cancellationToken);
        if (!await VerifyOwnershipAsync(ownershipId, cancellationToken))
        {
            throw new InvalidOperationException("The configured LanceDB table is not owned by this LocalRAG installation.");
        }
        await Task.Run(
            () => _native!.ResetOwned(_database!, ownershipId, cancellationToken),
            cancellationToken);
    }

    public Task RecoverOwnedStoreAsync(string ownershipId, CancellationToken cancellationToken) =>
        ResetOwnedStoreAsync(ownershipId, cancellationToken);

    internal async Task OptimizeAsync(CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation();
        await EnsureReadyAsync(cancellationToken);
        await Task.Run(() => _native!.Optimize(_database!, cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            // Preserve exclusive writer ownership until every native operation
            // and initialization has drained, including Task.Run continuations.
            while (_activeOperations != 0) Monitor.Wait(_lifetime);
            _database?.Dispose();
            _native?.Dispose();
            _pathLock?.Dispose();
            _initialization.Dispose();
        }
    }

    internal static void ValidateSearch(SearchRequest request, IReadOnlyList<float> queryVector)
    {
        if (request.Limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(request), "Search limit must be between 1 and 50.");
        if (request.Alpha is < 0 or > 1 || double.IsNaN(request.Alpha)) throw new ArgumentOutOfRangeException(nameof(request), "Search alpha must be between 0 and 1.");
        if (queryVector.Count != LanceDbIpcCodec.VectorDimensions || queryVector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("The search vector must contain exactly 384 finite values.", nameof(queryVector));
        }
        if (request.SourceIds is { Count: > 100 }) throw new ArgumentOutOfRangeException(nameof(request), "At most 100 source filters are allowed.");
        if (request.SourceIds?.Any(string.IsNullOrWhiteSpace) == true) throw new ArgumentException("Source filters must be non-empty.", nameof(request));
        if (request.Query.Length > 32_768 || request.Language?.Length > 128 || request.PathPrefix?.Length > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A structured search field exceeded its bound.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private OperationLease AcquireOperation()
    {
        lock (_lifetime)
        {
            ThrowIfDisposed();
            _activeOperations++;
            return new OperationLease(this);
        }
    }

    private sealed class OperationLease(LanceDbVectorStore owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lifetime)
            {
                owner._activeOperations--;
                Monitor.PulseAll(owner._lifetime);
            }
        }
    }
}
