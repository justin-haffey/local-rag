using LocalRag.Api;
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
    }
}
