using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class SqliteIndexStateProvenanceTests
{
    [Fact]
    public async Task InitializeMigratesLegacyChunksAndBackfillsProvenanceIdempotently()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var options = Options.Create(new LocalRagOptions { DataDirectory = root });
            var database = new SqliteDatabase(options);
            var registry = new SqliteSourceRegistry(database, options);
            await registry.InitializeAsync(CancellationToken.None);
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
            var source = await registry.RegisterAsync(sourceRoot, null, CancellationToken.None);

            await SeedLegacyChunkAsync(database, source.SourceId);

            var store = new SqliteIndexStateStore(database, options);
            await store.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            var chunk = await store.GetChunkAsync("legacy-chunk", CancellationToken.None);
            Assert.NotNull(chunk);
            Assert.Equal("text", chunk.ChunkKind);
            Assert.Null(chunk.QualifiedSymbolName);
            Assert.Equal("lines:4-9", chunk.StructuralLocator);
            Assert.Equal("generic", chunk.ChunkerId);
            Assert.Equal("1", chunk.ChunkerVersion);
            Assert.Equal("legacy-generic-1", chunk.ChunkProfileFingerprint);

            await using var connection = await database.OpenAsync(CancellationToken.None);
            await using var tableInfo = connection.CreateCommand();
            tableInfo.CommandText = "PRAGMA table_info(Chunks);";
            await using var reader = await tableInfo.ExecuteReaderAsync();
            var provenanceColumns = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                if (name is "ChunkKind" or "QualifiedSymbolName" or "StructuralLocator" or "ChunkerId" or "ChunkerVersion" or "ChunkProfileFingerprint")
                {
                    provenanceColumns.Add(name);
                }
            }

            Assert.Equal(6, provenanceColumns.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndReadRoundTripsAllChunkProvenance()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "source"));
        try
        {
            var options = Options.Create(new LocalRagOptions { DataDirectory = root });
            var database = new SqliteDatabase(options);
            var registry = new SqliteSourceRegistry(database, options);
            var store = new SqliteIndexStateStore(database, options);
            await registry.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            var source = await registry.RegisterAsync(Path.Combine(root, "source"), null, CancellationToken.None);

            var file = new IndexedFile("file-1", source.SourceId, "src/Widget.cs", "file-hash", 42, DateTimeOffset.Parse("2026-07-21T12:00:00Z", CultureInfo.InvariantCulture));
            var chunk = new ChunkRecord(
                "chunk-1", source.SourceId, file.FileId, file.RelativePath, "csharp", "Run", 5, 12, 0,
                "void Run() { }", "chunk-hash", 7, "bge-small-en-v1.5", DateTimeOffset.Parse("2026-07-21T12:01:00Z", CultureInfo.InvariantCulture),
                "member", "Example.Widget.Run", "type:Example.Widget/member:Run", "csharp-lexical", "2.0.0", "abc123");

            await store.SaveFileAndChunksAsync(file, [chunk], CancellationToken.None);

            Assert.Equal(chunk, await store.GetChunkAsync(chunk.ChunkId, CancellationToken.None));
            Assert.Equal([chunk], await store.GetChunksForFileAsync(file.FileId, CancellationToken.None));
            Assert.Equal([chunk], await store.GetChunksForSourceAsync(source.SourceId, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveRejectsInvalidProvenanceBeforeOpeningATransaction()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "source"));
        try
        {
            var options = Options.Create(new LocalRagOptions { DataDirectory = root });
            var database = new SqliteDatabase(options);
            var registry = new SqliteSourceRegistry(database, options);
            var store = new SqliteIndexStateStore(database, options);
            await registry.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            var source = await registry.RegisterAsync(Path.Combine(root, "source"), null, CancellationToken.None);
            var file = new IndexedFile("file-invalid", source.SourceId, "src/Invalid.cs", "file-hash", 42, DateTimeOffset.UtcNow);
            var chunk = new ChunkRecord(
                "chunk-invalid", source.SourceId, file.FileId, file.RelativePath, "csharp", "Run", 1, 1, 0,
                "void Run() { }", "chunk-hash", 7, "bge-small-en-v1.5", DateTimeOffset.UtcNow,
                "member", "Invalid.Run", string.Empty, "csharp-lexical", "2.0.0", "abc123");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveFileAndChunksAsync(file, [chunk], CancellationToken.None));

            Assert.Null(await store.GetFileAsync(source.SourceId, file.RelativePath, CancellationToken.None));
            Assert.Null(await store.GetChunkAsync(chunk.ChunkId, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedLegacyChunkAsync(SqliteDatabase database, string sourceId)
    {
        await using var connection = await database.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Files (
              FileId TEXT PRIMARY KEY,
              SourceId TEXT NOT NULL REFERENCES Sources(SourceId) ON DELETE CASCADE,
              RelativePath TEXT NOT NULL,
              ContentHash TEXT NOT NULL,
              SizeBytes INTEGER NOT NULL,
              LastModifiedUtc TEXT NOT NULL,
              UNIQUE(SourceId, RelativePath)
            );
            CREATE TABLE Chunks (
              ChunkId TEXT PRIMARY KEY,
              SourceId TEXT NOT NULL REFERENCES Sources(SourceId) ON DELETE CASCADE,
              FileId TEXT NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
              RelativePath TEXT NOT NULL,
              Language TEXT NOT NULL,
              SymbolName TEXT NULL,
              StartLine INTEGER NOT NULL,
              EndLine INTEGER NOT NULL,
              Ordinal INTEGER NOT NULL,
              Content TEXT NOT NULL,
              ContentHash TEXT NOT NULL,
              TokenCount INTEGER NOT NULL,
              EmbeddingProfileId TEXT NOT NULL,
              LastIndexedUtc TEXT NOT NULL
            );
            INSERT INTO Files(FileId, SourceId, RelativePath, ContentHash, SizeBytes, LastModifiedUtc)
              VALUES('legacy-file', $source, 'legacy.cs', 'file-hash', 10, '2026-07-21T12:00:00Z');
            INSERT INTO Chunks(ChunkId, SourceId, FileId, RelativePath, Language, SymbolName, StartLine, EndLine, Ordinal, Content, ContentHash, TokenCount, EmbeddingProfileId, LastIndexedUtc)
              VALUES('legacy-chunk', $source, 'legacy-file', 'legacy.cs', 'csharp', NULL, 4, 9, 0, 'legacy content', 'chunk-hash', 3, 'legacy-profile', '2026-07-21T12:01:00Z');
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-rag-provenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
