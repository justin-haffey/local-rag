using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ReconciliationRecoveryTests
{
    [Fact]
    public async Task SchedulerPersistsBoundedCauseBeforePublishingWakeup()
    {
        var store = new RecordingReconciliationStore();
        var wakeups = new IndexWorkChannel();
        var dispatchSignal = new ReconciliationDispatchSignal();
        var scheduler = new ReconciliationScheduler(store, wakeups, new OperationalMetrics(), dispatchSignal);

        await scheduler.RequestAsync(
            "source-1",
            ReconciliationCause.WatcherError | ReconciliationCause.WatcherOverflow,
            CancellationToken.None);

        Assert.NotNull(store.LastRequest);
        Assert.True(store.RequestCommitted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = wakeups.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("source-1", reader.Current);
        Assert.Equal(
            ReconciliationCause.WatcherError | ReconciliationCause.WatcherOverflow,
            store.LastRequest.Causes);
        await dispatchSignal.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task SourceGateCancellationFencesRemovalUntilActiveWorkExits()
    {
        var gate = new SourceOperationGate();
        var active = await gate.AcquireAsync("source-1", CancellationToken.None);
        var waiter = gate.AcquireAsync("source-1", CancellationToken.None);

        gate.CancelActive("source-1");

        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.False(waiter.IsCompleted);
        await active.DisposeAsync();
        await using var removal = await waiter;
        Assert.False(removal.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void TraversalFailsClosedInsteadOfTreatingInaccessibleFilesAsDeleted()
    {
        var options = IndexCoordinator.CreateEnumerationOptions();

        Assert.True(options.RecurseSubdirectories);
        Assert.False(options.IgnoreInaccessible);
        Assert.True(options.AttributesToSkip.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task WatcherOverflowDurablyRequestsRecovery()
    {
        var store = new RecordingReconciliationStore();
        var scheduler = new ReconciliationScheduler(
            store,
            new IndexWorkChannel(),
            new OperationalMetrics());
        using var watchers = new SourceWatcherRegistry(
            scheduler,
            Options.Create(new LocalRagOptions()),
            NullLogger<SourceWatcherRegistry>.Instance);

        await watchers.NotifyErrorAsync(
            "source-1",
            new InternalBufferOverflowException(),
            CancellationToken.None);

        Assert.NotNull(store.LastRequest);
        Assert.Equal(
            ReconciliationCause.WatcherError | ReconciliationCause.WatcherOverflow,
            store.LastRequest.Causes);
    }

    [Fact]
    public async Task WatcherFailureLogDoesNotIncludeExceptionPathsOrSecrets()
    {
        var logger = new RecordingLogger<SourceWatcherRegistry>();
        var scheduler = new ReconciliationScheduler(
            new RecordingReconciliationStore(),
            new IndexWorkChannel(),
            new OperationalMetrics());
        using var watchers = new SourceWatcherRegistry(
            scheduler,
            Options.Create(new LocalRagOptions()),
            logger);

        await watchers.NotifyErrorAsync(
            "source-1",
            new IOException(@"C:\private\customer.txt secret-value"),
            CancellationToken.None);

        var output = string.Join('\n', logger.Messages);
        Assert.Contains("source-1", output, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestCommittedHashMatchReturnsUnchangedWithoutEmbeddingOrUpsert()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "unchanged.txt");
            const string content = "manifest committed content";
            await File.WriteAllTextAsync(path, content);
            var info = new FileInfo(path);
            var source = new SourceRecord(
                "source-1",
                root,
                "fixture",
                SourceStatus.Ready,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                "test-profile");
            var existing = new IndexedFile(
                "file-1",
                source.SourceId,
                "unchanged.txt",
                Hash(content),
                info.Length + 1,
                new DateTimeOffset(info.LastWriteTimeUtc).AddSeconds(-1));
            var state = new ExistingFileState(existing);
            var embeddings = new CountingEmbeddings();
            var vectors = new CountingVectorStore();
            var options = Options.Create(new LocalRagOptions
            {
                Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
                Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
            });
            var service = new FileIndexingService(
                state,
                embeddings,
                new NeverChunker(),
                vectors,
                new ContentExtractionService([new PlainTextContentExtractor()]),
                options,
                new OperationalMetrics());

            var outcome = await service.IndexAsync(
                source,
                path,
                "unchanged.txt",
                info,
                CancellationToken.None);

            Assert.False(outcome.Changed);
            Assert.Equal(0, outcome.EmbeddingCount);
            Assert.Equal(0, outcome.UpsertCount);
            Assert.Equal(0, embeddings.PassageCalls);
            Assert.Equal(0, vectors.UpsertCalls);
            Assert.Equal(1, state.SaveCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetryAfterVectorUpsertBeforeManifestCommitConvergesWithDeterministicIds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-vector-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "recovery.txt");
            await File.WriteAllTextAsync(path, "recovery survives the vector to manifest crash window");
            var source = new SourceRecord(
                "source-1",
                root,
                "fixture",
                SourceStatus.Indexing,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                "test-profile");
            var state = new FailFirstSaveIndexState();
            var vectors = new IdempotentVectorStore();
            var options = Options.Create(new LocalRagOptions
            {
                Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
                Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
            });
            var service = new FileIndexingService(
                state,
                new CountingEmbeddings(),
                new SingleChunker(),
                vectors,
                new ContentExtractionService([new PlainTextContentExtractor()]),
                options,
                new OperationalMetrics());

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.IndexAsync(
                source,
                path,
                "recovery.txt",
                new FileInfo(path),
                CancellationToken.None));
            Assert.Null(state.File);
            var firstIds = Assert.Single(vectors.UpsertBatches);

            var recovered = await service.IndexAsync(
                source,
                path,
                "recovery.txt",
                new FileInfo(path),
                CancellationToken.None);

            Assert.True(recovered.Changed);
            Assert.NotNull(state.File);
            Assert.Equal(2, vectors.UpsertBatches.Count);
            Assert.Equal(firstIds, vectors.UpsertBatches[1]);
            Assert.Single(vectors.Documents);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemovalDuringVectorMutationCancelsWorkAndCannotResurrectSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-remove-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "race.txt"), "removal fences vector mutation");
            var options = Options.Create(new LocalRagOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
                Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
            });
            var database = new SqliteDatabase(options);
            var sources = new SqliteSourceRegistry(database, options);
            var indexState = new SqliteIndexStateStore(database, options);
            var profiles = new SqliteChunkProfileStateStore(database, new ChunkProfileOperationGate());
            var store = new SqliteReconciliationStore(database);
            await sources.InitializeAsync(CancellationToken.None);
            await indexState.InitializeAsync(CancellationToken.None);
            await profiles.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            var source = await sources.RegisterAsync(sourceRoot, "race", CancellationToken.None);
            var metrics = new OperationalMetrics();
            var scheduler = new ReconciliationScheduler(store, new IndexWorkChannel(), metrics);
            using var watchers = new SourceWatcherRegistry(scheduler, options, NullLogger<SourceWatcherRegistry>.Instance);
            var vectors = new BlockingVectorStore();
            var gate = new SourceOperationGate();
            var profile = new ChunkProfileProvider(options);
            var extraction = new ContentExtractionService([new PlainTextContentExtractor()]);
            var coordinator = new IndexCoordinator(
                sources,
                indexState,
                vectors,
                new FileIndexingService(
                    indexState,
                    new CountingEmbeddings(),
                    new SingleChunker(),
                    vectors,
                    extraction,
                    options,
                    metrics,
                    store),
                new FilePolicy(options, extraction),
                watchers,
                scheduler,
                store,
                gate,
                profile,
                profiles,
                NullLogger<IndexCoordinator>.Instance);
            await coordinator.QueueInitialIndexAsync(source.SourceId, CancellationToken.None);
            var lease = Assert.IsType<ReconciliationLease>(await store.TryLeaseAsync(
                source.SourceId,
                TimeSpan.FromMinutes(2),
                CancellationToken.None));

            var processing = Task.Run(async () =>
            {
                await using var operation = await gate.AcquireAsync(source.SourceId, CancellationToken.None);
                return await coordinator.ProcessAsync(lease, operation.CancellationToken);
            });
            await vectors.UpsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removal = coordinator.RemoveSourceAsync(source.SourceId, CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
            await removal;

            Assert.True(vectors.SourceDeleted);
            Assert.Null(await sources.GetAsync(source.SourceId, CancellationToken.None));
            Assert.False((await store.CompleteAsync(
                lease,
                new ReconciliationResult(1, 0, 0),
                CancellationToken.None)).Applied);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LockedFileFailureIsClassifiedIntoADurableRetry()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var path = Path.Combine(fixture.Source.CanonicalRootPath, "locked.txt");
        await File.WriteAllTextAsync(path, "locked recovery content");
        var options = Options.Create(new LocalRagOptions
        {
            Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
            Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
        });
        var service = new FileIndexingService(
            new FailFirstSaveIndexState(),
            new CountingEmbeddings(),
            new SingleChunker(),
            new CountingVectorStore(),
            new ContentExtractionService([new PlainTextContentExtractor()]),
            options,
            new OperationalMetrics());
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.FileHint),
            CancellationToken.None);
        var lease = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));

        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.IndexAsync(
                fixture.Source,
                path,
                "locked.txt",
                new FileInfo(path),
                CancellationToken.None));
        }
        var retry = await fixture.Store.FailAsync(
            lease,
            new ReconciliationFailure(ReconciliationFailureCode.FileUnstable),
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(retry.Applied);
        Assert.False(retry.IsTerminal);
        var replacement = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.True(replacement.Causes.HasFlag(ReconciliationCause.Retry));
    }

    [Fact]
    public async Task LargeSourceScanConvergesAndSecondPassPerformsNoEmbeddingsOrUpserts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-large-scan-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            foreach (var index in Enumerable.Range(0, 64))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(sourceRoot, $"file-{index:D3}.txt"),
                    $"bounded large source recovery fixture {index}");
            }
            var options = Options.Create(new LocalRagOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
                Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
            });
            var database = new SqliteDatabase(options);
            var sources = new SqliteSourceRegistry(database, options);
            var indexState = new SqliteIndexStateStore(database, options);
            var profiles = new SqliteChunkProfileStateStore(database, new ChunkProfileOperationGate());
            var store = new SqliteReconciliationStore(database);
            await sources.InitializeAsync(CancellationToken.None);
            await indexState.InitializeAsync(CancellationToken.None);
            await profiles.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            var source = await sources.RegisterAsync(sourceRoot, "large", CancellationToken.None);
            var metrics = new OperationalMetrics();
            var scheduler = new ReconciliationScheduler(store, new IndexWorkChannel(), metrics);
            using var watchers = new SourceWatcherRegistry(scheduler, options, NullLogger<SourceWatcherRegistry>.Instance);
            var vectors = new IdempotentVectorStore();
            var embeddings = new CountingEmbeddings();
            var extraction = new ContentExtractionService([new PlainTextContentExtractor()]);
            var profile = new ChunkProfileProvider(options);
            var coordinator = new IndexCoordinator(
                sources,
                indexState,
                vectors,
                new FileIndexingService(
                    indexState,
                    embeddings,
                    new SingleChunker(),
                    vectors,
                    extraction,
                    options,
                    metrics,
                    store),
                new FilePolicy(options, extraction),
                watchers,
                scheduler,
                store,
                new SourceOperationGate(),
                profile,
                profiles,
                NullLogger<IndexCoordinator>.Instance);

            await coordinator.QueueInitialIndexAsync(source.SourceId, CancellationToken.None);
            var initialLease = Assert.IsType<ReconciliationLease>(await store.TryLeaseAsync(
                source.SourceId,
                TimeSpan.FromMinutes(2),
                CancellationToken.None));
            var initial = await coordinator.ProcessAsync(initialLease, CancellationToken.None);
            Assert.Equal(64, initial.Result.ChangedFiles);
            Assert.Equal(64, initial.EmbeddingCount);
            Assert.Equal(64, initial.UpsertCount);
            Assert.True((await store.CompleteAsync(initialLease, initial.Result, CancellationToken.None)).Applied);

            await coordinator.ReindexAsync(source.SourceId, CancellationToken.None);
            var unchangedLease = Assert.IsType<ReconciliationLease>(await store.TryLeaseAsync(
                source.SourceId,
                TimeSpan.FromMinutes(2),
                CancellationToken.None));
            embeddings.Reset();
            var unchanged = await coordinator.ProcessAsync(unchangedLease, CancellationToken.None);
            Assert.Equal(0, unchanged.Result.ChangedFiles);
            Assert.Equal(64, unchanged.Result.UnchangedFiles);
            Assert.Equal(0, unchanged.EmbeddingCount);
            Assert.Equal(0, unchanged.UpsertCount);
            Assert.Equal(0, embeddings.PassageCalls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedOverflowDuringActiveGenerationCreatesExactlyOneSuccessor()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Initial),
            CancellationToken.None);
        var active = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));

        for (var index = 0; index < 5; index++)
        {
            await fixture.Store.RequestAsync(
                new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.WatcherOverflow),
                CancellationToken.None);
        }

        var state = Assert.IsType<SourceReconciliation>(await fixture.Store.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(active.Generation + 1, state.DesiredGeneration);
        Assert.Equal(active.Generation, state.ActiveGeneration);
        var completed = await fixture.Store.CompleteAsync(
            active,
            new ReconciliationResult(0, 0, 1),
            CancellationToken.None);
        Assert.True(completed.Applied);
        Assert.True(completed.HasSuccessor);
        var successor = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.Equal(active.Generation + 1, successor.Generation);
        Assert.True(successor.Causes.HasFlag(ReconciliationCause.WatcherOverflow));
    }

    [Fact]
    public async Task ExpiredLeaseRecoveryRejectsStaleCompletion()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Startup),
            CancellationToken.None);
        var stale = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None));
        await Task.Delay(20);
        Assert.False(await fixture.Store.RenewLeaseAsync(
            stale,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.False((await fixture.Store.CompleteAsync(
            stale,
            new ReconciliationResult(1, 0, 0),
            CancellationToken.None)).Applied);
        var recovered = await fixture.Store.RecoverExpiredLeasesAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Contains(fixture.Source.SourceId, recovered);
        var replacement = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        Assert.NotEqual(stale.LeaseId, replacement.LeaseId);

        Assert.False((await fixture.Store.CompleteAsync(
            stale,
            new ReconciliationResult(1, 0, 0),
            CancellationToken.None)).Applied);
        Assert.True((await fixture.Store.CompleteAsync(
            replacement,
            new ReconciliationResult(1, 0, 0),
            CancellationToken.None)).Applied);
    }

    [Fact]
    public async Task DependencyFailureRemainsDirtyUntilDurableRetryIsDue()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Periodic),
            CancellationToken.None);
        var lease = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
        var failure = await fixture.Store.FailAsync(
            lease,
            new ReconciliationFailure(ReconciliationFailureCode.DependencyUnavailable),
            maxAttempts: 3,
            retryDelay: TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.True(failure.Applied);
        Assert.False(failure.IsTerminal);
        Assert.NotNull(failure.NextAttemptUtc);
        var state = Assert.IsType<SourceReconciliation>(await fixture.Store.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.True(state.DesiredGeneration > state.CompletedGeneration);
        Assert.Equal(ReconciliationState.Queued, state.State);
        Assert.DoesNotContain(fixture.Source.SourceId, await fixture.Store.GetDueSourceIdsAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.Contains(fixture.Source.SourceId, await fixture.Store.GetDueSourceIdsAsync(
            failure.NextAttemptUtc!.Value.AddSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task TombstoneInvalidatesActiveLeaseBeforeRemovalCleanup()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        await fixture.Store.RequestAsync(
            new ReconciliationRequest(fixture.Source.SourceId, ReconciliationCause.Manual),
            CancellationToken.None);
        var lease = Assert.IsType<ReconciliationLease>(await fixture.Store.TryLeaseAsync(
            fixture.Source.SourceId,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));

        var tombstone = Assert.IsType<SourceLifecycle>(await fixture.Store.TombstoneAsync(
            fixture.Source.SourceId,
            CancellationToken.None));

        Assert.Equal(SourceLifecycleState.Removing, tombstone.State);
        Assert.False(await fixture.Store.IsLifecycleCurrentAsync(
            fixture.Source.SourceId,
            lease.LifecycleEpoch,
            CancellationToken.None));
        Assert.False((await fixture.Store.CompleteAsync(
            lease,
            new ReconciliationResult(1, 1, 0),
            CancellationToken.None)).Applied);
        var source = Assert.IsType<SourceRecord>(await fixture.Sources.GetAsync(
            fixture.Source.SourceId,
            CancellationToken.None));
        Assert.Equal(SourceLifecycleState.Removing, source.LifecycleState);
        Assert.Equal(tombstone.Epoch, source.LifecycleEpoch);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingReconciliationStore : IReconciliationStore
    {
        public ReconciliationRequest? LastRequest { get; private set; }
        public bool RequestCommitted { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ReconciliationRequestResult> RequestAsync(
            ReconciliationRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCommitted = true;
            return Task.FromResult(new ReconciliationRequestResult(1, true, true, ReconciliationState.Queued));
        }

        public Task<ReconciliationLease?> TryLeaseAsync(string sourceId, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult<ReconciliationLease?>(null);
        public Task<bool> RenewLeaseAsync(ReconciliationLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ReconciliationCompletionResult> CompleteAsync(ReconciliationLease lease, ReconciliationResult result, CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationCompletionResult(false, false, false));
        public Task<ReconciliationFailureResult> FailAsync(ReconciliationLease lease, ReconciliationFailure failure, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationFailureResult(false, false, false, null));
        public Task<bool> ReleaseAsync(ReconciliationLease lease, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> GetDueSourceIdsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<DateTimeOffset?> GetNextDueUtcAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
        public Task<IReadOnlyList<string>> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<SourceReconciliation?> GetAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult<SourceReconciliation?>(null);
        public Task<IReadOnlyList<SourceReconciliation>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceReconciliation>>([]);
        public Task<SourceLifecycle?> TombstoneAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult<SourceLifecycle?>(null);
        public Task<bool> IsLifecycleCurrentAsync(string sourceId, long expectedEpoch, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task PruneCompletedAsync(int historyLimit, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class InMemorySourceRegistry(SourceRecord source) : ISourceRegistry
    {
        public SourceRecord Source { get; private set; } = source;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SourceRecord> RegisterAsync(string rootPath, string? displayName, CancellationToken cancellationToken) => Task.FromResult(Source);
        public Task<IReadOnlyList<SourceRecord>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceRecord>>([Source]);
        public Task<SourceRecord?> GetAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult<SourceRecord?>(sourceId == Source.SourceId ? Source : null);
        public Task RemoveAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetStatusAsync(string sourceId, SourceStatus status, string? failureMessage, CancellationToken cancellationToken)
        {
            Source = Source with { Status = status, LastError = failureMessage };
            return Task.CompletedTask;
        }
    }

    private sealed class ExistingFileState(IndexedFile file) : IIndexStateStore
    {
        public int SaveCalls { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.FromResult<IndexedFile?>(file);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ChunkRecord>>([]);
        public Task SaveFileAndChunksAsync(IndexedFile indexedFile, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }
        public Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) => Task.FromResult<ChunkRecord?>(null);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ChunkRecord>>([]);
    }

    private sealed class CountingEmbeddings : IEmbeddingService
    {
        public string ProfileId => "test-profile";
        public int PassageCalls { get; private set; }
        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([1]);
        public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken)
        {
            PassageCalls++;
            return Task.FromResult<IReadOnlyList<float>>([1]);
        }
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Reset() => PassageCalls = 0;
    }

    private sealed class NeverChunker : IChunker
    {
        public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent) =>
            throw new InvalidOperationException("Hash-identical content must not be chunked.");
    }

    private sealed class CountingVectorStore : IVectorStore
    {
        public int UpsertCalls { get; private set; }
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
        {
            UpsertCalls++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }

    private sealed class FailFirstSaveIndexState : IIndexStateStore
    {
        private bool _fail = true;
        private IReadOnlyList<ChunkRecord> _chunks = [];

        public IndexedFile? File { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(File);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult(_chunks);
        public Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("Injected manifest commit failure.");
            }
            File = file;
            _chunks = chunks;
            return Task.CompletedTask;
        }
        public Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) =>
            Task.FromResult(_chunks.SingleOrDefault(chunk => chunk.ChunkId == chunkId));
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(_chunks);
    }

    private sealed class SingleChunker : IChunker
    {
        public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent) =>
        [
            new ChunkRecord(
                $"{file.FileId}-chunk",
                source.SourceId,
                file.FileId,
                file.RelativePath,
                "text",
                null,
                1,
                1,
                0,
                normalizedContent,
                file.ContentHash,
                8,
                source.EmbeddingProfileId,
                DateTimeOffset.UtcNow,
                "text",
                null,
                "lines:1-1",
                "generic",
                "1",
                "test-profile")
        ];
    }

    private sealed class IdempotentVectorStore : IVectorStore
    {
        public Dictionary<string, VectorDocument> Documents { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> UpsertBatches { get; } = [];
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
        {
            UpsertBatches.Add(documents.Select(document => document.Chunk.ChunkId).ToArray());
            foreach (var document in documents) Documents[document.Chunk.ChunkId] = document;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
        {
            foreach (var chunkId in chunkIds) Documents.Remove(chunkId);
            return Task.CompletedTask;
        }
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken)
        {
            foreach (var chunkId in Documents.Where(pair => pair.Value.Chunk.SourceId == sourceId).Select(pair => pair.Key).ToArray())
            {
                Documents.Remove(chunkId);
            }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }

    private sealed class BlockingVectorStore : IVectorStore
    {
        public TaskCompletionSource UpsertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SourceDeleted { get; private set; }
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
        {
            UpsertStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken)
        {
            SourceDeleted = true;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private SqliteFixture(
            string root,
            SqliteSourceRegistry sources,
            SqliteReconciliationStore store,
            SourceRecord source)
        {
            Root = root;
            Sources = sources;
            Store = store;
            Source = source;
        }

        public string Root { get; }
        public SqliteSourceRegistry Sources { get; }
        public SqliteReconciliationStore Store { get; }
        public SourceRecord Source { get; }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"local-rag-reconciliation-{Guid.NewGuid():N}");
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
            await new IndexJobStore(database).InitializeAsync(CancellationToken.None);
            var store = new SqliteReconciliationStore(database);
            await store.InitializeAsync(CancellationToken.None);
            return new SqliteFixture(root, sources, store, source);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
