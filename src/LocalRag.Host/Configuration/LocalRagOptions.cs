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
    /// <summary>Maximum combined uncompressed size of searchable XML parts read from a packaged document.</summary>
    public long MaxExpandedDocumentBytes { get; init; } = 50 * 1024 * 1024;
    /// <summary>Maximum number of pages accepted from one PDF document, including pages with no extractable text.</summary>
    public int MaxPdfPages { get; init; } = 1_000;
    /// <summary>Maximum number of extracted characters accepted from one PDF, including page separators.</summary>
    public int MaxPdfTextCharacters { get; init; } = 10_000_000;
    /// <summary>Whether pages without embedded text are rendered and passed through local OCR; disabling this leaves such pages empty.</summary>
    public bool EnablePdfOcr { get; init; } = true;
    /// <summary>Rasterization resolution used for OCR of scanned PDF pages, in dots per inch (72-600).</summary>
    public int PdfOcrDpi { get; init; } = 300;
    /// <summary>Maximum number of image-only pages OCR may process from one PDF.</summary>
    public int MaxPdfOcrPages { get; init; } = 100;
    /// <summary>Maximum rendered pixel count accepted for one OCR page at the configured DPI.</summary>
    public long MaxPdfOcrPixelsPerPage { get; init; } = 20_000_000;
    /// <summary>Quiet period after a file event before indexing is queued.</summary>
    public int DebounceMilliseconds { get; init; } = 5000;
    /// <summary>Interval used to confirm a file has stopped changing before it is read.</summary>
    public int StabilityIntervalMilliseconds { get; init; } = 1500;
    public int MaxConcurrentFiles { get; init; } = 4;
    public int ReconciliationIntervalMinutes { get; init; } = 30;
    /// <summary>Duration of a durable reconciliation lease before it is eligible for recovery.</summary>
    public int ReconciliationLeaseDurationSeconds { get; init; } = 120;
    /// <summary>Interval used by a running reconciliation to renew its durable lease.</summary>
    public int ReconciliationLeaseRenewalSeconds { get; init; } = 30;
    /// <summary>Maximum number of sources reconciled concurrently; each source remains single-flight.</summary>
    public int MaxConcurrentReconciliations { get; init; } = 2;
    /// <summary>Maximum delay before the dispatcher checks durable due work when no earlier due time is known.</summary>
    public int ReconciliationDispatchPollSeconds { get; init; } = 5;
    /// <summary>Terminal reconciliation generations retained per source after a later successful checkpoint.</summary>
    public int ReconciliationHistoryLimit { get; init; } = 20;
    /// <summary>How long an inaccessible source may remain degraded before its index and registry record are removed.</summary>
    public int MissingSourceCleanupGraceMinutes { get; init; } = 60;
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
    /// <summary>Structural adapters enabled for the active chunk profile.</summary>
    public string[] EnabledAdapters { get; init; } =
    [
        "csharp", "json", "markdown", "python", "toml", "typescript-javascript", "xml", "yaml"
    ];
}

public sealed class EmbeddingOptions
{
    public string ProfileId { get; init; } = "bge-small-en-v1.5-384";
    public string ModelDirectory { get; init; } = "%LOCALAPPDATA%\\LocalRag\\models\\bge-small-en-v1.5";
    /// <summary>Expected vector length produced by the configured embedding profile.</summary>
    public int Dimensions { get; init; } = 384;
    public int MaximumTokens { get; init; } = 512;
    /// <summary>Stable identity of the tokenizer rules used to enforce model limits.</summary>
    public string TokenizerId { get; init; } = "bert-wordpiece-lowercase-v1";
    public string QueryPrefix { get; init; } = string.Empty;
    public string PassagePrefix { get; init; } = string.Empty;
}
