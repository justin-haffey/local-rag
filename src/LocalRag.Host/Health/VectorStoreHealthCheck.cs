using LocalRag.Application;
using LocalRag.Infrastructure.VectorStore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalRag.Health;

public sealed class VectorStoreHealthCheck(IVectorStore vectorStore, ISelectedVectorStoreDescriptor descriptor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await vectorStore.EnsureReadyAsync(cancellationToken);
            return HealthCheckResult.Healthy($"{descriptor.Value.Provider} vector store is ready.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Degraded($"{descriptor.Value.Provider} vector store is unavailable.", exception);
        }
    }
}
