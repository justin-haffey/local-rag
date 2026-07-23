using System.Security.Cryptography;
using System.Text;
using LocalRag.Domain;

namespace LocalRag.Api;

public sealed record RegisterSourceRequest(string RootPath, string? DisplayName);
public sealed record SearchApiRequest(string Query, IReadOnlyList<string>? SourceIds, int? Limit, double? Alpha);
public sealed record ManagementIndexRequest(string RootPath, string? DisplayName);
public sealed record ManagementRemoveRequest(string RootPath, string? ConfirmationToken);
public sealed record ManagementResetRequest(string? ConfirmationToken);
public sealed record RecoveryResponse(
    string State,
    long DesiredGeneration,
    long CompletedGeneration,
    long? ActiveGeneration,
    IReadOnlyList<string> Causes,
    DateTimeOffset? RequestedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc,
    string? LastOutcome,
    string? LastErrorCode,
    string? LastErrorSummary,
    int ChangedFiles,
    int DeletedFiles,
    int UnchangedFiles);

public sealed record SourceResponse(
    string SourceId,
    string RootPathHash,
    string DisplayName,
    SourceStatus Status,
    DateTimeOffset? LastScanUtc,
    DateTimeOffset? LastSuccessfulIndexUtc,
    string? LastError,
    RecoveryResponse? Recovery = null);

public static class ApiContractMapping
{
    private const string MissingRootSummary = "Source root is no longer accessible.";
    private const string GenericDegradedSummary = "Indexing is degraded. Review recovery status and local host logs.";

    public static SourceResponse ToResponse(this SourceRecord source, SourceReconciliation? recovery = null) => new(
        source.SourceId,
        HashRootPath(source.CanonicalRootPath),
        source.DisplayName,
        source.Status,
        source.LastScanUtc,
        source.LastSuccessfulIndexUtc,
        SafeSourceError(source.LastError, recovery),
        recovery?.ToResponse());

    public static RecoveryResponse ToResponse(this SourceReconciliation recovery)
    {
        var causes = recovery.PendingCauses | recovery.ActiveCauses;
        var boundedCauses = Enum.GetValues<ReconciliationCause>()
            .Where(cause => cause != ReconciliationCause.None && causes.HasFlag(cause))
            .Select(cause => cause.ToString())
            .ToArray();
        var safeOutcome = recovery.LastOutcome is "Succeeded" or "RetryScheduled" or "Failed" or "Cancelled"
            ? recovery.LastOutcome
            : null;
        var safeSummary = recovery.LastErrorCode is { } errorCode
            ? new ReconciliationFailure(errorCode).SafeSummary
            : null;

        return new RecoveryResponse(
            recovery.State.ToString(),
            recovery.DesiredGeneration,
            recovery.CompletedGeneration,
            recovery.ActiveGeneration,
            boundedCauses,
            recovery.RequestedUtc,
            recovery.StartedUtc,
            recovery.LastSucceededUtc,
            recovery.LastFailedUtc,
            safeOutcome,
            recovery.LastErrorCode?.ToString(),
            safeSummary,
            Math.Max(0, recovery.LastChangedFiles),
            Math.Max(0, recovery.LastDeletedFiles),
            Math.Max(0, recovery.LastUnchangedFiles));
    }

    private static string? SafeSourceError(string? lastError, SourceReconciliation? recovery)
    {
        if (lastError is null) return null;
        if (string.Equals(lastError, MissingRootSummary, StringComparison.Ordinal)) return MissingRootSummary;
        if (recovery?.LastErrorCode is { } errorCode) return new ReconciliationFailure(errorCode).SafeSummary;
        return GenericDegradedSummary;
    }

    private static string HashRootPath(string rootPath)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
