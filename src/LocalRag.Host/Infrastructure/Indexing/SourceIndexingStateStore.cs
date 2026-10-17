using LocalRag.Infrastructure.Sqlite;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Desired indexing state survives transient runs, host restarts and interrupted pauses.</summary>
public sealed record SourceIndexingState(string SourceId, string DesiredState, string State, DateTimeOffset? UpdatedUtc)
{
    public bool AllowsWork => DesiredState == "Running";
    public bool QueryVisible => State == "Running";
}

public sealed class SourceIndexingStateStore(SqliteDatabase database)
{
    // Deliberately initialized per connection: a confirmed reset replaces the SQLite database in-process.
    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS source_indexing_state(source_id TEXT PRIMARY KEY, desired_state TEXT NOT NULL, state TEXT NOT NULL, updated_utc TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task<SourceIndexingState> GetAsync(string sourceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT desired_state,state,updated_utc FROM source_indexing_state WHERE source_id=$id";
        command.Parameters.AddWithValue("$id", sourceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(sourceId, reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture))
            : new(sourceId, "Running", "Running", null);
    }

    public async Task SetAsync(string sourceId, string desiredState, string state, CancellationToken ct)
    {
        if (desiredState is not ("Running" or "Paused") || state is not ("Running" or "Pausing" or "Paused" or "Resuming"))
            throw new ArgumentException("Unsupported indexing state.");
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO source_indexing_state VALUES($id,$desired,$state,$time) ON CONFLICT(source_id) DO UPDATE SET desired_state=$desired,state=$state,updated_utc=$time";
        command.Parameters.AddWithValue("$id", sourceId);
        command.Parameters.AddWithValue("$desired", desiredState);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(string sourceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM source_indexing_state WHERE source_id=$id";
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteResumeAsync(string sourceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE source_indexing_state SET state='Running',updated_utc=$time WHERE source_id=$id AND desired_state='Running' AND state='Resuming'";
        command.Parameters.AddWithValue("$id", sourceId);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
