using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Sqlite;

public sealed class SqliteSourceRegistry(SqliteDatabase database, IOptions<LocalRagOptions> options) : ISourceRegistry
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Sources (
              SourceId TEXT PRIMARY KEY,
              CanonicalRootPath TEXT NOT NULL UNIQUE,
              DisplayName TEXT NOT NULL,
              Status TEXT NOT NULL,
              CreatedUtc TEXT NOT NULL,
              UpdatedUtc TEXT NOT NULL,
              LastScanUtc TEXT NULL,
              LastSuccessfulIndexUtc TEXT NULL,
              EmbeddingProfileId TEXT NOT NULL,
              LastError TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS SchemaVersions (
              Name TEXT PRIMARY KEY,
              Version INTEGER NOT NULL,
              AppliedUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO SchemaVersions(Name, Version, AppliedUtc)
              VALUES ('sqlite', 1, $now);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SourceRecord> RegisterAsync(string rootPath, string? displayName, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"RAG source root does not exist: {rootPath}");
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var source = new SourceRecord(
            SourceId: Guid.NewGuid().ToString("N"),
            CanonicalRootPath: canonical,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(canonical) : displayName.Trim(),
            Status: SourceStatus.Pending,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastScanUtc: null,
            LastSuccessfulIndexUtc: null,
            EmbeddingProfileId: options.Value.Embedding.ProfileId);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var overlap = connection.CreateCommand();
        overlap.CommandText = "SELECT CanonicalRootPath FROM Sources;";
        {
            await using var reader = await overlap.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var existing = reader.GetString(0);
                if (IsOverlappingRoot(existing, canonical))
                {
                    throw new InvalidOperationException("A registered RAG source already contains or is contained by this root.");
                }
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Sources(SourceId, CanonicalRootPath, DisplayName, Status, CreatedUtc, UpdatedUtc, EmbeddingProfileId)
            VALUES ($id, $root, $name, $status, $created, $updated, $profile);
            """;
        AddSourceParameters(insert, source);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return source;
    }

    public async Task<IReadOnlyList<SourceRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var results = new List<SourceRecord>();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceId, CanonicalRootPath, DisplayName, Status, CreatedUtc, UpdatedUtc, LastScanUtc, LastSuccessfulIndexUtc, EmbeddingProfileId, LastError FROM Sources ORDER BY DisplayName;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSource(reader));
        }

        return results;
    }

    public async Task<SourceRecord?> GetAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceId, CanonicalRootPath, DisplayName, Status, CreatedUtc, UpdatedUtc, LastScanUtc, LastSuccessfulIndexUtc, EmbeddingProfileId, LastError FROM Sources WHERE SourceId = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSource(reader) : null;
    }

    public async Task RemoveAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Sources WHERE SourceId = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetStatusAsync(string sourceId, SourceStatus status, string? failureMessage, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sources SET
                Status = $status,
                UpdatedUtc = $updated,
                LastScanUtc = CASE WHEN $status IN ('Indexing', 'Ready', 'Degraded') THEN $updated ELSE LastScanUtc END,
                LastSuccessfulIndexUtc = CASE WHEN $status = 'Ready' THEN $updated ELSE LastSuccessfulIndexUtc END,
                LastError = $error
            WHERE SourceId = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)failureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSourceParameters(SqliteCommand command, SourceRecord source)
    {
        command.Parameters.AddWithValue("$id", source.SourceId);
        command.Parameters.AddWithValue("$root", source.CanonicalRootPath);
        command.Parameters.AddWithValue("$name", source.DisplayName);
        command.Parameters.AddWithValue("$status", source.Status.ToString());
        command.Parameters.AddWithValue("$created", source.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", source.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$profile", source.EmbeddingProfileId);
    }

    private static SourceRecord ReadSource(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), Enum.Parse<SourceStatus>(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    private static bool IsOverlappingRoot(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
