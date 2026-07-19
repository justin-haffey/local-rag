using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace LocalRag.Infrastructure.Indexing;

public sealed record IndexJob(string JobId, string SourceId, int Attempt);

/// <summary>Persists source-scoped indexing work so retries and restarts cannot lose a requested scan.</summary>
public sealed class IndexJobStore(SqliteDatabase database)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await AddColumnIfMissingAsync(connection, "ALTER TABLE IndexJobs ADD COLUMN Attempt INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        await AddColumnIfMissingAsync(connection, "ALTER TABLE IndexJobs ADD COLUMN NextAttemptUtc TEXT NULL;", cancellationToken);
    }

    public async Task QueueAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT JobId FROM IndexJobs WHERE SourceId = $source AND Status IN ('Pending', 'Processing') ORDER BY RequestedUtc DESC LIMIT 1;";
        existing.Parameters.AddWithValue("$source", sourceId);
        if (await existing.ExecuteScalarAsync(cancellationToken) is not null) return;

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO IndexJobs(JobId, SourceId, Status, RequestedUtc, Attempt) VALUES($id, $source, 'Pending', $now, 0);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexJob?> LeaseAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT JobId, Attempt FROM IndexJobs WHERE SourceId = $source AND Status = 'Pending' AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= $now) ORDER BY RequestedUtc LIMIT 1;";
        find.Parameters.AddWithValue("$source", sourceId);
        find.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await find.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var job = new IndexJob(reader.GetString(0), sourceId, reader.GetInt32(1));
        await reader.DisposeAsync();
        await using var lease = connection.CreateCommand();
        lease.Transaction = transaction;
        lease.CommandText = "UPDATE IndexJobs SET Status = 'Processing', StartedUtc = $now WHERE JobId = $id;";
        lease.Parameters.AddWithValue("$id", job.JobId);
        lease.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await lease.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task CompleteAsync(IndexJob job, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE IndexJobs SET Status = 'Completed', CompletedUtc = $now, Error = NULL WHERE JobId = $id; INSERT INTO IndexCheckpoints(SourceId, LastReconciledUtc, LastCompletedJobId) VALUES($source, $now, $id) ON CONFLICT(SourceId) DO UPDATE SET LastReconciledUtc = excluded.LastReconciledUtc, LastCompletedJobId = excluded.LastCompletedJobId;";
        command.Parameters.AddWithValue("$id", job.JobId);
        command.Parameters.AddWithValue("$source", job.SourceId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RetryOrFailAsync(IndexJob job, Exception exception, int maxAttempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var attempt = job.Attempt + 1;
        if (attempt >= maxAttempts)
        {
            await using var failed = connection.CreateCommand();
            failed.CommandText = "UPDATE IndexJobs SET Status = 'Failed', CompletedUtc = $now, Error = $error, Attempt = $attempt WHERE JobId = $id; INSERT OR REPLACE INTO DeadLetterJobs(JobId, SourceId, Error, FailedUtc) VALUES($id, $source, $error, $now);";
            AddFailureParameters(failed, job, exception, attempt, null);
            await failed.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var retry = connection.CreateCommand();
        retry.CommandText = "UPDATE IndexJobs SET Status = 'Pending', Error = $error, Attempt = $attempt, NextAttemptUtc = $next WHERE JobId = $id;";
        AddFailureParameters(retry, job, exception, attempt, DateTimeOffset.UtcNow.Add(delay));
        await retry.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var reset = connection.CreateCommand();
        reset.CommandText = "UPDATE IndexJobs SET Status = 'Pending', Error = COALESCE(Error, 'Recovered after host restart.') WHERE Status = 'Processing';";
        await reset.ExecuteNonQueryAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT SourceId FROM IndexJobs WHERE Status = 'Pending';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sources = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) sources.Add(reader.GetString(0));
        return sources;
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
    }

    private static void AddFailureParameters(SqliteCommand command, IndexJob job, Exception exception, int attempt, DateTimeOffset? next)
    {
        command.Parameters.AddWithValue("$id", job.JobId);
        command.Parameters.AddWithValue("$source", job.SourceId);
        command.Parameters.AddWithValue("$error", exception.Message);
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (next is not null) command.Parameters.AddWithValue("$next", next.Value.ToString("O"));
    }
}
