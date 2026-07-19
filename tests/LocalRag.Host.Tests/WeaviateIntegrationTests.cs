using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class WeaviateIntegrationTests
{
    [Fact]
    public async Task ExternalWeaviateSupportsSchemaUpsertHybridSearchAndDeleteWhenEndpointIsConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("WEAVIATE_TEST_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var collection = $"LocalRagContract{Guid.NewGuid():N}";
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        var store = new WeaviateVectorStore(client, Options.Create(new LocalRagOptions
        {
            Weaviate = new WeaviateOptions { Endpoint = endpoint, Collection = collection, Vectorizer = "none", BatchSize = 10 }
        }));
        var chunk = new ChunkRecord("contract-chunk", "contract-source", "contract-file", "README.md", "markdown", null, 1, 1, 0,
            "hybrid retrieval integration test", "hash", 5, "test", DateTimeOffset.UtcNow);

        try
        {
            await store.EnsureReadyAsync(CancellationToken.None);
            await store.UpsertAsync([new VectorDocument(chunk, [1f, 0f, 0f])], CancellationToken.None);
            var results = await store.SearchAsync(new SearchRequest("retrieval", ["contract-source"], 5), [1f, 0f, 0f], CancellationToken.None);
            Assert.Contains(results, result => result.ChunkId == chunk.ChunkId);
            await store.DeleteAsync([chunk.ChunkId], CancellationToken.None);
        }
        finally
        {
            await client.DeleteAsync($"v1/schema/{collection}");
        }
    }
}
