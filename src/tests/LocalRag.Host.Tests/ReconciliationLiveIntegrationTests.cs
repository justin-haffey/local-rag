using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ReconciliationLiveIntegrationTests
{
    [AllEnvironmentFact("WEAVIATE_TEST_ENDPOINT", "LOCALRAG_ONNX_TESTS=1")]
    public async Task OverflowRestartRecoveryConvergesRealOnnxAndWeaviateWithoutReembeddingUnchangedFiles()
    {
        var endpoint = Environment.GetEnvironmentVariable("WEAVIATE_TEST_ENDPOINT")!;
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-live-recovery-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var collection = $"LocalRagRecovery{Guid.NewGuid():N}";
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        using var onnx = new BgeOnnxEmbeddingService(Options.Create(new LocalRagOptions()));
        SourceWatcherRegistry? watchers = null;
        try
        {
            var options = Options.Create(new LocalRagOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
                Weaviate = new WeaviateOptions
                {
                    Endpoint = endpoint,
                    Collection = collection,
                    Vectorizer = "none",
                    BatchSize = 10
                }
            });
            var metrics = new OperationalMetrics();
            var database = new SqliteDatabase(options);
            var sources = new SqliteSourceRegistry(database, options);
            var indexState = new SqliteIndexStateStore(database, options);
            var profileGate = new ChunkProfileOperationGate();
            var profiles = new SqliteChunkProfileStateStore(database, profileGate);
            var store = new SqliteReconciliationStore(database);
            await sources.InitializeAsync(CancellationToken.None);
            await indexState.InitializeAsync(CancellationToken.None);
            await profiles.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "retry.txt"),
                "Retry backoff is configured by the durable reconciliation scheduler.");
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "obsolete.txt"),
                "This obsolete recovery marker must be deleted from the index.");
            var source = await sources.RegisterAsync(sourceRoot, "live recovery", CancellationToken.None);
            var embeddings = new CountingEmbeddingService(onnx);
            var vectorStore = new WeaviateVectorStore(client, options);
            var extraction = new ContentExtractionService([new PlainTextContentExtractor()]);
            var profile = new ChunkProfileProvider(options);
            var scheduler = new ReconciliationScheduler(store, new IndexWorkChannel(), metrics);
            watchers = new SourceWatcherRegistry(scheduler, options, NullLogger<SourceWatcherRegistry>.Instance);
            var coordinator = new IndexCoordinator(
                sources,
                indexState,
                vectorStore,
                new FileIndexingService(
                    indexState,
                    embeddings,
                    new GenericChunker(options, profile),
                    vectorStore,
                    extraction,
                    options,
                    metrics),
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
            Assert.Equal(2, initial.Result.ChangedFiles);
            Assert.True((await store.CompleteAsync(initialLease, initial.Result, CancellationToken.None)).Applied);

            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "retry.txt"),
                "Durable retry recovery resumes an expired lease and converges successfully.");
            File.Delete(Path.Combine(sourceRoot, "obsolete.txt"));
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "created.txt"),
                "Watcher overflow recovery indexes newly created content.");
            await watchers.NotifyErrorAsync(
                source.SourceId,
                new InternalBufferOverflowException(),
                CancellationToken.None);

            var staleLease = Assert.IsType<ReconciliationLease>(await store.TryLeaseAsync(
                source.SourceId,
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None));
            await Task.Delay(20);
            var restartedStore = new SqliteReconciliationStore(database);
            await restartedStore.InitializeAsync(CancellationToken.None);
            Assert.Contains(source.SourceId, await restartedStore.RecoverExpiredLeasesAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None));
            var recoveredLease = Assert.IsType<ReconciliationLease>(await restartedStore.TryLeaseAsync(
                source.SourceId,
                TimeSpan.FromMinutes(2),
                CancellationToken.None));
            Assert.NotEqual(staleLease.LeaseId, recoveredLease.LeaseId);

            embeddings.ResetPassageCalls();
            var recovery = await coordinator.ProcessAsync(recoveredLease, CancellationToken.None);
            Assert.Equal(2, recovery.Result.ChangedFiles);
            Assert.Equal(1, recovery.Result.DeletedFiles);
            Assert.True(embeddings.PassageCalls > 0);
            Assert.True((await restartedStore.CompleteAsync(
                recoveredLease,
                recovery.Result,
                CancellationToken.None)).Applied);
            Assert.False((await restartedStore.CompleteAsync(
                staleLease,
                new ReconciliationResult(99, 99, 99),
                CancellationToken.None)).Applied);

            var queryVector = await embeddings.EmbedQueryAsync("How does recovery resume after an expired lease?", CancellationToken.None);
            var results = await vectorStore.SearchAsync(
                new SearchRequest("durable retry recovery", [source.SourceId], 10),
                queryVector,
                CancellationToken.None);
            Assert.Contains(results, result => result.RelativePath == "retry.txt");
            Assert.DoesNotContain(results, result => result.RelativePath == "obsolete.txt");

            await watchers.NotifyErrorAsync(
                source.SourceId,
                new InternalBufferOverflowException(),
                CancellationToken.None);
            var unchangedLease = Assert.IsType<ReconciliationLease>(await restartedStore.TryLeaseAsync(
                source.SourceId,
                TimeSpan.FromMinutes(2),
                CancellationToken.None));
            embeddings.ResetPassageCalls();
            var unchanged = await coordinator.ProcessAsync(unchangedLease, CancellationToken.None);
            Assert.Equal(0, unchanged.Result.ChangedFiles);
            Assert.Equal(0, unchanged.Result.DeletedFiles);
            Assert.Equal(2, unchanged.Result.UnchangedFiles);
            Assert.Equal(0, unchanged.EmbeddingCount);
            Assert.Equal(0, unchanged.UpsertCount);
            Assert.Equal(0, embeddings.PassageCalls);
            Assert.True((await restartedStore.CompleteAsync(
                unchangedLease,
                unchanged.Result,
                CancellationToken.None)).IsClean);

            var finalState = Assert.IsType<SourceReconciliation>(await restartedStore.GetAsync(
                source.SourceId,
                CancellationToken.None));
            Assert.Equal(ReconciliationState.Clean, finalState.State);
            Assert.Equal(finalState.DesiredGeneration, finalState.CompletedGeneration);
            Assert.Equal(SourceStatus.Ready, (await sources.GetAsync(source.SourceId, CancellationToken.None))?.Status);
        }
        finally
        {
            watchers?.Dispose();
            try
            {
                await client.DeleteAsync($"v1/schema/{collection}");
            }
            catch (HttpRequestException)
            {
                // The test failure remains primary if the external service disappears during cleanup.
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingEmbeddingService(IEmbeddingService inner) : IEmbeddingService
    {
        public string ProfileId => inner.ProfileId;
        public int PassageCalls { get; private set; }

        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) =>
            inner.EmbedQueryAsync(input, cancellationToken);

        public async Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken)
        {
            PassageCalls++;
            return await inner.EmbedPassageAsync(input, cancellationToken);
        }

        public Task ValidateAsync(CancellationToken cancellationToken) => inner.ValidateAsync(cancellationToken);

        public void ResetPassageCalls() => PassageCalls = 0;
    }
}
