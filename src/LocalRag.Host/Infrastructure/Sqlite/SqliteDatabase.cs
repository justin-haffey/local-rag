using LocalRag.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Sqlite;

public sealed class SqliteDatabase(IOptions<LocalRagOptions> options)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(Expand(options.Value.DataDirectory), "localrag.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public string DataDirectory { get; } = Expand(options.Value.DataDirectory);

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataDirectory);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}
