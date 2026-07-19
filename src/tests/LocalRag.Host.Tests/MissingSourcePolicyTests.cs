using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class MissingSourcePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CleansUpMissingSourceAfterConfiguredGracePeriod()
    {
        var policy = CreatePolicy(60);
        var source = CreateSource(Now.AddMinutes(-61), MissingSourcePolicy.MissingRootMessage);

        Assert.True(policy.ShouldCleanup(source, Now));
    }

    [Fact]
    public void PreservesRecentlyMissingSourceForTemporarilyDisconnectedDrive()
    {
        var policy = CreatePolicy(60);
        var source = CreateSource(Now.AddMinutes(-59), MissingSourcePolicy.MissingRootMessage);

        Assert.False(policy.ShouldCleanup(source, Now));
    }

    [Fact]
    public void DoesNotDeleteSourceDegradedForAnotherReason()
    {
        var policy = CreatePolicy(60);
        var source = CreateSource(Now.AddDays(-1), "Weaviate is unavailable.");

        Assert.False(policy.ShouldCleanup(source, Now));
    }

    private static MissingSourcePolicy CreatePolicy(int graceMinutes) =>
        new(Options.Create(new LocalRagOptions
        {
            Indexing = new IndexingOptions { MissingSourceCleanupGraceMinutes = graceMinutes }
        }));

    private static SourceRecord CreateSource(DateTimeOffset updatedUtc, string error) =>
        new("source", "C:\\missing", "missing", SourceStatus.Degraded, Now.AddDays(-2), updatedUtc, null, null, "profile", error);
}
