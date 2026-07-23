using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalRag.Health;

public sealed class MaintenanceHealthCheck(
    HostMaintenanceCoordinator maintenance,
    ResetStateStore resetState) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(maintenance.IsReady && !resetState.HasIncompleteReset
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Local RAG maintenance recovery is required."));
}
