using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Sqlite;

public sealed class SqliteIndexStateStore(SqliteDatabase database, IOptions<LocalRagOptions> options) : IIndexStateStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Files (
              FileId TEXT PRIMARY KEY,
              SourceId TEXT NOT NULL REFERENCES Sources(SourceId) ON DELETE CASCADE,
              RelativePath TEXT NOT NULL,
              ContentHash TEXT NOT NULL,
              SizeBytes INTEGER NOT NULL,
              LastModifiedUtc TEXT NOT NULL,
              UNIQUE(SourceId, RelativePath)
            );
            CREATE TABLE IF NOT EXISTS Chunks (
              ChunkId TEXT PRIMARY KEY,
              SourceId TEXT NOT NULL REFERENCES Sources(SourceId) ON DELETE CASCADE,
              FileId TEXT NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
              RelativePath TEXT NOT NULL,
              Language TEXT NOT NULL,
              SymbolName TEXT NULL,
              ChunkKind TEXT NOT NULL DEFAULT 'text',
              QualifiedSymbolName TEXT NULL,
              StructuralLocator TEXT NOT NULL DEFAULT '',
              ChunkerId TEXT NOT NULL DEFAULT 'generic',
              ChunkerVersion TEXT NOT NULL DEFAULT '1',
              ChunkProfileFingerprint TEXT NOT NULL DEFAULT 'legacy-generic-1',
              StartLine INTEGER NOT NULL,
              EndLine INTEGER NOT NULL,
              Ordinal INTEGER NOT NULL,
              Content TEXT NOT NULL,
              ContentHash TEXT NOT NULL,
              TokenCount INTEGER NOT NULL,
              EmbeddingProfileId TEXT NOT NULL,
              LastIndexedUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Files_Source_Path ON Files(SourceId, RelativePath);
            CREATE INDEX IF NOT EXISTS IX_Chunks_File ON Chunks(FileId);
            CREATE INDEX IF NOT EXISTS IX_Chunks_Source ON Chunks(SourceId);
            CREATE TABLE IF NOT EXISTS EmbeddingProfiles (
              ProfileId TEXT PRIMARY KEY,
              Provider TEXT NOT NULL,
              ModelName TEXT NOT NULL,
              Dimensions INTEGER NOT NULL,
              DistanceMetric TEXT NOT NULL,
              CreatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS IndexJobs (
              JobId TEXT PRIMARY KEY,
              SourceId TEXT NOT NULL REFERENCES Sources(SourceId) ON DELETE CASCADE,
              Status TEXT NOT NULL,
              RequestedUtc TEXT NOT NULL,
              StartedUtc TEXT NULL,
              CompletedUtc TEXT NULL,
              Error TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS IndexCheckpoints (
              SourceId TEXT PRIMARY KEY REFERENCES Sources(SourceId) ON DELETE CASCADE,
              LastReconciledUtc TEXT NULL,
              LastCompletedJobId TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS DeadLetterJobs (
              JobId TEXT PRIMARY KEY,
              SourceId TEXT NOT NULL,
              Error TEXT NOT NULL,
              FailedUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EmbeddingProfiles(ProfileId, Provider, ModelName, Dimensions, DistanceMetric, CreatedUtc)
              VALUES ($profileId, 'onnx', 'BAAI/bge-small-en-v1.5', $dimensions, 'cosine', $now);
            """;
        command.Parameters.AddWithValue("$profileId", options.Value.Embedding.ProfileId);
        command.Parameters.AddWithValue("$dimensions", options.Value.Embedding.Dimensions);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MigrateChunkProvenanceAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FileId, SourceId, RelativePath, ContentHash, SizeBytes, LastModifiedUtc FROM Files WHERE SourceId = $sourceId AND RelativePath = $path;";
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$path", relativePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken)
    {
        var chunks = new List<ChunkRecord>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = CreateChunkSelect(connection, "FileId = $id");
        command.Parameters.AddWithValue("$id", fileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) chunks.Add(ReadChunk(reader));
        return chunks;
    }

    public async Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        ChunkRecordValidation.Validate(
            file,
            chunks,
            Math.Min(options.Value.Chunking.MaximumTokens, options.Value.Embedding.MaximumTokens));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var fileCommand = connection.CreateCommand())
        {
            fileCommand.Transaction = transaction;
            fileCommand.CommandText = """
                INSERT INTO Files(FileId, SourceId, RelativePath, ContentHash, SizeBytes, LastModifiedUtc)
                VALUES($id, $sourceId, $path, $hash, $size, $modified)
                ON CONFLICT(SourceId, RelativePath) DO UPDATE SET
                    FileId = excluded.FileId, ContentHash = excluded.ContentHash,
                    SizeBytes = excluded.SizeBytes, LastModifiedUtc = excluded.LastModifiedUtc;
                """;
            fileCommand.Parameters.AddWithValue("$id", file.FileId);
            fileCommand.Parameters.AddWithValue("$sourceId", file.SourceId);
            fileCommand.Parameters.AddWithValue("$path", file.RelativePath);
            fileCommand.Parameters.AddWithValue("$hash", file.ContentHash);
            fileCommand.Parameters.AddWithValue("$size", file.SizeBytes);
            fileCommand.Parameters.AddWithValue("$modified", file.LastModifiedUtc.ToString("O"));
            await fileCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM Chunks WHERE FileId = $fileId;";
            deleteCommand.Parameters.AddWithValue("$fileId", file.FileId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in chunks)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Chunks(ChunkId, SourceId, FileId, RelativePath, Language, SymbolName, ChunkKind, QualifiedSymbolName, StructuralLocator, ChunkerId, ChunkerVersion, ChunkProfileFingerprint, StartLine, EndLine, Ordinal, Content, ContentHash, TokenCount, EmbeddingProfileId, LastIndexedUtc)
                VALUES($id, $source, $file, $path, $language, $symbol, $chunkKind, $qualifiedSymbol, $locator, $chunkerId, $chunkerVersion, $chunkProfileFingerprint, $start, $end, $ordinal, $content, $hash, $tokens, $profile, $indexed);
                """;
            AddChunkParameters(insert, chunk);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Files WHERE SourceId = $source AND RelativePath = $path;";
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$path", relativePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = CreateChunkSelect(connection, "ChunkId = $id");
        command.Parameters.AddWithValue("$id", chunkId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadChunk(reader) : null;
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        var chunks = new List<ChunkRecord>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = CreateChunkSelect(connection, "SourceId = $id");
        command.Parameters.AddWithValue("$id", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) chunks.Add(ReadChunk(reader));
        return chunks;
    }

    private static SqliteCommand CreateChunkSelect(SqliteConnection connection, string predicate)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT ChunkId, SourceId, FileId, RelativePath, Language, SymbolName, ChunkKind, QualifiedSymbolName, StructuralLocator, ChunkerId, ChunkerVersion, ChunkProfileFingerprint, StartLine, EndLine, Ordinal, Content, ContentHash, TokenCount, EmbeddingProfileId, LastIndexedUtc FROM Chunks WHERE {predicate} ORDER BY RelativePath, Ordinal;";
        return command;
    }

    private static void AddChunkParameters(SqliteCommand command, ChunkRecord chunk)
    {
        command.Parameters.AddWithValue("$id", chunk.ChunkId);
        command.Parameters.AddWithValue("$source", chunk.SourceId);
        command.Parameters.AddWithValue("$file", chunk.FileId);
        command.Parameters.AddWithValue("$path", chunk.RelativePath);
        command.Parameters.AddWithValue("$language", chunk.Language);
        command.Parameters.AddWithValue("$symbol", (object?)chunk.SymbolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$chunkKind", chunk.ChunkKind);
        command.Parameters.AddWithValue("$qualifiedSymbol", (object?)chunk.QualifiedSymbolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$locator", chunk.StructuralLocator);
        command.Parameters.AddWithValue("$chunkerId", chunk.ChunkerId);
        command.Parameters.AddWithValue("$chunkerVersion", chunk.ChunkerVersion);
        command.Parameters.AddWithValue("$chunkProfileFingerprint", chunk.ChunkProfileFingerprint);
        command.Parameters.AddWithValue("$start", chunk.StartLine);
        command.Parameters.AddWithValue("$end", chunk.EndLine);
        command.Parameters.AddWithValue("$ordinal", chunk.Ordinal);
        command.Parameters.AddWithValue("$content", chunk.Content);
        command.Parameters.AddWithValue("$hash", chunk.ContentHash);
        command.Parameters.AddWithValue("$tokens", chunk.TokenCount);
        command.Parameters.AddWithValue("$profile", chunk.EmbeddingProfileId);
        command.Parameters.AddWithValue("$indexed", chunk.LastIndexedUtc.ToString("O"));
    }

    private static IndexedFile ReadFile(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));

    private static ChunkRecord ReadChunk(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14),
        reader.GetString(15), reader.GetString(16), reader.GetInt32(17), reader.GetString(18), DateTimeOffset.Parse(reader.GetString(19), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11));

    private static async Task MigrateChunkProvenanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var tableInfo = connection.CreateCommand())
        {
            tableInfo.Transaction = transaction;
            tableInfo.CommandText = "PRAGMA table_info(Chunks);";
            await using var reader = await tableInfo.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var migrations = new (string Name, string Definition)[]
        {
            ("ChunkKind", "TEXT NOT NULL DEFAULT 'text'"),
            ("QualifiedSymbolName", "TEXT NULL"),
            ("StructuralLocator", "TEXT NOT NULL DEFAULT ''"),
            ("ChunkerId", "TEXT NOT NULL DEFAULT 'generic'"),
            ("ChunkerVersion", "TEXT NOT NULL DEFAULT '1'"),
            ("ChunkProfileFingerprint", "TEXT NOT NULL DEFAULT 'legacy-generic-1'")
        };

        foreach (var (name, definition) in migrations)
        {
            if (columns.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE Chunks ADD COLUMN {name} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var backfill = connection.CreateCommand();
        backfill.Transaction = transaction;
        backfill.CommandText = "UPDATE Chunks SET StructuralLocator = 'lines:' || StartLine || '-' || EndLine WHERE StructuralLocator = '';";
        await backfill.ExecuteNonQueryAsync(cancellationToken);
    }
}
