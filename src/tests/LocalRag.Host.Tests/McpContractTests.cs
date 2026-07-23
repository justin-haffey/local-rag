using LocalRag.Api;
using LocalRag.Domain;
using ModelContextProtocol.Server;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class McpContractTests
{
    [Fact]
    public void ExposesTheFourPhaseOneToolsWithStableProtocolNames()
    {
        var names = typeof(RagMcpTools).GetMethods()
            .Select(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false).OfType<McpServerToolAttribute>().SingleOrDefault()?.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(names.SetEquals(new HashSet<string>(StringComparer.Ordinal)
        {
            "rag_search", "rag_get_chunk", "rag_list_sources", "rag_get_source_status"
        }));
    }

    [Fact]
    public void SourceContractDoesNotExposeCanonicalPaths()
    {
        var names = typeof(SourceResponse).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("CanonicalRootPath", names);
        Assert.Contains("RootPathHash", names);
        Assert.Contains("LastError", names);
        Assert.Contains("Recovery", names);
    }

    [Fact]
    public void RecoveryContractDoesNotExposePathsContentOrLeaseData()
    {
        var names = typeof(RecoveryResponse).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("SourceId", names);
        Assert.DoesNotContain("RootPath", names);
        Assert.DoesNotContain("RelativePath", names);
        Assert.DoesNotContain("Content", names);
        Assert.DoesNotContain("LeaseId", names);
        Assert.DoesNotContain("LeaseExpiresUtc", names);
        Assert.Contains("State", names);
        Assert.Contains("Causes", names);
        Assert.Contains("LastErrorCode", names);
        Assert.Contains("ChangedFiles", names);
    }

    [Fact]
    public void SourceMappingRedactsRawErrorsAndUnboundedRecoveryValues()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new SourceRecord(
            "source-1",
            "C:\\private\\repository",
            "repository",
            SourceStatus.Degraded,
            now,
            now,
            now,
            null,
            "profile",
            "System.Exception: raw path C:\\private\\repository\\secret.cs");
        var recovery = new SourceReconciliation(
            source.SourceId,
            2,
            1,
            null,
            ReconciliationState.Degraded,
            ReconciliationCause.Retry | (ReconciliationCause)(1 << 20),
            ReconciliationCause.None,
            now,
            null,
            null,
            now,
            "unbounded-outcome",
            ReconciliationFailureCode.DependencyUnavailable,
            "raw dependency exception and path",
            -1,
            -1,
            -1);

        var response = source.ToResponse(recovery);

        Assert.Equal("A required indexing dependency is unavailable.", response.LastError);
        Assert.NotNull(response.Recovery);
        Assert.Collection(response.Recovery.Causes, cause => Assert.Equal("Retry", cause));
        Assert.Null(response.Recovery.LastOutcome);
        Assert.Equal("A required indexing dependency is unavailable.", response.Recovery.LastErrorSummary);
        Assert.Equal(0, response.Recovery.ChangedFiles);
        Assert.DoesNotContain("private", response.LastError!, StringComparison.OrdinalIgnoreCase);
    }
}
