using System.Globalization;
using LocalRag.Application;
using LocalRag.Domain;
using Microsoft.Data.Sqlite;

namespace LocalRag.Infrastructure.Sqlite;

/// <summary>SQLite-backed generation queue and recovery state for source reconciliation.</summary>
public sealed class SqliteReconciliationStore(SqliteDatabase database) : IReconciliationStore
{
    private const string ReconciliationKind = "Reconciliation";
    private const string LegacyKind = "Legacy";
    private const string WatcherFailureSummary = "File watcher overflowed or failed; recovery has been queued.";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS IndexJobs (
                  JobId TEXT PRIMARY KEY,
                  SourceId TEXT NOT NULL REFERENCES Sources(SourceId) ON DELETE CASCADE,
                  Status TEXT NOT NULL,
                  RequestedUtc TEXT NOT NULL,
                  StartedUtc TEXT NULL,
                  CompletedUtc TEXT NULL,
                  Error TEXT NULL,
                  Attempt INTEGER NOT NULL DEFAULT 0,
                  NextAttemptUtc TEXT NULL,
                  TargetChunkProfileFingerprint TEXT NULL,
                  ForceContentProcessing INTEGER NOT NULL DEFAULT 0,
                  JobKind TEXT NOT NULL DEFAULT 'Legacy',
                  Generation INTEGER NOT NULL DEFAULT 0,
                  CauseMask INTEGER NOT NULL DEFAULT 0,
                  LeaseId TEXT NULL,
                  LeaseExpiresUtc TEXT NULL,
                  LifecycleEpoch INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS DeadLetterJobs (
                  JobId TEXT PRIMARY KEY,
                  SourceId TEXT NOT NULL,
                  Error TEXT NOT NULL,
                  FailedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SourceReconciliations (
                  SourceId TEXT PRIMARY KEY REFERENCES Sources(SourceId) ON DELETE CASCADE,
                  DesiredGeneration INTEGER NOT NULL DEFAULT 0 CHECK (DesiredGeneration >= 0),
                  CompletedGeneration INTEGER NOT NULL DEFAULT 0 CHECK (CompletedGeneration >= 0),
                  ActiveGeneration INTEGER NULL CHECK (ActiveGeneration IS NULL OR ActiveGeneration > 0),
                  State TEXT NOT NULL DEFAULT 'Clean' CHECK (State IN ('Clean', 'Queued', 'Running', 'Degraded')),
                  PendingCauseMask INTEGER NOT NULL DEFAULT 0 CHECK (PendingCauseMask >= 0),
                  ActiveCauseMask INTEGER NOT NULL DEFAULT 0 CHECK (ActiveCauseMask >= 0),
                  RequestedUtc TEXT NULL,
                  StartedUtc TEXT NULL,
                  LastSucceededUtc TEXT NULL,
                  LastFailedUtc TEXT NULL,
                  LastOutcome TEXT NULL,
                  LastErrorCode TEXT NULL,
                  LastErrorSummary TEXT NULL,
                  LastChangedFiles INTEGER NOT NULL DEFAULT 0,
                  LastDeletedFiles INTEGER NOT NULL DEFAULT 0,
                  LastUnchangedFiles INTEGER NOT NULL DEFAULT 0,
                  CHECK (CompletedGeneration <= DesiredGeneration),
                  CHECK (ActiveGeneration IS NULL OR ActiveGeneration <= DesiredGeneration),
                  CHECK ((State = 'Running' AND ActiveGeneration IS NOT NULL AND CompletedGeneration < ActiveGeneration AND ActiveCauseMask > 0)
                      OR (State <> 'Running' AND ActiveGeneration IS NULL AND ActiveCauseMask = 0)),
                  CHECK (State <> 'Clean' OR (DesiredGeneration = CompletedGeneration AND ActiveGeneration IS NULL AND PendingCauseMask = 0 AND ActiveCauseMask = 0))
                );
                CREATE TABLE IF NOT EXISTS SchemaVersions (
                  Name TEXT PRIMARY KEY,
                  Version INTEGER NOT NULL,
                  AppliedUtc TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureJobColumnsAsync(connection, transaction, cancellationToken);
        await SeedStatesAsync(connection, transaction, cancellationToken);
        await MigrateLegacyJobsAsync(connection, transaction, cancellationToken);

        await using (var indexes = connection.CreateCommand())
        {
            indexes.Transaction = transaction;
            indexes.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS UX_IndexJobs_Reconciliation_Processing
                  ON IndexJobs(SourceId)
                  WHERE JobKind = 'Reconciliation' AND Status = 'Processing';
                CREATE UNIQUE INDEX IF NOT EXISTS UX_IndexJobs_Reconciliation_Pending
                  ON IndexJobs(SourceId)
                  WHERE JobKind = 'Reconciliation' AND Status = 'Pending';
                CREATE UNIQUE INDEX IF NOT EXISTS UX_IndexJobs_Reconciliation_Generation
                  ON IndexJobs(SourceId, Generation)
                  WHERE JobKind = 'Reconciliation' AND Status IN ('Pending', 'RetryScheduled', 'Processing');
                CREATE INDEX IF NOT EXISTS IX_IndexJobs_Reconciliation_Due
                  ON IndexJobs(JobKind, Status, NextAttemptUtc, LeaseExpiresUtc, RequestedUtc);
                INSERT INTO SchemaVersions(Name, Version, AppliedUtc)
                  VALUES ('reconciliation', 1, $now)
                  ON CONFLICT(Name) DO UPDATE SET Version = MAX(Version, 1), AppliedUtc = excluded.AppliedUtc;
                """;
            indexes.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            await indexes.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReconciliationRequestResult> RequestAsync(
        ReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        if (request.Causes == ReconciliationCause.None) throw new ArgumentOutOfRangeException(nameof(request));

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        var lifecycle = await ReadLifecycleAsync(connection, transaction, request.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("The requested source does not exist.");
        if (lifecycle.State != SourceLifecycleState.Active)
        {
            throw new InvalidOperationException("A source being removed cannot accept reconciliation work.");
        }

        var now = DateTimeOffset.UtcNow;
        await EnsureSourceStateAsync(connection, transaction, request.SourceId, cancellationToken);
        await ProjectQueuedSourceAsync(connection, transaction, request.SourceId, now, cancellationToken);
        if ((request.Causes & (ReconciliationCause.WatcherError | ReconciliationCause.WatcherOverflow)) != 0)
        {
            await ProjectWatcherFailureAsync(connection, transaction, request.SourceId, now, cancellationToken);
        }
        var state = await ReadStateAsync(connection, transaction, request.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("The source reconciliation state was not initialized.");
        var processing = await ReadJobAsync(connection, transaction, request.SourceId, "Processing", cancellationToken);
        var retry = await ReadJobAsync(connection, transaction, request.SourceId, "RetryScheduled", cancellationToken);
        var pending = await ReadJobAsync(connection, transaction, request.SourceId, "Pending", cancellationToken);

        if (processing is not null)
        {
            var successorRequest = request with
            {
                TargetChunkProfileFingerprint = request.TargetChunkProfileFingerprint ?? processing.TargetFingerprint,
                ForceContentProcessing = request.ForceContentProcessing || processing.Force
            };
            if (pending is not null)
            {
                var merged = pending.Causes | request.Causes;
                var coalescedRequest = successorRequest with
                {
                    TargetChunkProfileFingerprint = request.TargetChunkProfileFingerprint
                        ?? pending.TargetFingerprint
                        ?? processing.TargetFingerprint,
                    ForceContentProcessing = request.ForceContentProcessing || pending.Force || processing.Force
                };
                await UpdatePendingAsync(connection, transaction, pending.JobId, merged, coalescedRequest, now, cancellationToken);
                await UpdateQueuedStateAsync(connection, transaction, state, pending.Generation, merged, now, keepRunning: true, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(pending.Generation, false, false, ReconciliationState.Running);
            }

            var generation = Math.Max(state.DesiredGeneration, processing.Generation) + 1;
            await InsertJobAsync(connection, transaction, successorRequest, generation, lifecycle.Epoch, now, cancellationToken);
            await UpdateQueuedStateAsync(connection, transaction, state, generation, request.Causes, now, keepRunning: true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(generation, true, false, ReconciliationState.Running);
        }

        if (retry is not null)
        {
            var expedite = request.Causes.HasFlag(ReconciliationCause.Manual);
            var mergedRetryCauses = retry.Causes | request.Causes;
            if (pending is not null)
            {
                var mergedSuccessorCauses = pending.Causes | request.Causes;
                var successorRequest = request with
                {
                    TargetChunkProfileFingerprint = request.TargetChunkProfileFingerprint
                        ?? pending.TargetFingerprint
                        ?? retry.TargetFingerprint,
                    ForceContentProcessing = request.ForceContentProcessing || pending.Force || retry.Force
                };
                await UpdatePendingAsync(
                    connection,
                    transaction,
                    pending.JobId,
                    mergedSuccessorCauses,
                    successorRequest,
                    now,
                    cancellationToken);
                await UpdateRetryAsync(
                    connection,
                    transaction,
                    retry,
                    mergedRetryCauses,
                    request with { TargetChunkProfileFingerprint = null, ForceContentProcessing = false },
                    now,
                    expedite,
                    cancellationToken);
                await UpdateQueuedStateAsync(
                    connection,
                    transaction,
                    state,
                    pending.Generation,
                    mergedRetryCauses | mergedSuccessorCauses,
                    now,
                    keepRunning: false,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(pending.Generation, false, expedite, ReconciliationState.Queued);
            }

            await UpdateRetryAsync(
                connection,
                transaction,
                retry,
                mergedRetryCauses,
                request,
                now,
                expedite,
                cancellationToken);
            await UpdateQueuedStateAsync(
                connection,
                transaction,
                state,
                retry.Generation,
                mergedRetryCauses,
                now,
                keepRunning: false,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(retry.Generation, false, expedite, ReconciliationState.Queued);
        }

        if (pending is not null)
        {
            var merged = pending.Causes | request.Causes;
            await UpdatePendingAsync(connection, transaction, pending.JobId, merged, request, now, cancellationToken);
            await UpdateQueuedStateAsync(connection, transaction, state, pending.Generation, merged, now, keepRunning: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(pending.Generation, false, true, ReconciliationState.Queued);
        }

        var nextGeneration = state.State == ReconciliationState.Degraded && state.DesiredGeneration > state.CompletedGeneration
            ? state.DesiredGeneration
            : state.DesiredGeneration + 1;
        var failed = await ReadJobByGenerationAsync(connection, transaction, request.SourceId, nextGeneration, cancellationToken);
        if (failed is not null && failed.Status == "Failed")
        {
            await ReactivateFailedAsync(connection, transaction, failed, request, lifecycle.Epoch, now, cancellationToken);
        }
        else
        {
            await InsertJobAsync(connection, transaction, request, nextGeneration, lifecycle.Epoch, now, cancellationToken);
        }
        await UpdateQueuedStateAsync(connection, transaction, state, nextGeneration, request.Causes, now, keepRunning: false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(nextGeneration, true, true, ReconciliationState.Queued);
    }

    public async Task<ReconciliationLease?> TryLeaseAsync(
        string sourceId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT j.JobId, j.Generation, j.Attempt, j.CauseMask,
                   j.TargetChunkProfileFingerprint, j.ForceContentProcessing, s.LifecycleEpoch
            FROM IndexJobs j
            JOIN Sources s ON s.SourceId = j.SourceId
            WHERE j.SourceId = $source AND j.JobKind = 'Reconciliation'
              AND j.Status IN ('Pending', 'RetryScheduled')
              AND (j.NextAttemptUtc IS NULL OR j.NextAttemptUtc <= $now)
              AND s.LifecycleState = 'Active' AND s.Status <> 'Paused'
              AND NOT EXISTS (
                SELECT 1 FROM IndexJobs earlier
                WHERE earlier.SourceId = j.SourceId AND earlier.JobKind = 'Reconciliation'
                  AND earlier.Status IN ('Pending', 'RetryScheduled', 'Processing')
                  AND earlier.Generation < j.Generation)
              AND NOT EXISTS (
                SELECT 1 FROM IndexJobs active
                WHERE active.SourceId = j.SourceId AND active.JobKind = 'Reconciliation' AND active.Status = 'Processing')
            ORDER BY j.Generation
            LIMIT 1;
            """;
        find.Parameters.AddWithValue("$source", sourceId);
        find.Parameters.AddWithValue("$now", Format(now));
        JobRow? job;
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            job = await reader.ReadAsync(cancellationToken)
                ? new JobRow(
                    reader.GetString(0), sourceId, reader.GetInt64(1), "Pending", reader.GetInt32(2),
                    (ReconciliationCause)reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) != 0,
                    reader.GetInt64(6))
                : null;
        }
        if (job is null) return null;

        var leaseId = Guid.NewGuid().ToString("N");
        var expires = now.Add(leaseDuration);
        await using (var lease = connection.CreateCommand())
        {
            lease.Transaction = transaction;
            lease.CommandText = """
                UPDATE IndexJobs
                SET Status = 'Processing', StartedUtc = $now, LeaseId = $lease,
                    LeaseExpiresUtc = $expires, NextAttemptUtc = NULL, LifecycleEpoch = $epoch
                WHERE JobId = $id AND JobKind = 'Reconciliation' AND Status IN ('Pending', 'RetryScheduled');
                """;
            lease.Parameters.AddWithValue("$id", job.JobId);
            lease.Parameters.AddWithValue("$now", Format(now));
            lease.Parameters.AddWithValue("$lease", leaseId);
            lease.Parameters.AddWithValue("$expires", Format(expires));
            lease.Parameters.AddWithValue("$epoch", job.LifecycleEpoch);
            if (await lease.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        }

        var successorCauses = await ReadPendingCauseMaskAsync(connection, transaction, sourceId, cancellationToken);
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE SourceReconciliations
                SET State = 'Running', ActiveGeneration = $generation, ActiveCauseMask = $causes,
                    PendingCauseMask = $pending, StartedUtc = $now
                WHERE SourceId = $source;
                UPDATE Sources SET Status = 'Indexing', LastError = NULL, UpdatedUtc = $now
                WHERE SourceId = $source AND LifecycleState = 'Active' AND Status <> 'Paused';
                """;
            state.Parameters.AddWithValue("$source", sourceId);
            state.Parameters.AddWithValue("$generation", job.Generation);
            state.Parameters.AddWithValue("$causes", (long)job.Causes);
            state.Parameters.AddWithValue("$pending", (long)successorCauses);
            state.Parameters.AddWithValue("$now", Format(now));
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(sourceId, job.Generation, job.JobId, leaseId, expires, job.Attempt, job.Causes,
            job.TargetFingerprint, job.Force, job.LifecycleEpoch);
    }

    public async Task<bool> RenewLeaseAsync(
        ReconciliationLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE IndexJobs SET LeaseExpiresUtc = $expires
            WHERE JobId = $id AND SourceId = $source AND Generation = $generation
              AND JobKind = 'Reconciliation' AND Status = 'Processing' AND LeaseId = $lease
              AND LifecycleEpoch = $epoch AND LeaseExpiresUtc > $now
              AND EXISTS (SELECT 1 FROM Sources s WHERE s.SourceId = $source
                AND s.LifecycleState = 'Active' AND s.LifecycleEpoch = $epoch);
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$expires", Format(now.Add(leaseDuration)));
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<ReconciliationCompletionResult> CompleteAsync(
        ReconciliationLease lease,
        ReconciliationResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        if (!await IsLeaseCurrentAsync(connection, transaction, lease, cancellationToken)) return new(false, false, false);
        var now = DateTimeOffset.UtcNow;
        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE IndexJobs SET Status = 'Completed', CompletedUtc = $now, Error = NULL,
                    LeaseId = NULL, LeaseExpiresUtc = NULL
                WHERE JobId = $id AND LeaseId = $lease AND Status = 'Processing';
                """;
            complete.Parameters.AddWithValue("$id", lease.JobId);
            complete.Parameters.AddWithValue("$lease", lease.LeaseId);
            complete.Parameters.AddWithValue("$now", Format(now));
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1) return new(false, false, false);
        }

        var pending = await ReadJobAsync(connection, transaction, lease.SourceId, "Pending", cancellationToken)
            ?? await ReadJobAsync(connection, transaction, lease.SourceId, "RetryScheduled", cancellationToken);
        var hasSuccessor = pending is not null;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE SourceReconciliations SET
                    CompletedGeneration = MAX(CompletedGeneration, $generation),
                    ActiveGeneration = NULL, ActiveCauseMask = 0,
                    State = $state, PendingCauseMask = $pending,
                    LastSucceededUtc = $now, LastOutcome = 'Succeeded',
                    LastErrorCode = NULL, LastErrorSummary = NULL,
                    LastChangedFiles = $changed, LastDeletedFiles = $deleted,
                    LastUnchangedFiles = $unchanged
                WHERE SourceId = $source AND ActiveGeneration = $generation;
                UPDATE Sources SET Status = $sourceStatus, LastError = NULL,
                    UpdatedUtc = $now, LastScanUtc = $now,
                    LastSuccessfulIndexUtc = CASE WHEN $ready = 1 THEN $now ELSE LastSuccessfulIndexUtc END
                WHERE SourceId = $source AND LifecycleState = 'Active' AND LifecycleEpoch = $epoch
                  AND Status <> 'Paused';
                """;
            state.Parameters.AddWithValue("$generation", lease.Generation);
            state.Parameters.AddWithValue("$state", hasSuccessor ? "Queued" : "Clean");
            state.Parameters.AddWithValue("$pending", pending is null ? 0L : (long)pending.Causes);
            state.Parameters.AddWithValue("$now", Format(now));
            state.Parameters.AddWithValue("$changed", result.ChangedFiles);
            state.Parameters.AddWithValue("$deleted", result.DeletedFiles);
            state.Parameters.AddWithValue("$unchanged", result.UnchangedFiles);
            state.Parameters.AddWithValue("$source", lease.SourceId);
            state.Parameters.AddWithValue("$sourceStatus", hasSuccessor ? SourceStatus.Indexing.ToString() : SourceStatus.Ready.ToString());
            state.Parameters.AddWithValue("$ready", hasSuccessor ? 0 : 1);
            state.Parameters.AddWithValue("$epoch", lease.LifecycleEpoch);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, hasSuccessor, !hasSuccessor);
    }

    public async Task<ReconciliationFailureResult> FailAsync(
        ReconciliationLease lease,
        ReconciliationFailure failure,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        if (!await IsLeaseCurrentAsync(connection, transaction, lease, cancellationToken)) return new(false, false, false, null);
        var now = DateTimeOffset.UtcNow;
        var attempt = lease.Attempt + 1;
        var retry = failure.IsTransient && attempt < maxAttempts;
        var next = retry ? now.Add(retryDelay) : (DateTimeOffset?)null;

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE IndexJobs SET Status = $status, Attempt = $attempt,
                    NextAttemptUtc = $next, CompletedUtc = CASE WHEN $terminal = 1 THEN $now ELSE NULL END,
                    Error = $summary, LeaseId = NULL, LeaseExpiresUtc = NULL,
                    CauseMask = CASE WHEN $retry = 1 THEN CauseMask | $retryCause ELSE CauseMask END
                WHERE JobId = $id AND LeaseId = $lease AND Status = 'Processing';
                """;
            update.Parameters.AddWithValue("$status", retry ? "RetryScheduled" : "Failed");
            update.Parameters.AddWithValue("$attempt", attempt);
            update.Parameters.AddWithValue("$next", next is null ? DBNull.Value : Format(next.Value));
            update.Parameters.AddWithValue("$terminal", retry ? 0 : 1);
            update.Parameters.AddWithValue("$retry", retry ? 1 : 0);
            update.Parameters.AddWithValue("$retryCause", (long)ReconciliationCause.Retry);
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$summary", failure.SafeSummary);
            update.Parameters.AddWithValue("$id", lease.JobId);
            update.Parameters.AddWithValue("$lease", lease.LeaseId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return new(false, false, false, null);
        }
        if (!retry)
        {
            await using var deadLetter = connection.CreateCommand();
            deadLetter.Transaction = transaction;
            deadLetter.CommandText = """
                INSERT INTO DeadLetterJobs(JobId, SourceId, Error, FailedUtc)
                VALUES($id, $source, $error, $now)
                ON CONFLICT(JobId) DO UPDATE SET Error = excluded.Error, FailedUtc = excluded.FailedUtc;
                """;
            deadLetter.Parameters.AddWithValue("$id", lease.JobId);
            deadLetter.Parameters.AddWithValue("$source", lease.SourceId);
            deadLetter.Parameters.AddWithValue("$error", failure.SafeSummary);
            deadLetter.Parameters.AddWithValue("$now", Format(now));
            await deadLetter.ExecuteNonQueryAsync(cancellationToken);
        }

        var successor = await ReadJobAsync(connection, transaction, lease.SourceId, "Pending", cancellationToken);
        var stateName = retry || successor is not null ? "Queued" : "Degraded";
        var pendingCauses = retry
            ? lease.Causes | ReconciliationCause.Retry | (successor?.Causes ?? ReconciliationCause.None)
            : successor?.Causes ?? ReconciliationCause.None;
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE SourceReconciliations SET State = $state, ActiveGeneration = NULL,
                    ActiveCauseMask = 0, PendingCauseMask = $pending,
                    LastFailedUtc = $now, LastOutcome = $outcome,
                    LastErrorCode = $code, LastErrorSummary = $summary
                WHERE SourceId = $source AND ActiveGeneration = $generation;
                UPDATE Sources SET Status = 'Degraded', LastError = $summary,
                    UpdatedUtc = $now, LastScanUtc = $now
                WHERE SourceId = $source AND LifecycleState = 'Active'
                  AND LifecycleEpoch = $epoch AND Status <> 'Paused';
                """;
            state.Parameters.AddWithValue("$state", stateName);
            state.Parameters.AddWithValue("$pending", (long)pendingCauses);
            state.Parameters.AddWithValue("$now", Format(now));
            state.Parameters.AddWithValue("$outcome", retry ? "RetryScheduled" : "Failed");
            state.Parameters.AddWithValue("$code", failure.Code.ToString());
            state.Parameters.AddWithValue("$summary", failure.SafeSummary);
            state.Parameters.AddWithValue("$source", lease.SourceId);
            state.Parameters.AddWithValue("$generation", lease.Generation);
            state.Parameters.AddWithValue("$epoch", lease.LifecycleEpoch);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, !retry, successor is not null, next);
    }

    public async Task<bool> ReleaseAsync(ReconciliationLease lease, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var release = connection.CreateCommand();
        release.Transaction = transaction;
        release.CommandText = """
            UPDATE IndexJobs SET Status = 'RetryScheduled', NextAttemptUtc = $now,
                LeaseId = NULL, LeaseExpiresUtc = NULL, Error = 'Reconciliation was interrupted and requeued.',
                CauseMask = CauseMask | $retryCause
            WHERE JobId = $id AND SourceId = $source AND Generation = $generation
              AND Status = 'Processing' AND LeaseId = $lease AND LifecycleEpoch = $epoch;
            """;
        release.Parameters.AddWithValue("$now", Format(now));
        release.Parameters.AddWithValue("$retryCause", (long)ReconciliationCause.Retry);
        AddLeaseParameters(release, lease);
        if (await release.ExecuteNonQueryAsync(cancellationToken) != 1) return false;

        var pending = await ReadJobAsync(connection, transaction, lease.SourceId, "Pending", cancellationToken);
        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            UPDATE SourceReconciliations SET State = 'Queued', ActiveGeneration = NULL,
                ActiveCauseMask = 0, PendingCauseMask = $causes
            WHERE SourceId = $source AND ActiveGeneration = $generation;
            """;
        state.Parameters.AddWithValue(
            "$causes",
            (long)(lease.Causes | ReconciliationCause.Retry | (pending?.Causes ?? ReconciliationCause.None)));
        state.Parameters.AddWithValue("$source", lease.SourceId);
        state.Parameters.AddWithValue("$generation", lease.Generation);
        await state.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetDueSourceIdsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT j.SourceId FROM IndexJobs j
            JOIN Sources s ON s.SourceId = j.SourceId
            WHERE j.JobKind = 'Reconciliation' AND j.Status IN ('Pending', 'RetryScheduled')
              AND (j.NextAttemptUtc IS NULL OR j.NextAttemptUtc <= $now)
              AND s.LifecycleState = 'Active' AND s.Status <> 'Paused'
              AND NOT EXISTS (
                SELECT 1 FROM IndexJobs earlier
                WHERE earlier.SourceId = j.SourceId AND earlier.JobKind = 'Reconciliation'
                  AND earlier.Status IN ('Pending', 'RetryScheduled', 'Processing')
                  AND earlier.Generation < j.Generation)
            ORDER BY j.SourceId;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(reader.GetString(0));
        return results;
    }

