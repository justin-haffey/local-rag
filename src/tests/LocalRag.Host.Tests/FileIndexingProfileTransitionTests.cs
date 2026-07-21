using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class FileIndexingProfileTransitionTests
{
    [Fact]
    public async Task ForcedProfileChangeBypassesMetadataAndContentHashShortCircuits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"localrag-force-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(path, "stable content");
        try
        {
            var state = new RecordingState();
            var embeddings = new RecordingEmbeddings();
            var chunker = new SwitchableChunker("profile-a");
            var vectors = new RecordingVectors();
            var extractor = new RecordingExtractor();
            var options = Options.Create(new LocalRagOptions
            {
                Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 }
            });
            var service = new FileIndexingService(
                state,
                embeddings,
                chunker,
                vectors,
                new ContentExtractionService([extractor]),
                options,
                new OperationalMetrics());
            var source = new SourceRecord(
                "source", root, "fixture", SourceStatus.Ready, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, null, null, embeddings.ProfileId);

            await service.IndexAsync(source, path, "sample.txt", new FileInfo(path), CancellationToken.None);
            await service.IndexAsync(source, path, "sample.txt", new FileInfo(path), CancellationToken.None);

            Assert.Equal(1, extractor.Calls);
            Assert.Equal(1, embeddings.PassageCalls);

            chunker.Profile = "profile-b";
            await service.IndexAsync(
                source, path, "sample.txt", new FileInfo(path), CancellationToken.None, forceContentProcessing: true);

            Assert.Equal(2, extractor.Calls);
            Assert.Equal(2, embeddings.PassageCalls);
            Assert.Equal(["profile-a", "profile-b"], vectors.UpsertedIds);
            Assert.Equal(["profile-a"], vectors.DeletedIds);
            Assert.Equal("profile-b", Assert.Single(state.Chunks).ChunkProfileFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisablingAdaptersRollsBackThroughRealReprocessingAndAtomicVisibility()
    {
        var root = Path.Combine(Path.GetTempPath(), $"localrag-generic-rollback-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var path = Path.Combine(sourceRoot, "Worker.cs");
        await File.WriteAllTextAsync(path, "namespace Demo;\npublic class Worker\n{\n    public void Run() { }\n}");
        try
        {
            var structuralOptions = TestOptions(root, ["csharp"]);
            var genericOptions = TestOptions(root, []);
            var structural = CreateComposite(structuralOptions, [new CSharpStructuralChunker()]);
            var generic = CreateComposite(genericOptions, []);
            Assert.Equal("generic/1", generic.Profile.ChunkerIdentity);
            Assert.NotEqual("generic/1", generic.Profile.Fingerprint);

            var database = new SqliteDatabase(structuralOptions);
            var registry = new SqliteSourceRegistry(database, structuralOptions);
            var state = new SqliteIndexStateStore(database, structuralOptions);
            await registry.InitializeAsync(CancellationToken.None);
            await state.InitializeAsync(CancellationToken.None);
            var source = await registry.RegisterAsync(sourceRoot, "rollback fixture", CancellationToken.None);
            var gate = new ChunkProfileOperationGate();
            var profiles = new SqliteChunkProfileStateStore(database, gate);
            await profiles.InitializeAsync(CancellationToken.None);
            await profiles.GetOrCreateAsync(
                source.SourceId, structural.Profile.Fingerprint, hasIndexedChunks: false, CancellationToken.None);

            var vectors = new RecordingVectors();
            var embeddings = new RecordingEmbeddings();
            var extractor = new RecordingExtractor();
            var structuralService = new FileIndexingService(
                state, embeddings, structural.Chunker, vectors, new ContentExtractionService([extractor]),
                structuralOptions, new OperationalMetrics());
            await structuralService.IndexAsync(
                source, path, "Worker.cs", new FileInfo(path), CancellationToken.None, forceContentProcessing: true);
            var structuralChunks = await state.GetChunksForSourceAsync(source.SourceId, CancellationToken.None);
            Assert.Contains(structuralChunks, chunk => chunk.ChunkerId == "csharp");
            var structuralIds = structuralChunks.Select(chunk => chunk.ChunkId).ToHashSet(StringComparer.Ordinal);

            await profiles.BeginTransitionAsync(source.SourceId, generic.Profile.Fingerprint, CancellationToken.None);
            Assert.False(await profiles.IsQueryVisibleAsync(source.SourceId, CancellationToken.None));
            var genericService = new FileIndexingService(
                state, embeddings, generic.Chunker, vectors, new ContentExtractionService([extractor]),
                genericOptions, new OperationalMetrics());
            await genericService.IndexAsync(
                source, path, "Worker.cs", new FileInfo(path), CancellationToken.None, forceContentProcessing: true);

            var genericChunks = await state.GetChunksForSourceAsync(source.SourceId, CancellationToken.None);
            Assert.NotEmpty(genericChunks);
            Assert.All(genericChunks, chunk =>
            {
                Assert.Equal("generic", chunk.ChunkerId);
                Assert.Equal(generic.Profile.Fingerprint, chunk.ChunkProfileFingerprint);
            });
            Assert.True(structuralIds.IsSubsetOf(vectors.DeletedIds.ToHashSet(StringComparer.Ordinal)));
            Assert.Equal(
                genericChunks.Select(chunk => chunk.ChunkId).OrderBy(id => id),
                vectors.ActiveIds.OrderBy(id => id));
            Assert.False(await profiles.IsQueryVisibleAsync(source.SourceId, CancellationToken.None));

            await profiles.CompleteTransitionAsync(source.SourceId, generic.Profile.Fingerprint, CancellationToken.None);
            Assert.True(await profiles.IsQueryVisibleAsync(source.SourceId, CancellationToken.None));
            Assert.Equal(generic.Profile.Fingerprint,
                (await profiles.GetAsync(source.SourceId, CancellationToken.None))?.ActiveFingerprint);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static IOptions<LocalRagOptions> TestOptions(string root, string[] enabledAdapters) =>
        Options.Create(new LocalRagOptions
        {
            DataDirectory = Path.Combine(root, "data"),
            Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
            Chunking = new ChunkingOptions
            {
                TargetTokens = 384,
                MaximumTokens = 480,
                OverlapTokens = 64,
                EnabledAdapters = enabledAdapters
            },
            Embedding = new EmbeddingOptions
            {
                ProfileId = "test-embedding",
                TokenizerId = "test-tokenizer",
                MaximumTokens = 512
            }
        });

    private static CompositeFixture CreateComposite(
        IOptions<LocalRagOptions> options,
        IStructuralChunker[] adapters)
    {
        var profile = new ChunkProfileProvider(adapters, options);
        var tokenCounter = new CharacterUpperBoundTokenCounter();
        var generic = new GenericChunker(options, profile, tokenCounter);
        return new CompositeFixture(
            new CompositeChunker(adapters, generic, profile, options, tokenCounter, NullLogger<CompositeChunker>.Instance),
            profile);
    }

    private sealed record CompositeFixture(IChunker Chunker, ChunkProfileProvider Profile);

    private sealed class RecordingExtractor : IContentExtractor
    {
        public int Calls { get; private set; }
        public bool Supports(string path) => true;
        public async Task<string> ExtractAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
    }

    private sealed class SwitchableChunker(string profile) : IChunker
    {
        public string Profile { get; set; } = profile;
        public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent) =>
        [
            new ChunkRecord(
                Profile, source.SourceId, file.FileId, file.RelativePath, "text", null, 1, 1, 0,
                normalizedContent, "content-hash", 4, source.EmbeddingProfileId, DateTimeOffset.UtcNow,
                ChunkProfileFingerprint: Profile, StructuralLocator: "lines:1-1")
        ];
    }

    private sealed class RecordingEmbeddings : IEmbeddingService
    {
        public string ProfileId => "test-embedding";
        public int PassageCalls { get; private set; }
        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float>>([1]);
        public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken)
        {
            PassageCalls++;
            return Task.FromResult<IReadOnlyList<float>>([1]);
        }
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingState : IIndexStateStore
    {
        public IndexedFile? File { get; private set; }
        public IReadOnlyList<ChunkRecord> Chunks { get; private set; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(File);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult(Chunks);
        public Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
        {
            File = file;
            Chunks = chunks;
            return Task.CompletedTask;
        }
        public Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) =>
            Task.FromResult(Chunks.SingleOrDefault(chunk => chunk.ChunkId == chunkId));
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(Chunks);
    }

    private sealed class RecordingVectors : IVectorStore
    {
        public List<string> UpsertedIds { get; } = [];
        public List<string> DeletedIds { get; } = [];
        public HashSet<string> ActiveIds { get; } = new(StringComparer.Ordinal);
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
        {
            UpsertedIds.AddRange(documents.Select(document => document.Chunk.ChunkId));
            ActiveIds.UnionWith(documents.Select(document => document.Chunk.ChunkId));
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
        {
            DeletedIds.AddRange(chunkIds);
            ActiveIds.ExceptWith(chunkIds);
            return Task.CompletedTask;
        }
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            SearchRequest request,
            IReadOnlyList<float> queryVector,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }
}
