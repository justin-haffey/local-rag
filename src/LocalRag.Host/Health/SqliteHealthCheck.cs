using LocalRag.Infrastructure.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalRag.Health;

public sealed class SqliteHealthCheck(SqliteDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("SQLite state storage is available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite state storage is unavailable.", exception);
        }
    }
}