    public async Task<DateTimeOffset?> GetNextDueUtcAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(CASE
              WHEN j.Status = 'Processing' THEN j.LeaseExpiresUtc
              ELSE COALESCE(j.NextAttemptUtc, $now)
            END)
            FROM IndexJobs j
            JOIN Sources s ON s.SourceId = j.SourceId
            WHERE j.JobKind = 'Reconciliation'
              AND s.LifecycleState = 'Active' AND s.Status <> 'Paused'
              AND (
                (j.Status = 'Processing' AND j.LeaseExpiresUtc IS NOT NULL)
                OR (
                  j.Status IN ('Pending', 'RetryScheduled')
                  AND NOT EXISTS (
                    SELECT 1 FROM IndexJobs earlier
                    WHERE earlier.SourceId = j.SourceId AND earlier.JobKind = 'Reconciliation'
                      AND earlier.Status IN ('Pending', 'RetryScheduled', 'Processing')
                      AND earlier.Generation < j.Generation)
                )
              );
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    public async Task<IReadOnlyList<string>> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        var sources = new List<(string SourceId, long Generation, ReconciliationCause Causes)>();
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT SourceId, Generation, CauseMask FROM IndexJobs
                WHERE JobKind = 'Reconciliation' AND Status = 'Processing'
                  AND LeaseExpiresUtc IS NOT NULL AND LeaseExpiresUtc <= $now;
                """;
            find.Parameters.AddWithValue("$now", Format(now));
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sources.Add((reader.GetString(0), reader.GetInt64(1), (ReconciliationCause)reader.GetInt64(2)));
            }
        }
        foreach (var item in sources)
        {
            await using var recover = connection.CreateCommand();
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE IndexJobs SET Status = 'RetryScheduled', NextAttemptUtc = $now,
                    LeaseId = NULL, LeaseExpiresUtc = NULL, Error = 'Recovered after lease expiry.',
                    CauseMask = CauseMask | $retryCause
                WHERE SourceId = $source AND Generation = $generation
                  AND JobKind = 'Reconciliation' AND Status = 'Processing';
                UPDATE SourceReconciliations SET State = 'Queued', ActiveGeneration = NULL,
                    ActiveCauseMask = 0, PendingCauseMask = PendingCauseMask | $causes,
                    LastOutcome = 'LeaseRecovered'
                WHERE SourceId = $source AND ActiveGeneration = $generation;
                """;
            recover.Parameters.AddWithValue("$now", Format(now));
            recover.Parameters.AddWithValue("$source", item.SourceId);
            recover.Parameters.AddWithValue("$generation", item.Generation);
            recover.Parameters.AddWithValue("$causes", (long)(item.Causes | ReconciliationCause.Retry));
            recover.Parameters.AddWithValue("$retryCause", (long)ReconciliationCause.Retry);
            await recover.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return sources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<SourceReconciliation?> GetAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = StateSelect + " WHERE SourceId = $source;";
        command.Parameters.AddWithValue("$source", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    public async Task<IReadOnlyList<SourceReconciliation>> ListAsync(CancellationToken cancellationToken)
    {
        var results = new List<SourceReconciliation>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = StateSelect + " ORDER BY SourceId;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadState(reader));
        return results;
    }

    public async Task<SourceLifecycle?> TombstoneAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await database.BeginImmediateTransactionAsync(connection, cancellationToken);
        var lifecycle = await ReadLifecycleAsync(connection, transaction, sourceId, cancellationToken);
        if (lifecycle is null) return null;
        var epoch = lifecycle.Value.Epoch;
        if (lifecycle.Value.State == SourceLifecycleState.Active)
        {
            epoch++;
            await using var tombstone = connection.CreateCommand();
            tombstone.Transaction = transaction;
            tombstone.CommandText = """
                UPDATE Sources SET LifecycleState = 'Removing', LifecycleEpoch = $epoch, UpdatedUtc = $now
                WHERE SourceId = $source AND LifecycleState = 'Active';
                UPDATE IndexJobs SET Status = 'Cancelled', CompletedUtc = $now,
                    Error = 'Source removal cancelled pending reconciliation.'
                WHERE SourceId = $source AND JobKind = 'Reconciliation'
                  AND Status IN ('Pending', 'RetryScheduled');
                """;
            tombstone.Parameters.AddWithValue("$epoch", epoch);
            tombstone.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            tombstone.Parameters.AddWithValue("$source", sourceId);
            await tombstone.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(sourceId, SourceLifecycleState.Removing, epoch);
    }

    public async Task<bool> IsLifecycleCurrentAsync(
        string sourceId,
        long expectedEpoch,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM Sources
              WHERE SourceId = $source AND LifecycleState = 'Active' AND LifecycleEpoch = $epoch);
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$epoch", expectedEpoch);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    public async Task PruneCompletedAsync(int historyLimit, CancellationToken cancellationToken)
    {
        if (historyLimit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM DeadLetterJobs
            WHERE EXISTS (
              SELECT 1 FROM IndexJobs j
              WHERE j.JobId = DeadLetterJobs.JobId
                AND j.JobKind = 'Reconciliation' AND j.Status <> 'Failed');

            WITH ranked AS (
              SELECT j.JobId,
                     ROW_NUMBER() OVER (PARTITION BY j.SourceId ORDER BY j.CompletedUtc DESC, j.Generation DESC) AS rn
              FROM IndexJobs j
              JOIN SourceReconciliations r ON r.SourceId = j.SourceId
              WHERE j.JobKind = 'Reconciliation'
                AND (j.Status IN ('Completed', 'Cancelled', 'Superseded')
                  OR (j.Status = 'Failed' AND j.Generation <= r.CompletedGeneration))
            )
            DELETE FROM DeadLetterJobs
            WHERE JobId IN (SELECT JobId FROM ranked WHERE rn > $limit);

            WITH ranked AS (
              SELECT j.JobId,
                     ROW_NUMBER() OVER (PARTITION BY j.SourceId ORDER BY j.CompletedUtc DESC, j.Generation DESC) AS rn
              FROM IndexJobs j
              JOIN SourceReconciliations r ON r.SourceId = j.SourceId
              WHERE j.JobKind = 'Reconciliation'
                AND (j.Status IN ('Completed', 'Cancelled', 'Superseded')
                  OR (j.Status = 'Failed' AND j.Generation <= r.CompletedGeneration))
            )
            DELETE FROM IndexJobs WHERE JobId IN (SELECT JobId FROM ranked WHERE rn > $limit);
            """;
        command.Parameters.AddWithValue("$limit", historyLimit);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureJobColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(IndexJobs);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }
        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Attempt"] = "ALTER TABLE IndexJobs ADD COLUMN Attempt INTEGER NOT NULL DEFAULT 0;",
            ["NextAttemptUtc"] = "ALTER TABLE IndexJobs ADD COLUMN NextAttemptUtc TEXT NULL;",
            ["TargetChunkProfileFingerprint"] = "ALTER TABLE IndexJobs ADD COLUMN TargetChunkProfileFingerprint TEXT NULL;",
            ["ForceContentProcessing"] = "ALTER TABLE IndexJobs ADD COLUMN ForceContentProcessing INTEGER NOT NULL DEFAULT 0;",
            ["JobKind"] = "ALTER TABLE IndexJobs ADD COLUMN JobKind TEXT NOT NULL DEFAULT 'Legacy';",
            ["Generation"] = "ALTER TABLE IndexJobs ADD COLUMN Generation INTEGER NOT NULL DEFAULT 0;",
            ["CauseMask"] = "ALTER TABLE IndexJobs ADD COLUMN CauseMask INTEGER NOT NULL DEFAULT 0;",
            ["LeaseId"] = "ALTER TABLE IndexJobs ADD COLUMN LeaseId TEXT NULL;",
            ["LeaseExpiresUtc"] = "ALTER TABLE IndexJobs ADD COLUMN LeaseExpiresUtc TEXT NULL;",
            ["LifecycleEpoch"] = "ALTER TABLE IndexJobs ADD COLUMN LifecycleEpoch INTEGER NOT NULL DEFAULT 0;"
        };
        foreach (var addition in additions.Where(item => !columns.Contains(item.Key)))
        {
            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = addition.Value;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SeedStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var seed = connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = """
            INSERT OR IGNORE INTO SourceReconciliations(SourceId, DesiredGeneration, CompletedGeneration, State)
            SELECT SourceId, 0, 0, 'Clean' FROM Sources;
            """;
        await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSourceStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var seed = connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = """
            INSERT OR IGNORE INTO SourceReconciliations(SourceId, DesiredGeneration, CompletedGeneration, State)
            VALUES($source, 0, 0, 'Clean');
            """;
        seed.Parameters.AddWithValue("$source", sourceId);
        await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ProjectWatcherFailureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE Sources SET Status = 'Degraded', LastError = $summary, UpdatedUtc = $now
            WHERE SourceId = $source AND LifecycleState = 'Active' AND Status <> 'Paused';
            """;
        update.Parameters.AddWithValue("$summary", WatcherFailureSummary);
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$source", sourceId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ProjectQueuedSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE Sources
            SET Status = CASE WHEN Status = 'Degraded' THEN Status ELSE 'Indexing' END,
                UpdatedUtc = $now
            WHERE SourceId = $source AND LifecycleState = 'Active' AND Status <> 'Paused';
            """;
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$source", sourceId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateLegacyJobsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyJob>();
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT JobId, SourceId, RequestedUtc, Attempt, TargetChunkProfileFingerprint, ForceContentProcessing
                FROM IndexJobs
                WHERE JobKind = 'Legacy' AND Status IN ('Pending', 'Processing')
                ORDER BY SourceId, RequestedUtc, JobId;
                """;
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) != 0));
            }
        }

        foreach (var group in rows.GroupBy(row => row.SourceId, StringComparer.Ordinal))
        {
            var ordered = group.ToArray();
            var generations = Math.Min(2, ordered.Length);
            for (var index = 0; index < generations; index++)
            {
                var mergedRows = index == 0 ? ordered.Take(1) : ordered.Skip(1);
                var target = mergedRows.Select(row => row.TargetFingerprint).LastOrDefault(value => value is not null);
                var force = mergedRows.Any(row => row.Force);
                await using var migrate = connection.CreateCommand();
                migrate.Transaction = transaction;
                migrate.CommandText = """
                    UPDATE IndexJobs SET JobKind = 'Reconciliation', Generation = $generation,
                        Status = 'Pending', CauseMask = $causes, StartedUtc = NULL,
                        LeaseId = NULL, LeaseExpiresUtc = NULL,
                        TargetChunkProfileFingerprint = $target,
                        ForceContentProcessing = $force
                    WHERE JobId = $id;
                    """;
                migrate.Parameters.AddWithValue("$generation", index + 1);
                migrate.Parameters.AddWithValue("$causes", (long)(ReconciliationCause.Startup | ReconciliationCause.Retry));
                migrate.Parameters.AddWithValue("$target", target is null ? DBNull.Value : target);
                migrate.Parameters.AddWithValue("$force", force ? 1 : 0);
                migrate.Parameters.AddWithValue("$id", ordered[index].JobId);
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var redundant in ordered.Skip(generations))
            {
                await using var supersede = connection.CreateCommand();
                supersede.Transaction = transaction;
                supersede.CommandText = "UPDATE IndexJobs SET Status = 'Superseded', CompletedUtc = $now WHERE JobId = $id;";
                supersede.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
                supersede.Parameters.AddWithValue("$id", redundant.JobId);
                await supersede.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE SourceReconciliations SET DesiredGeneration = $desired,
                    State = 'Queued', PendingCauseMask = $causes,
                    RequestedUtc = $requested
                WHERE SourceId = $source;
                """;
            state.Parameters.AddWithValue("$desired", generations);
            state.Parameters.AddWithValue("$causes", (long)(ReconciliationCause.Startup | ReconciliationCause.Retry));
            state.Parameters.AddWithValue("$requested", ordered[^1].RequestedUtc);
            state.Parameters.AddWithValue("$source", group.Key);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationRequest request,
        long generation,
        long lifecycleEpoch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO IndexJobs(
              JobId, SourceId, Status, RequestedUtc, Attempt, NextAttemptUtc,
              TargetChunkProfileFingerprint, ForceContentProcessing,
              JobKind, Generation, CauseMask, LifecycleEpoch)
            VALUES($id, $source, 'Pending', $now, 0, NULL, $target, $force,
              'Reconciliation', $generation, $causes, $epoch);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$source", request.SourceId);
        insert.Parameters.AddWithValue("$now", Format(now));
        insert.Parameters.AddWithValue("$target", request.TargetChunkProfileFingerprint is null ? DBNull.Value : request.TargetChunkProfileFingerprint);
        insert.Parameters.AddWithValue("$force", request.ForceContentProcessing ? 1 : 0);
        insert.Parameters.AddWithValue("$generation", generation);
        insert.Parameters.AddWithValue("$causes", (long)request.Causes);
        insert.Parameters.AddWithValue("$epoch", lifecycleEpoch);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdatePendingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        ReconciliationCause causes,
        ReconciliationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE IndexJobs SET CauseMask = $causes, RequestedUtc = $now,
                TargetChunkProfileFingerprint = COALESCE($target, TargetChunkProfileFingerprint),
                ForceContentProcessing = CASE WHEN $force = 1 THEN 1 ELSE ForceContentProcessing END
            WHERE JobId = $id AND Status = 'Pending';
            """;
        update.Parameters.AddWithValue("$causes", (long)causes);
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$target", request.TargetChunkProfileFingerprint is null ? DBNull.Value : request.TargetChunkProfileFingerprint);
        update.Parameters.AddWithValue("$force", request.ForceContentProcessing ? 1 : 0);
        update.Parameters.AddWithValue("$id", jobId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRetryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobRow retry,
        ReconciliationCause causes,
        ReconciliationRequest request,
        DateTimeOffset now,
        bool expedite,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE IndexJobs SET CauseMask = $causes, RequestedUtc = $now,
                NextAttemptUtc = CASE WHEN $expedite = 1 THEN $now ELSE NextAttemptUtc END,
                TargetChunkProfileFingerprint = COALESCE($target, TargetChunkProfileFingerprint),
                ForceContentProcessing = CASE WHEN $force = 1 THEN 1 ELSE ForceContentProcessing END
            WHERE JobId = $id AND Status = 'RetryScheduled';
            """;
        update.Parameters.AddWithValue("$causes", (long)causes);
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$expedite", expedite ? 1 : 0);
        update.Parameters.AddWithValue(
            "$target",
            request.TargetChunkProfileFingerprint is null ? DBNull.Value : request.TargetChunkProfileFingerprint);
        update.Parameters.AddWithValue("$force", request.ForceContentProcessing ? 1 : 0);
        update.Parameters.AddWithValue("$id", retry.JobId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateQueuedStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceReconciliation state,
        long desiredGeneration,
        ReconciliationCause pendingCauses,
        DateTimeOffset now,
        bool keepRunning,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE SourceReconciliations SET DesiredGeneration = MAX(DesiredGeneration, $desired),
                State = $state, PendingCauseMask = $causes, RequestedUtc = $now
            WHERE SourceId = $source;
            """;
        update.Parameters.AddWithValue("$desired", desiredGeneration);
        update.Parameters.AddWithValue("$state", keepRunning ? "Running" : "Queued");
        update.Parameters.AddWithValue("$causes", (long)pendingCauses);
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$source", state.SourceId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReactivateFailedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobRow failed,
        ReconciliationRequest request,
        long lifecycleEpoch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE IndexJobs SET Status = 'Pending', RequestedUtc = $now, StartedUtc = NULL,
                CompletedUtc = NULL, Error = NULL, Attempt = 0, NextAttemptUtc = NULL,
                CauseMask = CauseMask | $causes,
                TargetChunkProfileFingerprint = COALESCE($target, TargetChunkProfileFingerprint),
                ForceContentProcessing = CASE WHEN $force = 1 THEN 1 ELSE ForceContentProcessing END,
                LifecycleEpoch = $epoch
            WHERE JobId = $id AND Status = 'Failed';
            """;
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$causes", (long)request.Causes);
        update.Parameters.AddWithValue("$target", request.TargetChunkProfileFingerprint is null ? DBNull.Value : request.TargetChunkProfileFingerprint);
        update.Parameters.AddWithValue("$force", request.ForceContentProcessing ? 1 : 0);
        update.Parameters.AddWithValue("$epoch", lifecycleEpoch);
        update.Parameters.AddWithValue("$id", failed.JobId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsLeaseCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationLease lease,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM IndexJobs j JOIN Sources s ON s.SourceId = j.SourceId
              WHERE j.JobId = $id AND j.SourceId = $source AND j.Generation = $generation
                AND j.JobKind = 'Reconciliation' AND j.Status = 'Processing'
                AND j.LeaseId = $lease AND j.LifecycleEpoch = $epoch
                AND j.LeaseExpiresUtc > $now
                AND s.LifecycleState = 'Active' AND s.LifecycleEpoch = $epoch);
            """;
        AddLeaseParameters(command, lease);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<JobRow?> ReadJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT JobId, Generation, Status, Attempt, CauseMask,
                   TargetChunkProfileFingerprint, ForceContentProcessing, LifecycleEpoch
            FROM IndexJobs WHERE SourceId = $source AND JobKind = 'Reconciliation' AND Status = $status
            ORDER BY Generation LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$status", status);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader, sourceId) : null;
    }

    private static async Task<JobRow?> ReadJobByGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        long generation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT JobId, Generation, Status, Attempt, CauseMask,
                   TargetChunkProfileFingerprint, ForceContentProcessing, LifecycleEpoch
            FROM IndexJobs WHERE SourceId = $source AND JobKind = 'Reconciliation' AND Generation = $generation
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$generation", generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader, sourceId) : null;
    }

    private static JobRow ReadJob(SqliteDataReader reader, string sourceId) => new(
        reader.GetString(0), sourceId, reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
        (ReconciliationCause)reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6) != 0, reader.GetInt64(7));

    private static async Task<ReconciliationCause> ReadPendingCauseMaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(CauseMask), 0) FROM IndexJobs
            WHERE SourceId = $source AND JobKind = 'Reconciliation' AND Status = 'Pending';
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        return (ReconciliationCause)Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<(SourceLifecycleState State, long Epoch)?> ReadLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT LifecycleState, LifecycleEpoch FROM Sources WHERE SourceId = $source;";
        command.Parameters.AddWithValue("$source", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (Enum.Parse<SourceLifecycleState>(reader.GetString(0)), reader.GetInt64(1))
            : null;
    }

    private static async Task<SourceReconciliation?> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StateSelect + " WHERE SourceId = $source;";
        command.Parameters.AddWithValue("$source", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    private static SourceReconciliation ReadState(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
        Enum.Parse<ReconciliationState>(reader.GetString(4)), (ReconciliationCause)reader.GetInt64(5),
        (ReconciliationCause)reader.GetInt64(6), ReadDate(reader, 7), ReadDate(reader, 8), ReadDate(reader, 9),
        ReadDate(reader, 10), reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : Enum.Parse<ReconciliationFailureCode>(reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16));

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));

    private static void AddLeaseParameters(SqliteCommand command, ReconciliationLease lease)
    {
        command.Parameters.AddWithValue("$id", lease.JobId);
        command.Parameters.AddWithValue("$source", lease.SourceId);
        command.Parameters.AddWithValue("$generation", lease.Generation);
        command.Parameters.AddWithValue("$lease", lease.LeaseId);
        command.Parameters.AddWithValue("$epoch", lease.LifecycleEpoch);
    }

    private const string StateSelect = """
        SELECT SourceId, DesiredGeneration, CompletedGeneration, ActiveGeneration, State,
               PendingCauseMask, ActiveCauseMask, RequestedUtc, StartedUtc,
               LastSucceededUtc, LastFailedUtc, LastOutcome, LastErrorCode, LastErrorSummary,
               LastChangedFiles, LastDeletedFiles, LastUnchangedFiles
        FROM SourceReconciliations
        """;

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record JobRow(
        string JobId,
        string SourceId,
        long Generation,
        string Status,
        int Attempt,
        ReconciliationCause Causes,
        string? TargetFingerprint,
        bool Force,
        long LifecycleEpoch);

    private sealed record LegacyJob(
        string JobId,
        string SourceId,
        string RequestedUtc,
        int Attempt,
        string? TargetFingerprint,
        bool Force);
}
