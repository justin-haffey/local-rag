namespace LocalRag.Application;

public enum ManagementOperationState
{
    ConfirmationRequired,
    Accepted,
    Completed,
    Failed
}

public sealed record ManagementResult(
    string OperationId,
    ManagementOperationState State,
    string? ErrorCode = null,
    string? SourceId = null,
    int SourcesAffected = 0,
    int CollectionsAffected = 0,
    string? ConfirmationToken = null,
    DateTimeOffset? ConfirmationExpiresUtc = null,
    string? Warning = null);

public interface ILocalRagManagementService
{
    Task<ManagementResult> IndexAsync(string rootPath, string? displayName, string principalId, CancellationToken cancellationToken);
    Task<ManagementResult> RemoveAsync(string rootPath, string? confirmationToken, string principalId, CancellationToken cancellationToken);
    Task<ManagementResult> ResetAsync(string? confirmationToken, string principalId, CancellationToken cancellationToken);
}

public interface IManagementVectorStore
{
    Task<bool> VerifyOwnershipAsync(string ownershipId, CancellationToken cancellationToken);
    Task ResetOwnedCollectionAsync(string ownershipId, CancellationToken cancellationToken);
    Task RecoverOwnedCollectionAsync(string ownershipId, CancellationToken cancellationToken);
}
