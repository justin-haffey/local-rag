namespace LocalRag.Domain;

public enum SourceStatus
{
    Pending,
    Indexing,
    Ready,
    Degraded,
    Paused,
    Failed
}

public enum ChunkProfileStatus
{
    Ready,
    Reindexing,
    Failed
}

/// <summary>Durable source-level chunk profile cutover state.</summary>
public sealed record ChunkProfileState(
    string SourceId,
    string ActiveFingerprint,
    string? PendingFingerprint,
    ChunkProfileStatus Status,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? CompletedUtc,
    string? LastError);

/// <summary>Registered filesystem root and its current indexing lifecycle state.</summary>
public sealed record SourceRecord(
    string SourceId,
    string CanonicalRootPath,
    string DisplayName,
    SourceStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastScanUtc,
    DateTimeOffset? LastSuccessfulIndexUtc,
    string EmbeddingProfileId,
    string? LastError = null,
    SourceLifecycleState LifecycleState = SourceLifecycleState.Active,
    long LifecycleEpoch = 0);

/// <summary>File identity and metadata used to detect changes within a registered source.</summary>
public sealed record IndexedFile(
    string FileId,
    string SourceId,
    string RelativePath,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

/// <summary>Content chunk plus source locations and embedding-profile provenance.</summary>
public sealed record ChunkRecord(
    string ChunkId,
    string SourceId,
    string FileId,
    string RelativePath,
    string Language,
    string? SymbolName,
    int StartLine,
    int EndLine,
    int Ordinal,
    string Content,
    string ContentHash,
    int TokenCount,
    string EmbeddingProfileId,
    DateTimeOffset LastIndexedUtc,
    string ChunkKind = "text",
    string? QualifiedSymbolName = null,
    string StructuralLocator = "",
    string ChunkerId = "generic",
    string ChunkerVersion = "1",
    string ChunkProfileFingerprint = "legacy-generic-1");

public sealed record VectorDocument(ChunkRecord Chunk, IReadOnlyList<float> Vector);

/// <summary>Hybrid search input; <paramref name="Alpha"/> weights vector similarity versus lexical matching.</summary>
public sealed record SearchRequest(
    string Query,
    IReadOnlyList<string>? SourceIds = null,
    int Limit = 12,
    double Alpha = 0.65);

public sealed record SearchResult(
    string ChunkId,
    string SourceId,
    string RelativePath,
    string Language,
    string? SymbolName,
    int StartLine,
    int EndLine,
    double Score,
    string Content,
    string ContentHash,
    DateTimeOffset LastIndexedUtc,
    string ChunkKind = "text",
    string? QualifiedSymbolName = null,
    string StructuralLocator = "",
    string ChunkerId = "generic",
    string ChunkerVersion = "1",
    string ChunkProfileFingerprint = "legacy-generic-1");

public sealed record SearchResponse(
    string Query,
    IReadOnlyList<SearchResult> Results,
    int CandidateCount,
    long ElapsedMilliseconds,
    bool Truncated);
