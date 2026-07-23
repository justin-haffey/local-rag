using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class SqliteReconciliationStoreTests
{
    [Fact]
    public async Task BusyWriterReplayIsBoundedAndNeverSpinsIndefinitely()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var blockerConnection = await fixture.Database.OpenAsync(CancellationToken.None);
        await using var contenderConnection = await fixture.Database.OpenAsync(CancellationToken.None);
        contenderConnection.DefaultTimeout = 1;
        await using (var timeout = contenderConnection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 1;";
            await timeout.ExecuteNonQueryAsync();
        }
        await using var blocker = blockerConnection.BeginTransaction(deferred: false);

        var elapsed = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Database.BeginImmediateTransactionAsync(contenderConnection, CancellationToken.None));
        elapsed.Stop();

        Assert.True(
            exception.SqliteErrorCode is 5 or 6 || exception.SqliteExtendedErrorCode is 262 or 517,
            $"Unexpected SQLite contention code {exception.SqliteErrorCode}/{exception.SqliteExtendedErrorCode}.");
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestInitializesStateForSourceRegisteredAfterStoreStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-reconciliation-late-source-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var options = Options.Create(new LocalRagOptions { DataDirectory = Path.Combine(root, "data") });
            var database = new SqliteDatabase(options);
            var sources = new SqliteSourceRegistry(database, options);
            await sources.InitializeAsync(CancellationToken.None);
            var store = new SqliteReconciliationStore(database);
            await store.InitializeAsync(CancellationToken.None);
            var source = await sources.RegisterAsync(sourceRoot, "late source", CancellationToken.None);

            var request = await store.RequestAsync(
                new ReconciliationRequest(source.SourceId, ReconciliationCause.Initial),
                CancellationToken.None);

            Assert.Equal(1, request.Generation);
            Assert.NotNull(await store.GetAsync(source.SourceId, CancellationToken.None));
            Assert.Equal(
                SourceStatus.Indexing,
                (await sources.GetAsync(source.SourceId, CancellationToken.None))?.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WatcherRequestAtomicallyProjectsBoundedDegradedStatus()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Store.RequestAsync(
            new ReconciliationRequest(
                fixture.Source.SourceId,
                ReconciliationCause.WatcherError | ReconciliationCause.WatcherOverflow),
            CancellationToken.None);

        var source = Assert.IsType<SourceRecord>(await fixture.Sources.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(SourceStatus.Degraded, source.Status);
        Assert.Equal("File watcher overflowed or failed; recovery has been queued.", source.LastError);
        Assert.DoesNotContain(fixture.Source.CanonicalRootPath, source.LastError, StringComparison.OrdinalIgnoreCase);
        var state = Assert.IsType<SourceReconciliation>(await fixture.Store.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(ReconciliationState.Queued, state.State);
    }

    [Fact]
    public async Task ConcurrentSaveBurstCoalescesIntoOneGenerationAndOneLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        var causes = new[]
        {
            ReconciliationCause.Initial,
            ReconciliationCause.FileHint,
            ReconciliationCause.WatcherOverflow,
            ReconciliationCause.WatcherError,
            ReconciliationCause.Startup,
            ReconciliationCause.Periodic,
            ReconciliationCause.Manual
        };

        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, causes[index % causes.Length]),
            CancellationToken.None)));

        var state = Assert.IsType<SourceReconciliation>(await fixture.Store.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(1, state.DesiredGeneration);
        Assert.Equal(causes.Aggregate(ReconciliationCause.None, (all, cause) => all | cause), state.PendingCauses);

        var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None)));
        var lease = Assert.Single(leases, candidate => candidate is not null);
        Assert.Equal(state.PendingCauses, lease!.Causes);
    }

    [Fact]
    public async Task RetryAndRepeatedRequestsPreserveOneSuccessorAndAllCauses()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Initial),
            CancellationToken.None);
        var active = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.WatcherOverflow),
            CancellationToken.None);
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Manual),
            CancellationToken.None);

        var failed = await fixture.Store.FailAsync(
            active,
            new ReconciliationFailure(ReconciliationFailureCode.DependencyUnavailable),
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(failed.Applied);
        Assert.False(failed.IsTerminal);
        Assert.True(failed.HasSuccessor);
        var state = Assert.IsType<SourceReconciliation>(await fixture.Store.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(active.Generation + 1, state.DesiredGeneration);
        Assert.True(state.PendingCauses.HasFlag(ReconciliationCause.Initial));
        Assert.True(state.PendingCauses.HasFlag(ReconciliationCause.Retry));
        Assert.True(state.PendingCauses.HasFlag(ReconciliationCause.WatcherOverflow));
        Assert.True(state.PendingCauses.HasFlag(ReconciliationCause.Manual));

        var retry = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(active.Generation, retry.Generation);
        Assert.True(retry.Causes.HasFlag(ReconciliationCause.Retry));
        Assert.True((await fixture.Store.CompleteAsync(
            retry,
            new ReconciliationResult(0, 0, 1),
            CancellationToken.None)).HasSuccessor);
        var successor = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(active.Generation + 1, successor.Generation);
        Assert.True(successor.Causes.HasFlag(ReconciliationCause.WatcherOverflow));
        Assert.True(successor.Causes.HasFlag(ReconciliationCause.Manual));
    }

    [Fact]
    public async Task DelayedRetryBlocksItsSuccessorAndDispatcherUsesTheRetryDueTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Initial),
            CancellationToken.None);
        var active = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.WatcherOverflow),
            CancellationToken.None);
        Assert.DoesNotContain(fixture.Source.SourceId, await fixture.Store.GetDueSourceIdsAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        var leaseExpiry = Assert.IsType<DateTimeOffset>(await fixture.Store.GetNextDueUtcAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.InRange(leaseExpiry, active.LeaseExpiresUtc.AddSeconds(-1), active.LeaseExpiresUtc.AddSeconds(1));
        var failure = await fixture.Store.FailAsync(
            active,
            new ReconciliationFailure(ReconciliationFailureCode.DependencyUnavailable),
            maxAttempts: 3,
            retryDelay: TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.True(failure.HasSuccessor);
        Assert.Null(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.DoesNotContain(fixture.Source.SourceId, await fixture.Store.GetDueSourceIdsAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        var nextDue = Assert.IsType<DateTimeOffset>(await fixture.Store.GetNextDueUtcAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.InRange(nextDue, failure.NextAttemptUtc!.Value.AddSeconds(-1), failure.NextAttemptUtc.Value.AddSeconds(1));

        await using (var connection = await fixture.Database.OpenAsync(CancellationToken.None))
        await using (var makeDue = connection.CreateCommand())
        {
            makeDue.CommandText = """
                UPDATE IndexJobs SET NextAttemptUtc = $past
                WHERE SourceId = $source AND JobKind = 'Reconciliation' AND Status = 'RetryScheduled';
                """;
            makeDue.Parameters.AddWithValue("$past", DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));
            makeDue.Parameters.AddWithValue("$source", fixture.Source.SourceId);
            await makeDue.ExecuteNonQueryAsync();
        }

        var retry = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(active.Generation, retry.Generation);
        Assert.True((await fixture.Store.CompleteAsync(
            retry,
            new ReconciliationResult(0, 0, 1),
            CancellationToken.None)).HasSuccessor);
        var successor = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(active.Generation + 1, successor.Generation);
    }

    [Fact]
    public async Task QueuedRetryMergesHintsAndManualRequestExpeditesTheSameGeneration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Periodic),
            CancellationToken.None);
        var active = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        var failure = await fixture.Store.FailAsync(
            active,
            new ReconciliationFailure(ReconciliationFailureCode.DependencyUnavailable),
            maxAttempts: 3,
            retryDelay: TimeSpan.FromMinutes(5),
            CancellationToken.None);

        var hint = await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.FileHint),
            CancellationToken.None);
        Assert.Equal(active.Generation, hint.Generation);
        Assert.False(hint.CreatedGeneration);
        Assert.Null(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(
            failure.NextAttemptUtc,
            await fixture.Store.GetNextDueUtcAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        var manual = await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Manual),
            CancellationToken.None);
        Assert.Equal(active.Generation, manual.Generation);
        Assert.False(manual.CreatedGeneration);
        Assert.True(manual.WakeRequired);
        var retry = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(active.Generation, retry.Generation);
        Assert.True(retry.Causes.HasFlag(ReconciliationCause.FileHint));
        Assert.True(retry.Causes.HasFlag(ReconciliationCause.Manual));
        Assert.True(retry.Causes.HasFlag(ReconciliationCause.Retry));
    }

    [Fact]
    public async Task ForcedFailureSuccessorRetainsProfilePayloadAndResolvedFailureIsPrunable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(
                fixture.Source.SourceId,
                ReconciliationCause.Manual,
                "profile-next",
                ForceContentProcessing: true),
            CancellationToken.None);
        var forced = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.WatcherOverflow),
            CancellationToken.None);
        Assert.True((await fixture.Store.FailAsync(
            forced,
            new ReconciliationFailure(ReconciliationFailureCode.Unexpected),
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None)).IsTerminal);

        var successor = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(forced.Generation + 1, successor.Generation);
        Assert.Equal("profile-next", successor.TargetChunkProfileFingerprint);
        Assert.True(successor.ForceContentProcessing);
        Assert.True((await fixture.Store.CompleteAsync(
            successor,
            new ReconciliationResult(1, 0, 0),
            CancellationToken.None)).Applied);

        await using (var seedConnection = await fixture.Database.OpenAsync(CancellationToken.None))
        await using (var legacy = seedConnection.CreateCommand())
        {
            legacy.CommandText = """
                INSERT INTO DeadLetterJobs(JobId, SourceId, Error, FailedUtc)
                VALUES('legacy-orphan', 'legacy-source', 'legacy failure', $now);
                """;
            legacy.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await legacy.ExecuteNonQueryAsync();
        }
        await fixture.Store.PruneCompletedAsync(1, CancellationToken.None);
        await using var connection = await fixture.Database.OpenAsync(CancellationToken.None);
        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT
              SUM(CASE WHEN j.Status = 'Failed' THEN 1 ELSE 0 END),
              (SELECT COUNT(*) FROM DeadLetterJobs d WHERE d.SourceId = $source),
              (SELECT COUNT(*) FROM DeadLetterJobs d WHERE d.JobId = 'legacy-orphan')
            FROM IndexJobs j
            WHERE j.SourceId = $source AND j.JobKind = 'Reconciliation';
            """;
        count.Parameters.AddWithValue("$source", fixture.Source.SourceId);
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task OrdinaryHintCannotOverwriteANewerForcedSuccessorProfile()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(
                fixture.Source.SourceId,
                ReconciliationCause.Manual,
                "profile-a",
                ForceContentProcessing: true),
            CancellationToken.None);
        var active = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(
                fixture.Source.SourceId,
                ReconciliationCause.Manual,
                "profile-b",
                ForceContentProcessing: true),
            CancellationToken.None);
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.FileHint),
            CancellationToken.None);

        Assert.True((await fixture.Store.CompleteAsync(
            active,
            new ReconciliationResult(1, 0, 0),
            CancellationToken.None)).HasSuccessor);
        var successor = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal("profile-b", successor.TargetChunkProfileFingerprint);
        Assert.True(successor.ForceContentProcessing);
        Assert.True(successor.Causes.HasFlag(ReconciliationCause.FileHint));
    }

    [Fact]
    public async Task PausedSourceRetainsDirtyWorkUntilResumed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Sources.SetStatusAsync(
            fixture.Source.SourceId,
            SourceStatus.Paused,
            null,
            CancellationToken.None);
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Manual),
            CancellationToken.None);

        Assert.Null(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.DoesNotContain(fixture.Source.SourceId, await fixture.Store.GetDueSourceIdsAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        await fixture.Sources.SetStatusAsync(
            fixture.Source.SourceId,
            SourceStatus.Ready,
            null,
            CancellationToken.None);
        Assert.NotNull(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
    }

    [Fact]
    public async Task TransientDependencyFailureExhaustsIntoRetainedSafeDeadLetter()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Periodic),
            CancellationToken.None);
        var first = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        var retry = await fixture.Store.FailAsync(
            first,
            new ReconciliationFailure(ReconciliationFailureCode.DependencyUnavailable),
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);
        Assert.False(retry.IsTerminal);

        var second = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        var terminal = await fixture.Store.FailAsync(
            second,
            new ReconciliationFailure(ReconciliationFailureCode.DependencyUnavailable),
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(terminal.IsTerminal);
        var state = Assert.IsType<SourceReconciliation>(await fixture.Store.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(ReconciliationState.Degraded, state.State);
        Assert.Equal(ReconciliationFailureCode.DependencyUnavailable, state.LastErrorCode);
        Assert.Equal("A required indexing dependency is unavailable.", state.LastErrorSummary);
        Assert.Null(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));

        await fixture.Store.PruneCompletedAsync(1, CancellationToken.None);
        await using var connection = await fixture.Database.OpenAsync(CancellationToken.None);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM DeadLetterJobs WHERE SourceId = $source;";
        count.Parameters.AddWithValue("$source", fixture.Source.SourceId);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task TemporaryMissingRootFailureKeepsSourceAndCanRecoverSameGeneration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Startup),
            CancellationToken.None);
        var missing = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.True((await fixture.Store.FailAsync(
            missing,
            new ReconciliationFailure(ReconciliationFailureCode.SourceMissing),
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None)).IsTerminal);
        Assert.NotNull(await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None));

        var requeued = await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Periodic),
            CancellationToken.None);
        Assert.Equal(missing.Generation, requeued.Generation);
        var recovered = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(missing.Generation, recovered.Generation);
        Assert.True((await fixture.Store.CompleteAsync(
            recovered,
            new ReconciliationResult(0, 0, 1),
            CancellationToken.None)).IsClean);
        Assert.NotNull(await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None));
    }

    [Fact]
    public async Task InitializationMigratesLegacyForcedWorkWithoutLosingLatestPayload()
    {
        await using var fixture = await Fixture.CreateAsync(initializeStore: false);
        var legacy = new IndexJobStore(fixture.Database);
        await legacy.InitializeAsync(CancellationToken.None);
        await legacy.QueueAsync(
            fixture.Source.SourceId,
            "profile-next",
            forceContentProcessing: true,
            CancellationToken.None);
        _ = Assert.IsType<IndexJob>(await legacy.LeaseAsync(fixture.Source.SourceId, CancellationToken.None));

        await fixture.Store.InitializeAsync(CancellationToken.None);
        var migrated = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));

        Assert.Equal("profile-next", migrated.TargetChunkProfileFingerprint);
        Assert.True(migrated.ForceContentProcessing);
        Assert.True(migrated.Causes.HasFlag(ReconciliationCause.Startup));
        Assert.True(migrated.Causes.HasFlag(ReconciliationCause.Retry));
    }

    [Fact]
    public async Task RetentionPrunesResolvedHistoryButKeepsTerminalFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var generation = 0; generation < 4; generation++)
        {
            await fixture.Store.RequestAsync(
                new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Manual),
                CancellationToken.None);
            var lease = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
                fixture.Source.SourceId,
                TimeSpan.FromMinutes(2),
                CancellationToken.None));
            Assert.True((await fixture.Store.CompleteAsync(
                lease,
                new ReconciliationResult(1, 0, 0),
                CancellationToken.None)).Applied);
        }

        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Manual),
            CancellationToken.None);
        var failedLease = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.True((await fixture.Store.FailAsync(
            failedLease,
            new ReconciliationFailure(ReconciliationFailureCode.StateCorrupt),
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None)).IsTerminal);

        await fixture.Store.PruneCompletedAsync(2, CancellationToken.None);
        await using var connection = await fixture.Database.OpenAsync(CancellationToken.None);
        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT
              SUM(CASE WHEN Status = 'Completed' THEN 1 ELSE 0 END),
              SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END)
            FROM IndexJobs
            WHERE SourceId = $source AND JobKind = 'Reconciliation';
            """;
        count.Parameters.AddWithValue("$source", fixture.Source.SourceId);
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            SqliteDatabase database,
            SqliteSourceRegistry sources,
            SqliteReconciliationStore store,
            SourceRecord source)
        {
            Root = root;
            Database = database;
            Sources = sources;
            Store = store;
            Source = source;
        }

        public string Root { get; }
        public SqliteDatabase Database { get; }
        public SqliteSourceRegistry Sources { get; }
        public SqliteReconciliationStore Store { get; }
        public SourceRecord Source { get; }

        public static async Task<Fixture> CreateAsync(bool initializeStore = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"local-rag-reconciliation-store-{Guid.NewGuid():N}");
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
            var options = Options.Create(new LocalRagOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
            });
            var database = new SqliteDatabase(options);
            var sources = new SqliteSourceRegistry(database, options);
            await sources.InitializeAsync(CancellationToken.None);
            var source = await sources.RegisterAsync(sourceRoot, "fixture", CancellationToken.None);
            await new SqliteIndexStateStore(database, options).InitializeAsync(CancellationToken.None);
            var store = new SqliteReconciliationStore(database);
            if (initializeStore)
            {
                await store.InitializeAsync(CancellationToken.None);
            }
            return new Fixture(root, database, sources, store, source);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
