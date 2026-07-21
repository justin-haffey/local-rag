using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;

namespace LocalRag.Infrastructure.Sqlite;

/// <summary>Owns the durable, source-level cutover between incompatible chunk profiles.</summary>
public sealed class SqliteChunkProfileStateStore : IChunkProfileStateStore
{
    private readonly SqliteDatabase _database;
    private readonly IChunkProfileOperationGate _gate;

    public SqliteChunkProfileStateStore(SqliteDatabase database)
        : this(database, new ChunkProfileOperationGate())
    {
    }

    public SqliteChunkProfileStateStore(SqliteDatabase database, IChunkProfileOperationGate gate)
    {
        _database = database;
        _gate = gate;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SourceChunkProfiles (
              SourceId TEXT PRIMARY KEY REFERENCES Sources(SourceId) ON DELETE CASCADE,
              ActiveFingerprint TEXT NOT NULL,
              PendingFingerprint TEXT NULL,
              State TEXT NOT NULL,
              RequestedUtc TEXT NOT NULL,
              CompletedUtc TEXT NULL,
              LastError TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ChunkProfileState> GetOrCreateAsync(
        string sourceId,
        string configuredFingerprint,
        bool hasIndexedChunks,
        CancellationToken cancellationToken)
    {
        await using var lease = await _gate.AcquireAsync([sourceId], cancellationToken);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO SourceChunkProfiles(
              SourceId, ActiveFingerprint, PendingFingerprint, State, RequestedUtc, CompletedUtc, LastError)
            VALUES($source, $active, NULL, 'Ready', $now, $now, NULL);
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$active", hasIndexedChunks ? "legacy-generic-1" : configuredFingerprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetAsync(sourceId, cancellationToken)
            ?? throw new InvalidOperationException($"Chunk profile state could not be created for source '{sourceId}'.");
    }

    public async Task<ChunkProfileState?> GetAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceId, ActiveFingerprint, PendingFingerprint, State, RequestedUtc, CompletedUtc, LastError FROM SourceChunkProfiles WHERE SourceId = $source;";
        command.Parameters.AddWithValue("$source", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ChunkProfileState(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<ChunkProfileStatus>(reader.GetString(3), ignoreCase: false),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task BeginTransitionAsync(string sourceId, string targetFingerprint, CancellationToken cancellationToken)
    {
        await using var lease = await _gate.AcquireAsync([sourceId], cancellationToken);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SourceChunkProfiles
            SET PendingFingerprint = $target, State = 'Reindexing', RequestedUtc = $now,
                CompletedUtc = NULL, LastError = NULL
            WHERE SourceId = $source AND (ActiveFingerprint <> $target OR State <> 'Ready');
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$target", targetFingerprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteTransitionAsync(string sourceId, string targetFingerprint, CancellationToken cancellationToken)
    {
        await using var lease = await _gate.AcquireAsync([sourceId], cancellationToken);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SourceChunkProfiles
            SET ActiveFingerprint = $target, PendingFingerprint = NULL, State = 'Ready',
                CompletedUtc = $now, LastError = NULL
            WHERE SourceId = $source AND PendingFingerprint = $target AND State = 'Reindexing';
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$target", targetFingerprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Chunk profile transition for source '{sourceId}' is not active for the requested target.");
        }
    }

    public async Task FailTransitionAsync(
        string sourceId,
        string targetFingerprint,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var lease = await _gate.AcquireAsync([sourceId], cancellationToken);
        await using var connection = await _database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SourceChunkProfiles
            SET PendingFingerprint = $target, State = 'Failed', CompletedUtc = $now, LastError = $error
            WHERE SourceId = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$target", targetFingerprint);
        command.Parameters.AddWithValue("$error", failureMessage);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsQueryVisibleAsync(string sourceId, CancellationToken cancellationToken)
    {
        var state = await GetAsync(sourceId, cancellationToken);
        return state is { Status: ChunkProfileStatus.Ready, PendingFingerprint: null };
    }
}
