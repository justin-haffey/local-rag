using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Management;

public sealed class LocalRagManagementService(
    ISourceRegistry sources,
    IIndexCoordinator coordinator,
    ManagementConfirmationStore confirmations,
    InstallationOwnershipStore ownership,
    ResetStateStore resetState,
    HostMaintenanceCoordinator maintenance,
    IManagementVectorStore vectors,
    SqliteDatabase database,
    IIndexStateStore indexState,
    IChunkProfileStateStore chunkProfiles,
    IndexJobStore jobs,
    IReconciliationStore reconciliations,
    IOptions<LocalRagOptions> options,
    SourceWatcherRegistry? watchers = null) : ILocalRagManagementService
{
    private const string RemoveAction = "remove";
    private const string ResetAction = "reset";
    private const string RemoveWarning = "This removes only the Local RAG index for the selected folder. Source files are preserved.";
    private const string ResetWarning = "This irreversibly resets all Local RAG SQLite state and the owned Weaviate collection. Source files are preserved.";

    public async Task<ManagementResult> IndexAsync(
        string rootPath,
        string? displayName,
        string principalId,
        CancellationToken cancellationToken)
    {
        var operationId = NewOperationId();
        var lease = maintenance.TryAcquireOperational(cancellationToken);
        if (lease is null) return Failed(operationId, "ResetInProgress");
        await using (lease)
        {
            if (!TryCanonicalizeDirectory(rootPath, out var canonical))
            {
                return Failed(operationId, "InvalidSourcePath");
            }

            var existing = (await sources.ListAsync(lease.CancellationToken))
                .FirstOrDefault(source => PathsEqual(source.CanonicalRootPath, canonical));
            if (existing is not null)
            {
                return new(operationId, ManagementOperationState.Accepted, "AlreadyRegistered", existing.SourceId);
            }

            try
            {
                var source = await sources.RegisterAsync(canonical, displayName, lease.CancellationToken);
                await coordinator.QueueInitialIndexAsync(source.SourceId, lease.CancellationToken);
                return new(operationId, ManagementOperationState.Accepted, SourceId: source.SourceId, SourcesAffected: 1);
            }
            catch (InvalidOperationException)
            {
                return Failed(operationId, "SourceConflict");
            }
            catch (DirectoryNotFoundException)
            {
                return Failed(operationId, "InvalidSourcePath");
            }
            catch (UnauthorizedAccessException)
            {
                return Failed(operationId, "InvalidSourcePath");
            }
        }
    }

    public async Task<ManagementResult> RemoveAsync(
        string rootPath,
        string? confirmationToken,
        string principalId,
        CancellationToken cancellationToken)
    {
        var operationId = NewOperationId();
        if (!TryCanonicalize(rootPath, out var canonical))
        {
            return Failed(operationId, "InvalidSourcePath");
        }

        var source = (await sources.ListAsync(cancellationToken))
            .FirstOrDefault(item => PathsEqual(item.CanonicalRootPath, canonical));
        if (source is null) return Failed(operationId, "SourceNotFound");

        if (string.IsNullOrWhiteSpace(confirmationToken))
        {
            var challenge = confirmations.Create(RemoveAction, canonical, principalId);
            return Confirmation(operationId, challenge, RemoveWarning);
        }
        if (!confirmations.Consume(confirmationToken, RemoveAction, canonical, principalId))
        {
            return Failed(operationId, "InvalidConfirmation");
        }

        var lease = maintenance.TryAcquireOperational(cancellationToken);
        if (lease is null) return Failed(operationId, "ResetInProgress");
        await using (lease)
        {
            var current = (await sources.ListAsync(lease.CancellationToken))
                .FirstOrDefault(item => PathsEqual(item.CanonicalRootPath, canonical));
            if (current is null) return Failed(operationId, "SourceNotFound");
            await coordinator.RemoveSourceAsync(current.SourceId, lease.CancellationToken);
            return new(operationId, ManagementOperationState.Completed, SourceId: current.SourceId, SourcesAffected: 1);
        }
    }

    public async Task<ManagementResult> ResetAsync(
        string? confirmationToken,
        string principalId,
        CancellationToken cancellationToken)
    {
        var operationId = NewOperationId();
        var ownershipId = await ownership.GetOrCreateAsync(cancellationToken);
        var resetTarget = ownershipId + ":" + options.Value.Weaviate.Collection;
        if (string.IsNullOrWhiteSpace(confirmationToken))
        {
            var challenge = confirmations.Create(ResetAction, resetTarget, principalId);
            return Confirmation(operationId, challenge, ResetWarning);
        }
        if (!confirmations.Consume(confirmationToken, ResetAction, resetTarget, principalId))
        {
            return Failed(operationId, "InvalidConfirmation");
        }
        var priorReset = await resetState.ReadAsync(cancellationToken);
        var recoveryAuthorized = priorReset is
        {
            State: "Running",
            PhaseCode: "SqliteCleared" or "RecoveryPrepared"
        } && string.Equals(priorReset.OwnershipId, ownershipId, StringComparison.Ordinal);
        if (!recoveryAuthorized && !await vectors.VerifyOwnershipAsync(ownershipId, cancellationToken))
        {
            return Failed(operationId, "OwnershipNotVerified");
        }

        HostMaintenanceCoordinator.MaintenanceLease? maintenanceLease;
        try
        {
            maintenanceLease = await maintenance.TryAcquireMaintenanceAsync(
                TimeSpan.FromSeconds(options.Value.Management.MaintenanceDrainTimeoutSeconds),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return Failed(operationId, "MaintenanceDrainTimeout");
        }
        if (maintenanceLease is null) return Failed(operationId, "ResetInProgress");

        await using (maintenanceLease)
        {
            try
            {
                watchers?.UntrackAll();
                await resetState.WriteAsync(
                    new(1, ownershipId, operationId, "Running", recoveryAuthorized ? "RecoveryPrepared" : "Prepared"),
                    cancellationToken);
                await database.ResetAsync(cancellationToken);
                await resetState.WriteAsync(new(1, ownershipId, operationId, "Running", "SqliteCleared"), cancellationToken);
                if (recoveryAuthorized)
                {
                    await vectors.RecoverOwnedCollectionAsync(ownershipId, cancellationToken);
                }
                else
                {
                    await vectors.ResetOwnedCollectionAsync(ownershipId, cancellationToken);
                }
                await resetState.WriteAsync(new(1, ownershipId, operationId, "Running", "CollectionRecreated"), cancellationToken);
                await sources.InitializeAsync(cancellationToken);
                await indexState.InitializeAsync(cancellationToken);
                await chunkProfiles.InitializeAsync(cancellationToken);
                await jobs.InitializeAsync(cancellationToken);
                await reconciliations.InitializeAsync(cancellationToken);
                if ((await sources.ListAsync(cancellationToken)).Count != 0)
                {
                    throw new InvalidOperationException("Reset verification failed.");
                }
                resetState.Complete();
                maintenanceLease.Complete();
                return new(operationId, ManagementOperationState.Completed, SourcesAffected: 0, CollectionsAffected: 1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                maintenance.MarkFailed();
                throw;
            }
            catch
            {
                maintenance.MarkFailed();
                return Failed(operationId, "ResetFailed");
            }
        }
    }

    private static ManagementResult Confirmation(string operationId, ConfirmationChallenge challenge, string warning) =>
        new(operationId, ManagementOperationState.ConfirmationRequired,
            ConfirmationToken: challenge.Token,
            ConfirmationExpiresUtc: challenge.ExpiresUtc,
            Warning: warning);

    private static ManagementResult Failed(string operationId, string errorCode) =>
        new(operationId, ManagementOperationState.Failed, errorCode);

    private static string NewOperationId() => Guid.NewGuid().ToString("N");

    private static bool TryCanonicalizeDirectory(string path, out string canonical) =>
        TryCanonicalize(path, out canonical) && Directory.Exists(canonical);

    private static bool TryCanonicalize(string path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
