using System.Security.Cryptography;
using System.Text.Json;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class LanceDbRollbackIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [NativeIntegrationFact]
    [Trait("Category", "NativeIntegration")]
    public async Task QuiescedCheckpointRestoresMatchingSqliteVectorsAndOwnership()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!;
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires a manifest.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOCALRAG_NATIVE_RUN_DIRECTORY")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "runs", "native-rollback"), Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "live");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "rollback.txt"), "A coherent rollback retains matching operational and vector state.");
        var nativeOptions = new LanceDbOptions { Path = Path.Combine(live, "vectors"), Table = "rag_chunks_v1", BatchSize = 100 };
        var options = Options.Create(new LocalRagOptions
        {
            DataDirectory = Path.Combine(live, "state"),
            VectorStore = new VectorStoreOptions { Provider = VectorStoreProvider.LanceDb, LanceDb = nativeOptions }
        });
        var database = new SqliteDatabase(options);
        var sources = new SqliteSourceRegistry(database, options);
        var state = new SqliteIndexStateStore(database, options);
        await sources.InitializeAsync(CancellationToken.None);
        await state.InitializeAsync(CancellationToken.None);
        var identity = new SqliteVectorStoreIdentityStore(database);
        await identity.InitializeAsync(CancellationToken.None);
        var fingerprint = SqliteVectorStoreIdentityStore.CreateConfiguredIdentity(options.Value);
        await identity.EnforceAsync(fingerprint, CancellationToken.None);
        var source = await sources.RegisterAsync(sourceRoot, "rollback fixture", CancellationToken.None);
        var file = new IndexedFile("file-rollback", source.SourceId, "rollback.txt", "hash-rollback", 72, DateTimeOffset.UnixEpoch);
        var chunk = new ChunkRecord("chunk-rollback", source.SourceId, file.FileId, file.RelativePath, "text", null,
            1, 1, 0, "A coherent rollback retains matching operational and vector state.", file.ContentHash, 12,
            source.EmbeddingProfileId, DateTimeOffset.UnixEpoch, StructuralLocator: "line:1");
        var vector = new float[384];
        vector[0] = 1;
        string ownerId;
        using (var owner = new InstallationOwnershipStore(options))
        using (var store = new LanceDbVectorStore(nativeOptions, owner, new LanceDbIpcCodec(), () => new LanceDbNativeApi(manifest)))
        {
            ownerId = await owner.GetOrCreateAsync(CancellationToken.None);
            await store.UpsertAsync([new VectorDocument(chunk, vector)], CancellationToken.None);
            await state.SaveFileAndChunksAsync(file, [chunk], CancellationToken.None);
        }
        // No watcher, queue or host is running in this fixture. Every provider
        // operation has drained and native handles/writer lock are now closed.
        await using (var connection = await database.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
        }
        SqliteConnection.ClearAllPools();
        var snapshot = Path.Combine(root, "quiesced-checkpoint");
        CopyTree(live, snapshot);
        var files = Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(snapshot, path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "rollback-checkpoint.json"), JsonSerializer.Serialize(new
        {
            capturedUtc = DateTimeOffset.UtcNow,
            sourceId = source.SourceId,
            chunkId = chunk.ChunkId,
            ownershipId = ownerId,
            methodology = "All native writers disposed; SQLite WAL checkpoint busy=0; all connections and pools closed; complete SQLite/vector/ownership tree copied together",
            files
        }, JsonOptions));
        using (var owner = new InstallationOwnershipStore(options))
        using (var store = new LanceDbVectorStore(nativeOptions, owner, new LanceDbIpcCodec(), () => new LanceDbNativeApi(manifest)))
        {
            await store.DeleteSourceAsync(source.SourceId, CancellationToken.None);
            await state.DeleteFileAsync(source.SourceId, file.RelativePath, CancellationToken.None);
        }
        Assert.Null(await state.GetChunkAsync(chunk.ChunkId, CancellationToken.None));
        SqliteConnection.ClearAllPools();
        // Retain the mutated bundle instead of deleting individual WAL/table
        // files. Restore only after all writers have quiesced again.
        Directory.Move(live, Path.Combine(root, "mutated-state-retained"));
        CopyTree(snapshot, live);
        var restored = Assert.IsType<ChunkRecord>(await state.GetChunkAsync(chunk.ChunkId, CancellationToken.None));
        Assert.Equal(chunk.Content, restored.Content);
        Assert.Equal(source.SourceId, restored.SourceId);
        Assert.Equal(fingerprint.Fingerprint, (await identity.GetAsync(CancellationToken.None))?.Fingerprint);
        using (var owner = new InstallationOwnershipStore(options))
        using (var store = new LanceDbVectorStore(nativeOptions, owner, new LanceDbIpcCodec(), () => new LanceDbNativeApi(manifest)))
        {
            Assert.Equal(ownerId, await owner.GetOrCreateAsync(CancellationToken.None));
            Assert.True(await store.VerifyOwnershipAsync(ownerId, CancellationToken.None));
            var found = Assert.Single(await store.SearchAsync(new SearchRequest("coherent rollback", [source.SourceId], 5), vector, CancellationToken.None));
            Assert.Equal(restored.ChunkId, found.ChunkId);
            Assert.Equal(restored.ContentHash, found.ContentHash);
        }
        Assert.True(File.Exists(Path.Combine(sourceRoot, "rollback.txt")));
        SqliteConnection.ClearAllPools();
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
    }
}
