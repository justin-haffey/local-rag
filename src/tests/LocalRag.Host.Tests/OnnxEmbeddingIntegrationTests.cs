using LocalRag.Configuration;
using LocalRag.Infrastructure.Embeddings;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class OnnxEmbeddingIntegrationTests
{
    [EnvironmentFact("LOCALRAG_ONNX_TESTS", "1")]
    public async Task PinnedOnnxProfileProducesNormalizedAndSemanticallyUsefulVectorsWhenExplicitlyEnabled()
    {
        using var embeddings = new BgeOnnxEmbeddingService(Options.Create(new LocalRagOptions()));
        await embeddings.ValidateAsync(CancellationToken.None);
        var query = await embeddings.EmbedQueryAsync("Where is retry backoff configured?", CancellationToken.None);
        var relevant = await embeddings.EmbedPassageAsync("RetryOptions configures exponential retry backoff and delay.", CancellationToken.None);
        var unrelated = await embeddings.EmbedPassageAsync("A garden hose waters flowers in the afternoon.", CancellationToken.None);

        Assert.Equal(384, query.Count);
        Assert.All(query, value => Assert.True(float.IsFinite(value)));
        Assert.InRange(Magnitude(query), 0.999f, 1.001f);
        Assert.True(Dot(query, relevant) > Dot(query, unrelated), "The retrieval query should rank its paired passage above an unrelated passage.");
    }

    private static float Dot(IReadOnlyList<float> left, IReadOnlyList<float> right) => left.Zip(right).Sum(pair => pair.First * pair.Second);
    private static float Magnitude(IReadOnlyList<float> vector) => MathF.Sqrt(vector.Sum(value => value * value));
}
