using LocalRag.Configuration;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class SqliteSourceRegistryTests
{
    [Fact]
    public async Task RemoveDeletesSourceAndNonCascadingDeadLetterRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-rag-remove-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var options = Options.Create(new LocalRagOptions { DataDirectory = root });
            var database = new SqliteDatabase(options);
            var registry = new SqliteSourceRegistry(database, options);
            var indexState = new SqliteIndexStateStore(database, options);
            await registry.InitializeAsync(CancellationToken.None);
            await indexState.InitializeAsync(CancellationToken.None);
            var source = await registry.RegisterAsync(sourceRoot, null, CancellationToken.None);

            await using (var connection = await database.OpenAsync(CancellationToken.None))
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO DeadLetterJobs(JobId, SourceId, Error, FailedUtc) VALUES ('job', $source, 'failure', $now);";
                insert.Parameters.AddWithValue("$source", source.SourceId);
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            await registry.RemoveAsync(source.SourceId, CancellationToken.None);

            Assert.Null(await registry.GetAsync(source.SourceId, CancellationToken.None));
            await using var verify = await database.OpenAsync(CancellationToken.None);
            await using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM DeadLetterJobs WHERE SourceId = $source;";
            count.Parameters.AddWithValue("$source", source.SourceId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
