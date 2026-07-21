using System.Text.Json;
using LocalRag.Domain;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class StructuralSearchContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RestAndMcpSearchPayloadRetainsAdditiveStructuralProvenanceWithoutAnAbsoluteRoot()
    {
        var result = new SearchResult(
            "chunk", "source", "src/Widget.cs", "csharp", "Run", 7, 9, 0.9,
            "void Run() { }", "hash", DateTimeOffset.UnixEpoch,
            "member", "Example.Widget.Run", "type:Example.Widget/member:Run", "csharp", "1", "fingerprint");
        var response = new SearchResponse("run", [result], 1, 2, false);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WebJson));
        var payload = document.RootElement.GetProperty("results")[0];

        Assert.Equal("src/Widget.cs", payload.GetProperty("relativePath").GetString());
        Assert.Equal(7, payload.GetProperty("startLine").GetInt32());
        Assert.Equal(9, payload.GetProperty("endLine").GetInt32());
        Assert.Equal("member", payload.GetProperty("chunkKind").GetString());
        Assert.Equal("Example.Widget.Run", payload.GetProperty("qualifiedSymbolName").GetString());
        Assert.Equal("type:Example.Widget/member:Run", payload.GetProperty("structuralLocator").GetString());
        Assert.Equal("csharp", payload.GetProperty("chunkerId").GetString());
        Assert.Equal("1", payload.GetProperty("chunkerVersion").GetString());
        Assert.Equal("fingerprint", payload.GetProperty("chunkProfileFingerprint").GetString());
        Assert.DoesNotContain("canonicalPath", payload.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("rootPath", payload.EnumerateObject().Select(property => property.Name));
    }
}
