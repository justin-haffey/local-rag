namespace LocalRag.Domain;

/// <summary>Bounded reasons a source requires a full manifest reconciliation.</summary>
[Flags]
public enum ReconciliationCause
{
    None = 0,
    Initial = 1 << 0,
    FileHint = 1 << 1,
    WatcherOverflow = 1 << 2,
    WatcherError = 1 << 3,
    Startup = 1 << 4,
    Periodic = 1 << 5,
    Manual = 1 << 6,
    Retry = 1 << 7
}

/// <summary>The durable scheduling/execution state for one source's reconciliation obligation.</summary>
public enum ReconciliationState
{
    Clean,
    Queued,
    Running,
    Degraded
}

/// <summary>Lifecycle fence used to prevent a removed source from accepting or completing work.</summary>
public enum SourceLifecycleState
{
    Active,
    Removing
}

/// <summary>Safe, bounded failure categories persisted by reconciliation jobs.</summary>
public enum ReconciliationFailureCode
{
    DependencyUnavailable,
    FileUnstable,
    SourceMissing,
    SchemaMismatch,
    StateCorrupt,
    Cancelled,
    Unexpected
}

/// <summary>One durable reconciliation request. Causes are merged; profile work retains the strongest request.</summary>
public sealed record ReconciliationRequest(
    string SourceId,
    ReconciliationCause Causes,
    string? TargetChunkProfileFingerprint = null,
    bool ForceContentProcessing = false);

/// <summary>Result of coalescing a request into a current or follow-up generation.</summary>
public sealed record ReconciliationRequestResult(
    long Generation,
    bool CreatedGeneration,
    bool WakeRequired,
    ReconciliationState State);

/// <summary>Token-fenced lease a worker must present to mutate reconciliation state.</summary>
public sealed record ReconciliationLease(
    string SourceId,
    long Generation,
    string JobId,
    string LeaseId,
    DateTimeOffset LeaseExpiresUtc,
    int Attempt,
    ReconciliationCause Causes,
    string? TargetChunkProfileFingerprint,
    bool ForceContentProcessing,
    long LifecycleEpoch);

/// <summary>Counts produced only after a complete manifest/vector convergence pass.</summary>
public sealed record ReconciliationResult(
    int ChangedFiles,
    int DeletedFiles,
    int UnchangedFiles);

/// <summary>Bounded failure input; no raw exception text is persisted by the reconciliation store.</summary>
public sealed record ReconciliationFailure(ReconciliationFailureCode Code)
{
    public bool IsTransient => Code is ReconciliationFailureCode.DependencyUnavailable or ReconciliationFailureCode.FileUnstable;

    public string SafeSummary => Code switch
    {
        ReconciliationFailureCode.DependencyUnavailable => "A required indexing dependency is unavailable.",
        ReconciliationFailureCode.FileUnstable => "A source file did not remain stable long enough to index.",
        ReconciliationFailureCode.SourceMissing => "The registered source root is unavailable.",
        ReconciliationFailureCode.SchemaMismatch => "The local state schema is incompatible with this host.",
        ReconciliationFailureCode.StateCorrupt => "The local reconciliation state could not be validated.",
        ReconciliationFailureCode.Cancelled => "Reconciliation was cancelled before completion.",
        _ => "Reconciliation did not complete."
    };
}

/// <summary>Durable source reconciliation state exposed through authenticated diagnostics.</summary>
public sealed record SourceReconciliation(
    string SourceId,
    long DesiredGeneration,
    long CompletedGeneration,
    long? ActiveGeneration,
    ReconciliationState State,
    ReconciliationCause PendingCauses,
    ReconciliationCause ActiveCauses,
    DateTimeOffset? RequestedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc,
    string? LastOutcome,
    ReconciliationFailureCode? LastErrorCode,
    string? LastErrorSummary,
    int LastChangedFiles,
    int LastDeletedFiles,
    int LastUnchangedFiles);

/// <summary>Persistent source-removal fence and epoch.</summary>
public sealed record SourceLifecycle(string SourceId, SourceLifecycleState State, long Epoch);

/// <summary>Completion disposition after a token-fenced update.</summary>
public sealed record ReconciliationCompletionResult(bool Applied, bool HasSuccessor, bool IsClean);

/// <summary>Failure disposition after retry/backoff or terminal handling.</summary>
public sealed record ReconciliationFailureResult(bool Applied, bool IsTerminal, bool HasSuccessor, DateTimeOffset? NextAttemptUtc);
