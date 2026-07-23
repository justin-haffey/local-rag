using LocalRag.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Sqlite;

public sealed class SqliteDatabase(IOptions<LocalRagOptions> options)
{
    private const int MaxBusyReplayAttempts = 3;
    private static readonly TimeSpan BusyReplayBaseDelay = TimeSpan.FromMilliseconds(25);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(Expand(options.Value.DataDirectory), "localrag.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        DefaultTimeout = 5
    }.ToString();

    public string DataDirectory { get; } = Expand(options.Value.DataDirectory);

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataDirectory);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>Acquires an immediate write transaction with bounded replay for SQLite writer contention.</summary>
    public async Task<SqliteTransaction> BeginImmediateTransactionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return connection.BeginTransaction(deferred: false);
            }
            catch (SqliteException exception) when (
                IsBusy(exception) && attempt < MaxBusyReplayAttempts)
            {
                await Task.Delay(BusyReplayBaseDelay * attempt, cancellationToken);
            }
        }
    }

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6 || exception.SqliteExtendedErrorCode is 262 or 517;

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}
