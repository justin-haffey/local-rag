using LocalRag.Domain;

namespace LocalRag.Application;

/// <summary>Persists registered source roots and their indexing status.</summary>
public interface ISourceRegistry
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<SourceRecord> RegisterAsync(string rootPath, string? displayName, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceRecord>> ListAsync(CancellationToken cancellationToken);
    Task<SourceRecord?> GetAsync(string sourceId, CancellationToken cancellationToken);
    Task RemoveAsync(string sourceId, CancellationToken cancellationToken);
    Task SetStatusAsync(string sourceId, SourceStatus status, string? failureMessage, CancellationToken cancellationToken);
}

/// <summary>Stores file and chunk metadata independently of the vector backend.</summary>
public interface IIndexStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken);
    Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken);
    Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken);
    Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken);
}

/// <summary>Produces vectors using one stable, named embedding profile.</summary>
public interface IEmbeddingService
{
    /// <summary>Identifier persisted with chunks so vectors are never mixed across profiles.</summary>
    string ProfileId { get; }
    Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken);
    Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken);
    Task ValidateAsync(CancellationToken cancellationToken);
}

/// <summary>Splits normalized file content into location-preserving chunks.</summary>
public interface IChunker
{
    IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent);
}

/// <summary>Exposes the canonical identity of the active chunking configuration.</summary>
public interface IChunkProfileProvider
{
    string ChunkerIdentity { get; }
    string Fingerprint { get; }
}

/// <summary>Persists source-level chunk-profile transitions and query cutover state.</summary>
public interface IChunkProfileStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ChunkProfileState> GetOrCreateAsync(
        string sourceId,
        string configuredFingerprint,
        bool hasIndexedChunks,
        CancellationToken cancellationToken);
    Task<ChunkProfileState?> GetAsync(string sourceId, CancellationToken cancellationToken);
    Task BeginTransitionAsync(string sourceId, string targetFingerprint, CancellationToken cancellationToken);
    Task CompleteTransitionAsync(string sourceId, string targetFingerprint, CancellationToken cancellationToken);
    Task FailTransitionAsync(string sourceId, string targetFingerprint, string failureMessage, CancellationToken cancellationToken);
    Task<bool> IsQueryVisibleAsync(string sourceId, CancellationToken cancellationToken);
}

/// <summary>Linearizes source-profile transitions with source-derived query operations.</summary>
public interface IChunkProfileOperationGate
{
    Task<IAsyncDisposable> AcquireAsync(IEnumerable<string> sourceIds, CancellationToken cancellationToken);
}

/// <summary>Extracts searchable text from one supported file format.</summary>
public interface IContentExtractor
{
    bool Supports(string path);
    Task<string> ExtractAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Maintains chunk vectors and executes similarity searches.</summary>
public interface IVectorStore
{
    Task EnsureReadyAsync(CancellationToken cancellationToken);
    Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken);
    Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken);
    Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken);
}

/// <summary>Coordinates source scans, persistence, embedding, and vector-store updates.</summary>
public interface IIndexCoordinator
{
    Task QueueInitialIndexAsync(string sourceId, CancellationToken cancellationToken);
    Task ReindexAsync(string sourceId, CancellationToken cancellationToken);
    Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken);
}

/// <summary>
/// Persists source-scoped reconciliation generations. SQLite is the authoritative queue; an in-memory
/// channel may only be used to wake due source IDs after these methods commit.
/// </summary>
public interface IReconciliationStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ReconciliationRequestResult> RequestAsync(ReconciliationRequest request, CancellationToken cancellationToken);
    Task<ReconciliationLease?> TryLeaseAsync(string sourceId, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(ReconciliationLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<ReconciliationCompletionResult> CompleteAsync(
        ReconciliationLease lease,
        ReconciliationResult result,
        CancellationToken cancellationToken);
    Task<ReconciliationFailureResult> FailAsync(
        ReconciliationLease lease,
        ReconciliationFailure failure,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);
    Task<bool> ReleaseAsync(ReconciliationLease lease, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetDueSourceIdsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetNextDueUtcAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<SourceReconciliation?> GetAsync(string sourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceReconciliation>> ListAsync(CancellationToken cancellationToken);
    Task<SourceLifecycle?> TombstoneAsync(string sourceId, CancellationToken cancellationToken);
    Task<bool> IsLifecycleCurrentAsync(string sourceId, long expectedEpoch, CancellationToken cancellationToken);
    Task PruneCompletedAsync(int historyLimit, CancellationToken cancellationToken);
}

/// <summary>Application-facing search facade combining metadata and vector retrieval.</summary>
public interface IRagSearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken);
}
