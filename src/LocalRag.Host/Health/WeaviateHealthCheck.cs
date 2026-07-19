using LocalRag.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalRag.Health;

public sealed class WeaviateHealthCheck(IVectorStore vectorStore) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await vectorStore.EnsureReadyAsync(cancellationToken);
            return HealthCheckResult.Healthy("Weaviate is reachable and schema-compatible.");
        }
        catch (Exception exception)
        {
            return exception.Message.Contains("must use", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("missing compatible property", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckResult.Unhealthy("Weaviate collection schema is incompatible.", exception)
                : HealthCheckResult.Degraded("Weaviate is unavailable.", exception);
        }
    }
}
