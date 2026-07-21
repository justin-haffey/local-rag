using LocalRag.Configuration;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class LocalRagOptionsValidatorTests
{
    [Fact]
    public void DefaultOptionsPassValidation()
    {
        Assert.True(new LocalRagOptionsValidator().Validate(null, new LocalRagOptions()).Succeeded);
    }

    [Fact]
    public void InvalidTokenLimitsAndUnknownAdaptersFailValidation()
    {
        var options = new LocalRagOptions
        {
            Chunking = new ChunkingOptions
            {
                TargetTokens = 500,
                MaximumTokens = 2,
                OverlapTokens = -1,
                EnabledAdapters = ["csharp", "unknown"]
            },
            Embedding = new EmbeddingOptions { MaximumTokens = 2, TokenizerId = string.Empty }
        };

        var result = new LocalRagOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("MaximumTokens", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("TargetTokens", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("OverlapTokens", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("TokenizerId", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("unknown", StringComparison.Ordinal));
    }
}
