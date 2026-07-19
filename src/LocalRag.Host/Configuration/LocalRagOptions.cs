namespace LocalRag.Configuration;

public sealed class LocalRagOptions
{
    /// <summary>Configuration section and defaults shared by the host's local indexing and search services.</summary>
    public const string SectionName = "LocalRag";

    /// <summary>Root for application data; the default uses the <c>LOCALAPPDATA</c> placeholder.</summary>
    public string DataDirectory { get; init; } = "%LOCALAPPDATA%\\LocalRag";
    public AuthenticationOptions Authentication { get; init; } = new();
    public WeaviateOptions Weaviate { get; init; } = new();
    public IndexingOptions Indexing { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();
    public EmbeddingOptions Embedding { get; init; } = new();
}

public sealed class AuthenticationOptions
{
    public string Token { get; init; } = string.Empty;
}

public sealed class WeaviateOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:8080";
    public string Collection { get; init; } = "RagChunk_v1";
    public string? ApiKey { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 30;
    /// <summary>Maximum number of documents sent in one vector-store batch request.</summary>
    public int BatchSize { get; init; } = 100;
    public string Vectorizer { get; init; } = "none";
}

public sealed class IndexingOptions
{
    /// <summary>Files larger than this byte count are skipped by the indexer.</summary>
    public long MaxFileBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>Quiet period after a file event before indexing is queued.</summary>
    public int DebounceMilliseconds { get; init; } = 5000;
    /// <summary>Interval used to confirm a file has stopped changing before it is read.</summary>
    public int StabilityIntervalMilliseconds { get; init; } = 1500;
    public int MaxConcurrentFiles { get; init; } = 4;
    public int ReconciliationIntervalMinutes { get; init; } = 30;
    public int MaxRetryAttempts { get; init; } = 5;
    public int RetryBaseDelaySeconds { get; init; } = 2;
}

public sealed class ChunkingOptions
{
    /// <summary>Preferred chunk size; <see cref="MaximumTokens"/> remains the hard upper bound.</summary>
    public int TargetTokens { get; init; } = 384;
    public int MaximumTokens { get; init; } = 480;
    /// <summary>Number of trailing tokens repeated in the following chunk for context continuity.</summary>
    public int OverlapTokens { get; init; } = 64;
}

public sealed class EmbeddingOptions
{
    public string ProfileId { get; init; } = "bge-small-en-v1.5-384";
    public string ModelDirectory { get; init; } = "%LOCALAPPDATA%\\LocalRag\\models\\bge-small-en-v1.5";
    /// <summary>Expected vector length produced by the configured embedding profile.</summary>
    public int Dimensions { get; init; } = 384;
    public int MaximumTokens { get; init; } = 512;
    public string QueryPrefix { get; init; } = string.Empty;
    public string PassagePrefix { get; init; } = string.Empty;
}
