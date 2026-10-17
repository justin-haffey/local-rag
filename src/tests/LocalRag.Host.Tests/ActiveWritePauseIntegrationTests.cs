using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

/// <summary>Exercises production workers after successful real provider upserts and deletions.</summary>
public sealed class ActiveWritePauseIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task EmbeddedPauseDrainsCommittedUpsertAndResumeRemovesDeletedUnmanifestedVectors() => RunAsync(true, false);

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task EmbeddedPauseResumeReplacesModifiedUnmanifestedPathWithoutStaleVectors() => RunAsync(true, true);

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task EmbeddedPauseAfterObsoleteDeleteAndRevertRestoresCommittedContent() => RunAsync(true, true, true);

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task EmbeddedPauseAfterRemovedFileDeleteAndRestoreReindexesIdenticalContent() => RunAsync(true, true, true, true);

    [AllEnvironmentFact("LOCALRAG_P7_WEAVIATE_TESTS=1", "LOCALRAG_ONNX_TESTS=1")]
    public Task WeaviatePauseDrainsCommittedUpsertAndResumeRemovesDeletedUnmanifestedVectors() => RunAsync(false, false);

    [AllEnvironmentFact("LOCALRAG_P7_WEAVIATE_TESTS=1", "LOCALRAG_ONNX_TESTS=1")]
    public Task WeaviatePauseResumeReplacesModifiedUnmanifestedPathWithoutStaleVectors() => RunAsync(false, true);

    [AllEnvironmentFact("LOCALRAG_P7_WEAVIATE_TESTS=1", "LOCALRAG_ONNX_TESTS=1")]
    public Task WeaviatePauseAfterObsoleteDeleteAndRevertRestoresCommittedContent() => RunAsync(false, true, true);

    [AllEnvironmentFact("LOCALRAG_P7_WEAVIATE_TESTS=1", "LOCALRAG_ONNX_TESTS=1")]
    public Task WeaviatePauseAfterRemovedFileDeleteAndRestoreReindexesIdenticalContent() => RunAsync(false, true, true, true);

    private static async Task RunAsync(bool embedded, bool modifiedSamePath, bool afterObsoleteDelete = false, bool restoredRemovedFile = false)
    {
        await using var fixture = await Fixture.CreateAsync(embedded);
        Task<SourceIndexingState>? pause = null; Exception? failure = null; var passed = false;
        try
        {
            await fixture.Coordinator.QueueInitialIndexAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            await fixture.UntilReadyAsync(fixture.Source.SourceId);
            var original = await fixture.IndexState.GetChunksForSourceAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            Assert.Equal(3, original.Count);
            var sentinel = Assert.Single(original, chunk => chunk.RelativePath == "sentinel.txt");
            var originalIdentity = await fixture.Sources.GetAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            fixture.Record("initial-ready", new { chunks = original.Select(chunk => chunk.ChunkId).ToArray(), fixture.Source.SourceId });

            var originalKeepText = await File.ReadAllTextAsync(Path.Combine(fixture.SourceRoot, "keep.txt"), fixture.Timeout.Token);
            fixture.Vectors.Arm(afterObsoleteDelete);
            if (restoredRemovedFile) File.Delete(Path.Combine(fixture.SourceRoot, "keep.txt"));
            else await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "keep.txt"), "A coral reef shelters a parrotfish and an orphanmarker.", fixture.Timeout.Token);
            await fixture.Coordinator.ReindexAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            await fixture.Vectors.CommittedButHeld.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var pendingWrites = await fixture.IndexState.GetPendingVectorWritesAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            Assert.All(fixture.Vectors.HeldChunkIds, id => Assert.Contains(pendingWrites, row => row.ChunkId == id));
            fixture.Record("durable-intent-at-held-mutation", new { pendingWrites });
            var duringWrite = await fixture.IndexState.GetChunksForSourceAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            if (afterObsoleteDelete)
            {
                if (restoredRemovedFile) Assert.DoesNotContain(duringWrite, chunk => chunk.RelativePath == "keep.txt");
                else Assert.Contains(duringWrite, chunk => chunk.Content.Contains("orphanmarker", StringComparison.Ordinal));
                Assert.DoesNotContain(duringWrite, chunk => fixture.Vectors.HeldChunkIds.Contains(chunk.ChunkId));
            }
            else Assert.Equal(original.Select(chunk => chunk.ChunkId).Order(), duringWrite.Select(chunk => chunk.ChunkId).Order());

            pause = fixture.Lifecycle.SetPausedAsync(fixture.Source.SourceId, true, fixture.Timeout.Token);
            await fixture.Vectors.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pausing = await fixture.States.GetAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            Assert.Equal("Pausing", pausing.State); Assert.False(pausing.AllowsWork); Assert.False(pausing.QueryVisible);
            Assert.False(pause.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ReindexAsync(fixture.Source.SourceId, fixture.Timeout.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Search.SearchAsync(new("orphanmarker", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token));
            fixture.Record("pausing-before-release", new { pausing.State, pauseCompleted = pause.IsCompleted, mutations = fixture.Vectors.Mutations.ToArray() });

            fixture.Vectors.Release();
            var paused = await pause.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("Paused", paused.State); Assert.False(paused.QueryVisible);
            // Acquiring the actual source operation gate proves the canceled worker relinquished its mutation lease.
            await using (var drained = await fixture.Gate.AcquireAsync(fixture.Source.SourceId, fixture.Timeout.Token)) { }
            var pausedMutationCount = fixture.Vectors.Mutations.Count;
            var preserved = await fixture.Sources.GetAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            Assert.Equal(originalIdentity!.SourceId, preserved!.SourceId);
            Assert.Equal(originalIdentity.DisplayName, preserved.DisplayName); Assert.Equal(originalIdentity.Description, preserved.Description);
            Assert.Equal(originalIdentity.Scope, preserved.Scope); Assert.Equal(SourceStatus.Paused, preserved.Status);
            Assert.Equal(duringWrite.Select(chunk => (chunk.ChunkId, chunk.LastIndexedUtc)).Order(), (await fixture.IndexState.GetChunksForSourceAsync(fixture.Source.SourceId, fixture.Timeout.Token)).Select(chunk => (chunk.ChunkId, chunk.LastIndexedUtc)).Order());
            fixture.Record("paused-after-drain", new { pausedMutationCount, preserved.SourceId, preserved.Status });

            // The real channel is FIFO. One actual worker must consume the paused wake before this empty probe can complete.
            Assert.Equal(1, fixture.Options.Value.Indexing.MaxConcurrentReconciliations);
            await fixture.Channel.EnqueueAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            var probeRoot = Path.Combine(fixture.Root, "empty-probe"); Directory.CreateDirectory(probeRoot);
            var probe = await fixture.Sources.RegisterAsync(probeRoot, "FIFO drain probe", fixture.Timeout.Token);
            await fixture.Coordinator.QueueInitialIndexAsync(probe.SourceId, fixture.Timeout.Token);
            await fixture.UntilReadyAsync(probe.SourceId);
            Assert.Equal(pausedMutationCount, fixture.Vectors.Mutations.Count);
            Assert.Equal("Paused", (await fixture.States.GetAsync(fixture.Source.SourceId, fixture.Timeout.Token)).State);
            fixture.Record("fifo-probe-ready-no-paused-mutation", new { probe.SourceId, mutationCount = fixture.Vectors.Mutations.Count });

            var reopened = new SqliteIndexStateStore(fixture.Database, fixture.Options);
            var reopenedWrites = await reopened.GetPendingVectorWritesAsync(fixture.Source.SourceId, fixture.Timeout.Token);
            Assert.All(fixture.Vectors.HeldChunkIds, id => Assert.Contains(reopenedWrites, row => row.ChunkId == id));
            if (modifiedSamePath) await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "keep.txt"), afterObsoleteDelete ? originalKeepText : "A pelican circles a harbor with a renewedmarker.", fixture.Timeout.Token);
            else File.Delete(Path.Combine(fixture.SourceRoot, "keep.txt"));
            File.Delete(Path.Combine(fixture.SourceRoot, "delete.txt"));
            await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "new.txt"), "A lighthouse guides passing ships through the night.", fixture.Timeout.Token);
            var resuming = await fixture.Lifecycle.SetPausedAsync(fixture.Source.SourceId, false, fixture.Timeout.Token);
            Assert.Equal("Resuming", resuming.State); Assert.False(resuming.QueryVisible);
            await fixture.UntilReadyAsync(fixture.Source.SourceId);
            Assert.True((await fixture.States.GetAsync(fixture.Source.SourceId, fixture.Timeout.Token)).QueryVisible);
            var fresh = await fixture.Search.SearchAsync(new("lighthouse", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token);
            var orphan = await fixture.Search.SearchAsync(new("orphanmarker", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token);
            var obsolete = await fixture.Search.SearchAsync(new("fossilmarker", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token);
            var replacement = await fixture.Search.SearchAsync(new("renewedmarker", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token);
            var sentinelResults = await fixture.Search.SearchAsync(new("sentinelmarker", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token);
            var reverted = await fixture.Search.SearchAsync(new("hummingbird", [fixture.Source.SourceId], Mode: SearchMode.Lexical), fixture.Timeout.Token);
            fixture.Record("resume-query-results", new { fresh, orphan, obsolete, replacement, sentinelResults, reverted, committedChunks = await fixture.IndexState.GetChunksForSourceAsync(fixture.Source.SourceId, fixture.Timeout.Token) });
            Assert.Contains(fresh.Results, result => result.RelativePath == "new.txt");
            Assert.Empty(obsolete.Results);
            Assert.Equal(modifiedSamePath, File.Exists(Path.Combine(fixture.SourceRoot, "keep.txt")));
            Assert.Empty(orphan.Results);
            Assert.Contains(sentinelResults.Results, result => result.ChunkId == sentinel.ChunkId);
            Assert.Contains(await fixture.IndexState.GetChunksForSourceAsync(fixture.Source.SourceId, fixture.Timeout.Token), chunk => chunk.ChunkId == sentinel.ChunkId && chunk.LastIndexedUtc == sentinel.LastIndexedUtc);
            if (modifiedSamePath && !afterObsoleteDelete) Assert.Contains(replacement.Results, result => result.RelativePath == "keep.txt");
            if (afterObsoleteDelete) Assert.Contains(reverted.Results, result => result.RelativePath == "keep.txt" && result.Content == originalKeepText);
            Assert.DoesNotContain(await fixture.IndexState.GetChunksForSourceAsync(fixture.Source.SourceId, fixture.Timeout.Token), chunk => chunk.RelativePath == "delete.txt" || (!modifiedSamePath && chunk.RelativePath == "keep.txt"));
            Assert.Empty(await fixture.IndexState.GetPendingVectorWritesAsync(fixture.Source.SourceId, fixture.Timeout.Token));
            passed = true;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            fixture.Vectors.Release();
            if (pause is not null)
                try { await pause.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception error) { fixture.Record("pause-cleanup", new { error = error.ToString() }); }
            await fixture.StopAsync();
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "outcome.json"), JsonSerializer.Serialize(new
            {
                completedUtc = DateTimeOffset.UtcNow,
                modifiedSamePath,
                afterObsoleteDelete,
                restoredRemovedFile,
                passed,
                provider = embedded ? "LanceDb" : "Weaviate",
                failure = failure?.ToString(),
                boundary = "Actual IndexWorker/Dispatcher and production services, one worker; post-real-provider-commit completion barrier, not interruption inside native I/O or full HTTP/installed-host acceptance",
                events = fixture.Events.ToArray(),
                mutations = fixture.Vectors.Mutations.ToArray(),
                cleanupFailures = fixture.CleanupFailures,
                sourceRecords = await fixture.Sources.ListAsync(CancellationToken.None),
                reconciliations = await fixture.Reconciliations.ListAsync(CancellationToken.None),
                sourceFiles = Directory.EnumerateFiles(fixture.SourceRoot).Select(path => new { path = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }),
                nativeManifest = embedded ? Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST") : null,
                productionHostSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(HostApplication).Assembly.Location)))
            }, Json));
            ManagerHostIntegrationTests.RecordManifest(fixture.Root);
            if (failure is null) Assert.Empty(fixture.CleanupFailures);
        }
    }

    private sealed class CommitCompletionGate(IVectorStore inner, Action<string, object> record, Func<IReadOnlyList<string>, CancellationToken, Task> verifyIntents, CancellationToken deadline) : IVectorStore
    {
        private int _armed;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CommittedButHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[] HeldChunkIds { get; private set; } = [];
        internal ConcurrentQueue<object> Mutations { get; } = new();
        internal void Arm(bool afterObsoleteDelete) => Interlocked.Exchange(ref _armed, afterObsoleteDelete ? 2 : 1);
        internal void Release() => _release.TrySetResult();
        public Task EnsureReadyAsync(CancellationToken ct) => inner.EnsureReadyAsync(ct);
        public async Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken ct)
        {
            await verifyIntents(documents.Select(document => document.Chunk.ChunkId).ToArray(), ct);
            await inner.UpsertAsync(documents, ct);
            if (documents.Count == 0) return;
            var mutation = new { kind = "upsert", committedUtc = DateTimeOffset.UtcNow, ids = documents.Select(document => document.Chunk.ChunkId).ToArray() };
            Mutations.Enqueue(mutation); record("real-upsert-committed", mutation);
            if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1) return;
            await HoldCompletionAsync(documents.Select(document => document.Chunk.ChunkId).ToArray(), "upsert", ct);
        }
        private async Task HoldCompletionAsync(string[] ids, string operation, CancellationToken ct)
        {
            HeldChunkIds = ids;
            using var cancellation = ct.Register(() => { record("active-write-cancellation-observed", new { observedUtc = DateTimeOffset.UtcNow }); CancellationObserved.TrySetResult(); });
            record("real-mutation-completion-held", new { operation, ids });
            CommittedButHeld.TrySetResult();
            // Deliberately retain the source lease after the successful real commit; failure cleanup always releases this barrier.
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), deadline);
            record("committed-mutation-completion-released", new { operation, cancellationRequested = ct.IsCancellationRequested });
        }
        public async Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            await verifyIntents(ids, ct);
            await inner.DeleteAsync(ids, ct);
            if (ids.Count > 0) { var mutation = new { kind = "delete", committedUtc = DateTimeOffset.UtcNow, ids }; Mutations.Enqueue(mutation); record("real-delete-committed", mutation); }
            if (ids.Count > 0 && Interlocked.CompareExchange(ref _armed, 0, 2) == 2)
                await HoldCompletionAsync(ids.ToArray(), "obsolete-delete", ct);
        }
        public async Task DeleteSourceAsync(string sourceId, CancellationToken ct)
        {
            await inner.DeleteSourceAsync(sourceId, ct); var mutation = new { kind = "delete-source", committedUtc = DateTimeOffset.UtcNow, sourceId }; Mutations.Enqueue(mutation); record("real-source-delete-committed", mutation);
        }
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> vector, CancellationToken ct) => inner.SearchAsync(request, vector, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(RepositoryRoot(), "artifacts", "runs", "p7-active-pause-20261017", Guid.NewGuid().ToString("N"));
        internal string SourceRoot => Path.Combine(Root, "source");
        internal CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(90));
        internal IOptions<LocalRagOptions> Options { get; private set; } = null!;
        internal SqliteDatabase Database { get; private set; } = null!;
        internal SqliteSourceRegistry Sources { get; private set; } = null!;
        internal SqliteIndexStateStore IndexState { get; private set; } = null!;
        internal SqliteReconciliationStore Reconciliations { get; private set; } = null!;
        internal SourceIndexingStateStore States { get; private set; } = null!;
        internal SourceOperationGate Gate { get; } = new();
        internal IndexWorkChannel Channel { get; } = new();
        internal IndexCoordinator Coordinator { get; private set; } = null!;
        internal SourceLifecycleService Lifecycle { get; private set; } = null!;
        internal RagSearchService Search { get; private set; } = null!;
        internal SourceRecord Source { get; private set; } = null!;
        internal CommitCompletionGate Vectors { get; private set; } = null!;
        internal ConcurrentQueue<object> Events { get; } = new();
        internal List<string> CleanupFailures { get; } = [];
        private IndexWorker _worker = null!;
        private ReconciliationDispatcher _dispatcher = null!;
        private SourceWatcherRegistry _watchers = null!;
        private BgeOnnxEmbeddingService _embeddings = null!;
        private InstallationOwnershipStore _ownership = null!;
        private IVectorStore _inner = null!;
        private HttpClient? _http;
        private ServiceProvider _services = null!;
        private bool _stopped;

        internal static async Task<Fixture> CreateAsync(bool embedded)
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.SourceRoot);
            fixture.Options = Microsoft.Extensions.Options.Options.Create(new LocalRagOptions
            {
                DataDirectory = Path.Combine(fixture.Root, "data"),
                Indexing = new() { StabilityIntervalMilliseconds = 0, MaxConcurrentReconciliations = 1, ReconciliationDispatchPollSeconds = 1 },
                VectorStore = new()
                {
                    Provider = embedded ? VectorStoreProvider.LanceDb : VectorStoreProvider.Weaviate,
                    LanceDb = new() { Path = Path.Combine(fixture.Root, "vectors") },
                    Weaviate = new() { Endpoint = "http://127.0.0.1:18080", Collection = "P7Pause" + Guid.NewGuid().ToString("N") }
                }
            });
            var database = new SqliteDatabase(fixture.Options); fixture.Database = database; fixture.Sources = new(database, fixture.Options); fixture.IndexState = new(database, fixture.Options);
            var profileGate = new ChunkProfileOperationGate(); var profiles = new SqliteChunkProfileStateStore(database, profileGate);
            fixture.Reconciliations = new(database); fixture.States = new(database);
            await fixture.Sources.InitializeAsync(fixture.Timeout.Token); await fixture.IndexState.InitializeAsync(fixture.Timeout.Token);
            await profiles.InitializeAsync(fixture.Timeout.Token); await fixture.Reconciliations.InitializeAsync(fixture.Timeout.Token);
            fixture._ownership = new(fixture.Options);
            if (embedded) fixture._inner = new LanceDbVectorStore(fixture.Options.Value.VectorStore.LanceDb, fixture._ownership, new LanceDbIpcCodec(), () => new LanceDbNativeApi(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!));
            else { fixture._http = new() { BaseAddress = new("http://127.0.0.1:18080"), Timeout = TimeSpan.FromSeconds(30) }; fixture._inner = new WeaviateVectorStore(fixture._http, fixture.Options, fixture._ownership); }
            fixture.Vectors = new(fixture._inner, fixture.Record, async (ids, ct) =>
            {
                if (ids.Count == 0) return;
                var pending = await fixture.IndexState.GetPendingVectorWritesAsync(fixture.Source.SourceId, ct);
                Assert.All(ids, id => Assert.Contains(pending, row => row.ChunkId == id));
                fixture.Record("intent-before-provider-mutation", new { ids });
            }, fixture.Timeout.Token);
            fixture._embeddings = new(fixture.Options); await fixture._embeddings.ValidateAsync(fixture.Timeout.Token);
            var tokenizer = new BertWordPieceTokenizer(Path.Combine(Environment.ExpandEnvironmentVariables(fixture.Options.Value.Embedding.ModelDirectory), "vocab.txt"));
            IStructuralChunker[] adapters = [new CSharpStructuralChunker(), new TypeScriptJavaScriptStructuralChunker(), new PythonStructuralChunker(), new MarkdownStructuralChunker(), new JsonStructuralChunker(), new YamlStructuralChunker(), new TomlStructuralChunker(), new XmlStructuralChunker()];
            var profile = new ChunkProfileProvider(adapters, fixture.Options);
            var chunker = new CompositeChunker(adapters, new GenericChunker(fixture.Options, profile, tokenizer), profile, fixture.Options, tokenizer, NullLogger<CompositeChunker>.Instance);
            var extraction = new ContentExtractionService([new PlainTextContentExtractor()]); var metrics = new OperationalMetrics(); var maintenance = new HostMaintenanceCoordinator(); var signal = new ReconciliationDispatchSignal();
            var scheduler = new ReconciliationScheduler(fixture.Reconciliations, fixture.Channel, metrics, signal, maintenance, fixture.States);
            fixture._watchers = new(scheduler, fixture.Options, NullLogger<SourceWatcherRegistry>.Instance, maintenance);
            fixture.Coordinator = new(fixture.Sources, fixture.IndexState, fixture.Vectors, new(fixture.IndexState, fixture._embeddings, chunker, fixture.Vectors, extraction, fixture.Options, metrics, fixture.Reconciliations), new(fixture.Options, extraction), fixture._watchers, scheduler, fixture.Reconciliations, fixture.Gate, profile, profiles, NullLogger<IndexCoordinator>.Instance, fixture.States);
            fixture.Lifecycle = new(fixture.Sources, fixture.States, fixture.Gate, fixture._watchers, fixture.Coordinator, maintenance);
            fixture.Search = new(fixture._embeddings, fixture.Vectors, fixture.IndexState, fixture.Sources, profiles, profileGate, metrics, fixture.States);
            fixture._services = new ServiceCollection().AddSingleton(fixture.Coordinator).BuildServiceProvider();
            fixture._worker = new(fixture.Channel, fixture._services, fixture.Reconciliations, fixture.Gate, maintenance, signal, fixture.Options, metrics, NullLogger<IndexWorker>.Instance, fixture.States);
            fixture._dispatcher = new(fixture.Reconciliations, scheduler, signal, fixture.Options, metrics, maintenance, NullLogger<ReconciliationDispatcher>.Instance, fixture.States);
            await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "keep.txt"), "A hummingbird drinks sweet nectar.", fixture.Timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "delete.txt"), "An obsolete fossilmarker belongs to this source.", fixture.Timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "sentinel.txt"), "An unchanged sentinelmarker must remain searchable.", fixture.Timeout.Token);
            fixture.Source = await fixture.Sources.RegisterCatalogAsync(fixture.SourceRoot, "Active write fixture", "Keep this metadata", "global", null, fixture.Timeout.Token);
            await fixture._worker.StartAsync(fixture.Timeout.Token); await fixture._dispatcher.StartAsync(fixture.Timeout.Token);
            fixture.Record("actual-workers-started", new { fixture.Options.Value.Indexing.MaxConcurrentReconciliations, provider = embedded ? "LanceDb" : "Weaviate" });
            return fixture;
        }

        internal void Record(string stage, object detail) => Events.Enqueue(new { utc = DateTimeOffset.UtcNow, stage, detail });
        internal async Task UntilReadyAsync(string sourceId)
        {
            while ((await Sources.GetAsync(sourceId, Timeout.Token))?.Status != SourceStatus.Ready ||
                   (await Reconciliations.GetAsync(sourceId, Timeout.Token))?.State != ReconciliationState.Clean)
                await Task.Delay(25, Timeout.Token);
        }
        internal async Task StopAsync()
        {
            if (_stopped) return; _stopped = true; Vectors.Release();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _dispatcher.StopAsync(drain.Token); } catch (Exception error) { CleanupFailures.Add(error.ToString()); }
            try { await _worker.StopAsync(drain.Token); } catch (Exception error) { CleanupFailures.Add(error.ToString()); }
            _dispatcher.Dispose(); _worker.Dispose(); _watchers.Dispose(); await _services.DisposeAsync();
            if (_inner is IDisposable disposable) disposable.Dispose(); _embeddings.Dispose(); _ownership.Dispose();
            if (_http is not null)
            {
                try { await _http.DeleteAsync("/v1/schema/" + Options.Value.VectorStore.Weaviate.Collection, drain.Token); } catch (Exception error) { CleanupFailures.Add(error.ToString()); }
                _http.Dispose();
            }
        }
        public async ValueTask DisposeAsync() { await StopAsync(); Timeout.Dispose(); }
        private static string RepositoryRoot() { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path is not null && !File.Exists(Path.Combine(path.FullName, "LocalRag.sln"))) path = path.Parent; return path!.FullName; }
    }
}
