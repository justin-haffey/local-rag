using System.Text.Json;
using LocalRag.Infrastructure.Diagnostics;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class OperationalMetricsTests
{
    [Fact]
    public void RecoveryMetricsExposeBoundedLabelsAndCountsWithoutSourceData()
    {
        var metrics = new OperationalMetrics();
        metrics.ReconciliationRequested("WatcherOverflow");
        metrics.ReconciliationRequested("untrusted-source-or-path-value");
        metrics.WatcherOverflowed();
        metrics.ReconciliationStarted();
        metrics.ReconciliationRetried();
        metrics.ReconciliationLeaseRecovered();
        metrics.ReconciliationFinished("Succeeded", TimeSpan.FromMilliseconds(125), 2, 1, 9);
        metrics.SetRecoveryGauges(3, 1);

        var json = JsonSerializer.Serialize(metrics.Snapshot());

        Assert.Contains("WatcherOverflow", json, StringComparison.Ordinal);
        Assert.Contains("Other", json, StringComparison.Ordinal);
        Assert.Contains("\"requests\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"changedFiles\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"dirtySources\":3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted-source-or-path-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaseId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveryMetricsClampNegativeDurationsCountsAndGauges()
    {
        var metrics = new OperationalMetrics();
        metrics.ReconciliationFinished("unknown", TimeSpan.FromMilliseconds(-10), -1, -2, -3);
        metrics.SetRecoveryGauges(-1, -1);

        var json = JsonSerializer.Serialize(metrics.Snapshot());

        Assert.Contains("\"lastDurationMilliseconds\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"changedFiles\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"dirtySources\":0", json, StringComparison.Ordinal);
    }
}
