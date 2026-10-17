using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class VectorWriteIntentRecoveryTests
{
    [Theory]
    [InlineData("before-upsert")]
    [InlineData("partial-upsert")]
    [InlineData("after-upsert")]
    [InlineData("after-manifest")]
    [InlineData("during-partial-cleanup")]
    [InlineData("after-delete-before-acknowledgement")]
    [InlineData("after-acknowledgement")]
    public async Task ReopenedDurableIntentRecoveryIsIdempotentAndPreservesCommittedAndOtherSourceIds(string window)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = window == "after-manifest" ? new[] { fixture.CommittedChunk.ChunkId } : new[] { "attempt-a", "attempt-b" };
        await fixture.State.StageVectorWritesAsync(fixture.Source.SourceId, "unfinished-file", ids, CancellationToken.None);
        await fixture.State.StageVectorWritesAsync(fixture.Other.SourceId, "other-file", ["other-pending"], CancellationToken.None);
        if (window is "partial-upsert" or "after-upsert" or "during-partial-cleanup" or "after-delete-before-acknowledgement" or "after-acknowledgement") fixture.Vectors.Ids.Add(ids[0]);
        if (window is "after-upsert" or "during-partial-cleanup" or "after-delete-before-acknowledgement" or "after-acknowledgement") fixture.Vectors.Ids.Add(ids[1]);
        if (window == "after-acknowledgement")
        {
            await fixture.Vectors.DeleteAsync(ids, CancellationToken.None);
            await fixture.State.ClearPendingVectorWritesAsync(fixture.Source.SourceId, ids, CancellationToken.None);
        }
        SqliteConnection.ClearAllPools();
        var reopened = new SqliteIndexStateStore(fixture.Database, fixture.Options);
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal(window == "after-acknowledgement" ? [] : ids.Order().ToArray(), (await reopened.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None)).Select(row => row.ChunkId).Order());

        if (window == "during-partial-cleanup")
        {
            fixture.Vectors.FailNextPartialDelete = true;
            Assert.False(await fixture.Coordinator(reopened).ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
            Assert.Equal(SourceStatus.Degraded, (await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None))!.Status);
            Assert.Equal(2, (await reopened.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None)).Count);
            Assert.Single(ids, fixture.Vectors.Ids.Contains);
            reopened = new(fixture.Database, fixture.Options);
        }

        if (window == "after-delete-before-acknowledgement")
        {
            var interrupted = fixture.Coordinator(new FailAcknowledgementState(reopened));
            Assert.False(await interrupted.ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
            Assert.Equal(SourceStatus.Degraded, (await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None))!.Status);
            Assert.DoesNotContain(ids[0], fixture.Vectors.Ids); Assert.DoesNotContain(ids[1], fixture.Vectors.Ids);
            Assert.Equal(2, (await reopened.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None)).Count);
            reopened = new(fixture.Database, fixture.Options);
        }
        var coordinator = fixture.Coordinator(reopened);
        Assert.True(await coordinator.ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Empty(await reopened.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Contains(fixture.CommittedChunk.ChunkId, fixture.Vectors.Ids);
        Assert.Contains("other-committed", fixture.Vectors.Ids);
        Assert.Single(await reopened.GetPendingVectorWritesAsync(fixture.Other.SourceId, CancellationToken.None));
        if (window != "after-manifest") Assert.All(ids, id => Assert.DoesNotContain(id, fixture.Vectors.Ids));
        Assert.DoesNotContain(fixture.Vectors.Deletes.SelectMany(batch => batch), id => id is "other-committed" or "other-pending" || id == fixture.CommittedChunk.ChunkId);
        var deletionCalls = fixture.Vectors.Deletes.Count;
        Assert.True(await fixture.Coordinator(new SqliteIndexStateStore(fixture.Database, fixture.Options)).ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Equal(deletionCalls, fixture.Vectors.Deletes.Count);
        Assert.Equal(fixture.CommittedChunk, Assert.Single(await reopened.GetChunksForSourceAsync(fixture.Source.SourceId, CancellationToken.None)));
        Assert.True(File.Exists(Path.Combine(fixture.Source.CanonicalRootPath, "sentinel.txt")));
    }

    [Fact]
    public async Task ManifestRollbackRetainsIntentAndAtomicCommitRetiresOnlyPublishedIds()
    {
        await using var fixture = await Fixture.CreateAsync();
        var file = new IndexedFile("new-file", fixture.Source.SourceId, "new.txt", "content-hash", 4, DateTimeOffset.UtcNow);
        var chunk = fixture.Chunk(file, "new-chunk", "new text");
        await fixture.State.StageVectorWritesAsync(file.SourceId, file.FileId, [chunk.ChunkId, "unpublished-same-file"], CancellationToken.None);
        await fixture.State.StageVectorWritesAsync(fixture.Other.SourceId, file.FileId, [chunk.ChunkId], CancellationToken.None);
        await using (var connection = await fixture.Database.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER RejectIntentManifest BEFORE INSERT ON Chunks WHEN NEW.ChunkId='new-chunk' BEGIN SELECT RAISE(ABORT,'injected manifest failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => fixture.State.SaveFileAndChunksAsync(file, [chunk], CancellationToken.None));
        Assert.Null(await fixture.State.GetFileAsync(file.SourceId, file.RelativePath, CancellationToken.None));
        Assert.Equal(2, (await fixture.State.GetPendingVectorWritesAsync(file.SourceId, CancellationToken.None)).Count);
        await using (var connection = await fixture.Database.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand()) { command.CommandText = "DROP TRIGGER RejectIntentManifest;"; await command.ExecuteNonQueryAsync(); }
        await fixture.State.SaveFileAndChunksAsync(file, [chunk], CancellationToken.None);
        Assert.Equal("unpublished-same-file", Assert.Single(await fixture.State.GetPendingVectorWritesAsync(file.SourceId, CancellationToken.None)).ChunkId);
        Assert.Equal(chunk.ChunkId, Assert.Single(await fixture.State.GetPendingVectorWritesAsync(fixture.Other.SourceId, CancellationToken.None)).ChunkId);
        Assert.Equal(chunk, await fixture.State.GetChunkAsync(chunk.ChunkId, CancellationToken.None));
    }

    [Fact]
    public async Task IntentSchemaMigratesAdditivelyRejectsFutureVersionsAndRemovalResetRemainScoped()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var connection = await fixture.Database.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE PendingVectorWrites; DELETE FROM SchemaVersions WHERE Name='vector-write-intents';"; await command.ExecuteNonQueryAsync();
        }
        var migrated = new SqliteIndexStateStore(fixture.Database, fixture.Options); await migrated.InitializeAsync(CancellationToken.None);
        Assert.Equal(fixture.CommittedChunk, await migrated.GetChunkAsync(fixture.CommittedChunk.ChunkId, CancellationToken.None));
        // There is intentionally no Files FK: an intent must commit before the first file manifest exists.
        await migrated.StageVectorWritesAsync(fixture.Source.SourceId, "not-yet-a-file", ["same-id"], CancellationToken.None);
        await migrated.StageVectorWritesAsync(fixture.Other.SourceId, "not-yet-a-file", ["same-id"], CancellationToken.None);
        await migrated.ClearPendingVectorWritesAsync(fixture.Source.SourceId, ["same-id"], CancellationToken.None);
        Assert.Single(await migrated.GetPendingVectorWritesAsync(fixture.Other.SourceId, CancellationToken.None));
        await migrated.StageVectorWritesAsync(fixture.Source.SourceId, "not-yet-a-file", ["source-pending"], CancellationToken.None);
        await fixture.Sources.RemoveAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.Empty(await migrated.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Single(await migrated.GetPendingVectorWritesAsync(fixture.Other.SourceId, CancellationToken.None));
        await using (var connection = await fixture.Database.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand()) { command.CommandText = "UPDATE SchemaVersions SET Version=2 WHERE Name='vector-write-intents';"; await command.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteIndexStateStore(fixture.Database, fixture.Options).InitializeAsync(CancellationToken.None));
        Assert.Single(await migrated.GetPendingVectorWritesAsync(fixture.Other.SourceId, CancellationToken.None));
        var sentinel = Path.Combine(fixture.Root, "unrelated.keep"); await File.WriteAllTextAsync(sentinel, "preserve unrelated bytes");
        await fixture.Database.ResetAsync(CancellationToken.None);
        await fixture.Sources.InitializeAsync(CancellationToken.None); await migrated.InitializeAsync(CancellationToken.None);
        Assert.Empty(await migrated.GetPendingVectorWritesAsync(fixture.Other.SourceId, CancellationToken.None));
        Assert.Equal("preserve unrelated bytes", await File.ReadAllTextAsync(sentinel));
        Assert.True(File.Exists(Path.Combine(fixture.Source.CanonicalRootPath, "sentinel.txt")));
    }

    [Fact]
    public async Task UnsupportedStateFailsBeforeProviderMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unsupported = new StateWithoutIntents(fixture.State);
        var beforeEnsure = fixture.Vectors.EnsureCalls;
        Assert.False(await fixture.Coordinator(unsupported).ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Equal(beforeEnsure, fixture.Vectors.EnsureCalls);
        var indexer = fixture.Indexer(unsupported); var path = Path.Combine(fixture.Source.CanonicalRootPath, "sentinel.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.IndexAsync(fixture.Source, path, "sentinel.txt", new(path), CancellationToken.None));
        Assert.Empty(fixture.Vectors.Deletes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ObsoleteDeleteOrAcknowledgementFailureAfterPublicationRemainsDegradedAndRecovers(bool acknowledgementFailure, bool removedFile)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Vectors.AllowUpsert = true;
        if (removedFile) File.Delete(Path.Combine(fixture.Source.CanonicalRootPath, "sentinel.txt"));
        else await File.WriteAllTextAsync(Path.Combine(fixture.Source.CanonicalRootPath, "sentinel.txt"), "The updated committed file now describes a migrating albatross.");
        IIndexStateStore state = acknowledgementFailure ? new FailAcknowledgementState(fixture.State) : fixture.State;
        fixture.Vectors.FailNextPartialDelete = !acknowledgementFailure;
        Assert.False(await fixture.Coordinator(state).ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Equal(SourceStatus.Degraded, (await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None))!.Status);
        var published = await fixture.State.GetChunksForSourceAsync(fixture.Source.SourceId, CancellationToken.None);
        if (removedFile) Assert.Empty(published);
        else Assert.NotEmpty(published);
        Assert.DoesNotContain(published, chunk => chunk.ChunkId == fixture.CommittedChunk.ChunkId);
        Assert.All(published, chunk => Assert.Contains(chunk.ChunkId, fixture.Vectors.Ids));
        Assert.Equal(fixture.CommittedChunk.ChunkId, Assert.Single(await fixture.State.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None)).ChunkId);

        Assert.True(await fixture.Coordinator(new SqliteIndexStateStore(fixture.Database, fixture.Options)).ProcessAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.Equal(SourceStatus.Ready, (await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None))!.Status);
        Assert.Empty(await fixture.State.GetPendingVectorWritesAsync(fixture.Source.SourceId, CancellationToken.None));
        Assert.DoesNotContain(fixture.CommittedChunk.ChunkId, fixture.Vectors.Ids);
        Assert.Contains("other-committed", fixture.Vectors.Ids);
        Assert.Equal(published, await fixture.State.GetChunksForSourceAsync(fixture.Source.SourceId, CancellationToken.None));
    }

    private sealed class FailAcknowledgementState(SqliteIndexStateStore inner) : StateWithoutIntents(inner), IVectorWriteIntentStore
    {
        public Task StageVectorWritesAsync(string sourceId, string fileId, IReadOnlyList<string> ids, CancellationToken ct) => Inner.StageVectorWritesAsync(sourceId, fileId, ids, ct);
        public Task<IReadOnlyList<PendingVectorWrite>> GetPendingVectorWritesAsync(string sourceId, CancellationToken ct) => Inner.GetPendingVectorWritesAsync(sourceId, ct);
        public Task ClearPendingVectorWritesAsync(string sourceId, IReadOnlyList<string> ids, CancellationToken ct) => throw new IOException("Injected crash after provider cleanup but before intent acknowledgement.");
    }
    private class StateWithoutIntents(SqliteIndexStateStore inner) : IIndexStateStore
    {
        protected SqliteIndexStateStore Inner { get; } = inner;
        public Task InitializeAsync(CancellationToken ct) => Inner.InitializeAsync(ct);
        public Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken ct) => Inner.GetFileAsync(sourceId, relativePath, ct);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken ct) => Inner.GetChunksForFileAsync(fileId, ct);
        public Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct) => Inner.SaveFileAndChunksAsync(file, chunks, ct);
        public Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken ct) => Inner.DeleteFileAsync(sourceId, relativePath, ct);
        public Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct) => Inner.GetChunkAsync(id, ct);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken ct) => Inner.GetChunksForSourceAsync(sourceId, ct);
    }

    private sealed class RecoveryVectors : IVectorStore
    {
        internal HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        internal List<string[]> Deletes { get; } = [];
        internal int EnsureCalls { get; private set; }
        internal bool FailNextPartialDelete { get; set; }
        internal bool AllowUpsert { get; set; }
        public Task EnsureReadyAsync(CancellationToken ct) { EnsureCalls++; return Task.CompletedTask; }
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken ct)
        {
            if (!AllowUpsert) throw new InvalidOperationException("Committed unchanged sentinel must not be re-embedded/upserted.");
            Ids.UnionWith(documents.Select(document => document.Chunk.ChunkId)); return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            if (ids.Count > 0) Deletes.Add(ids.ToArray());
            if (FailNextPartialDelete)
            {
                FailNextPartialDelete = false;
                Ids.Remove(ids[0]);
                throw new IOException("Injected interruption after only the first provider delete.");
            }
            Ids.ExceptWith(ids); return Task.CompletedTask;
        }
        public Task DeleteSourceAsync(string sourceId, CancellationToken ct) => throw new InvalidOperationException("Recovery must not reset a source.");
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> vector, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class NeverEmbeddings(bool allowPassages = false) : IEmbeddingService
    {
        public string ProfileId => "bge-small-en-v1.5-384";
        public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken ct) => throw new InvalidOperationException("No query inference is expected.");
        public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken ct) => allowPassages ? Task.FromResult<IReadOnlyList<float>>(new float[384]) : throw new InvalidOperationException("Unchanged sentinel must not be re-embedded.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(RepositoryRoot(), "artifacts", "runs", "p7-active-pause-20261017", "journal-contracts", Guid.NewGuid().ToString("N"));
        internal IOptions<LocalRagOptions> Options { get; private set; } = null!;
        internal SqliteDatabase Database { get; private set; } = null!;
        internal SqliteSourceRegistry Sources { get; private set; } = null!;
        internal SqliteIndexStateStore State { get; private set; } = null!;
        internal SourceRecord Source { get; private set; } = null!;
        internal SourceRecord Other { get; private set; } = null!;
        internal ChunkRecord CommittedChunk { get; private set; } = null!;
        internal RecoveryVectors Vectors { get; } = new();
        private SqliteReconciliationStore _reconciliations = null!;
        private SqliteChunkProfileStateStore _profiles = null!;
        private ChunkProfileProvider _profile = null!;
        private SourceWatcherRegistry _watchers = null!;
        private ReconciliationScheduler _scheduler = null!;
        private readonly OperationalMetrics _metrics = new();
        private readonly ContentExtractionService _extraction = new([new PlainTextContentExtractor()]);
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "source")); Directory.CreateDirectory(Path.Combine(fixture.Root, "other"));
            fixture.Options = Microsoft.Extensions.Options.Options.Create(new LocalRagOptions { DataDirectory = Path.Combine(fixture.Root, "data") });
            fixture.Database = new(fixture.Options); fixture.Sources = new(fixture.Database, fixture.Options); fixture.State = new(fixture.Database, fixture.Options);
            fixture._profiles = new(fixture.Database, new ChunkProfileOperationGate()); fixture._reconciliations = new(fixture.Database); fixture._profile = new(fixture.Options);
            await fixture.Sources.InitializeAsync(CancellationToken.None); await fixture.State.InitializeAsync(CancellationToken.None);
            await fixture._profiles.InitializeAsync(CancellationToken.None); await fixture._reconciliations.InitializeAsync(CancellationToken.None);
            fixture.Source = await fixture.Sources.RegisterAsync(Path.Combine(fixture.Root, "source"), "committed sentinel", CancellationToken.None);
            fixture.Other = await fixture.Sources.RegisterAsync(Path.Combine(fixture.Root, "other"), "other source", CancellationToken.None);
            await fixture._profiles.GetOrCreateAsync(fixture.Source.SourceId, fixture._profile.Fingerprint, false, CancellationToken.None);
            const string text = "A committed sentinel remains intact."; var path = Path.Combine(fixture.Source.CanonicalRootPath, "sentinel.txt"); await File.WriteAllTextAsync(path, text);
            var info = new FileInfo(path); var file = new IndexedFile("committed-file", fixture.Source.SourceId, "sentinel.txt", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), info.Length, new(info.LastWriteTimeUtc));
            fixture.CommittedChunk = fixture.Chunk(file, "committed-chunk", text); await fixture.State.SaveFileAndChunksAsync(file, [fixture.CommittedChunk], CancellationToken.None);
            fixture.Vectors.Ids.UnionWith([fixture.CommittedChunk.ChunkId, "other-committed"]);
            fixture._scheduler = new(fixture._reconciliations, new(), fixture._metrics);
            fixture._watchers = new(fixture._scheduler, fixture.Options, NullLogger<SourceWatcherRegistry>.Instance);
            return fixture;
        }
        internal ChunkRecord Chunk(IndexedFile file, string id, string text) => new(id, file.SourceId, file.FileId, file.RelativePath, "text", null, 1, 1, 0, text, file.ContentHash, 8, Options.Value.Embedding.ProfileId, DateTimeOffset.UtcNow, StructuralLocator: "lines:1-1", ChunkProfileFingerprint: _profile.Fingerprint);
        internal FileIndexingService Indexer(IIndexStateStore state) => new(state, new NeverEmbeddings(Vectors.AllowUpsert), new GenericChunker(Options, _profile), Vectors, _extraction, Options, _metrics, _reconciliations);
        internal IndexCoordinator Coordinator(IIndexStateStore state) => new(Sources, state, Vectors, Indexer(state), new(Options, _extraction), _watchers, _scheduler, _reconciliations, new(), _profile, _profiles, NullLogger<IndexCoordinator>.Instance);
        public ValueTask DisposeAsync() { _watchers.Dispose(); ManagerHostIntegrationTests.RecordManifest(Root); return ValueTask.CompletedTask; }
        private static string RepositoryRoot() { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path is not null && !File.Exists(Path.Combine(path.FullName, "LocalRag.sln"))) path = path.Parent; return path!.FullName; }
    }
}
