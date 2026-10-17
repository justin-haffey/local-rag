using LocalRag.Configuration;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void InvalidReconciliationBoundsFailValidation()
    {
        var options = new LocalRagOptions
        {
            Indexing = new IndexingOptions
            {
                ReconciliationIntervalMinutes = 0,
                ReconciliationLeaseDurationSeconds = 29,
                ReconciliationLeaseRenewalSeconds = 30,
                MaxConcurrentReconciliations = 0,
                ReconciliationDispatchPollSeconds = 61,
                ReconciliationHistoryLimit = 101
            }
        };

        var result = new LocalRagOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("ReconciliationIntervalMinutes", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("ReconciliationLeaseDurationSeconds", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("ReconciliationLeaseRenewalSeconds", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("MaxConcurrentReconciliations", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("ReconciliationDispatchPollSeconds", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("ReconciliationHistoryLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void LeaseRenewalMustBeShorterThanLeaseDuration()
    {
        var options = new LocalRagOptions
        {
            Indexing = new IndexingOptions
            {
                ReconciliationLeaseDurationSeconds = 120,
                ReconciliationLeaseRenewalSeconds = 120
            }
        };

        var result = new LocalRagOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("shorter", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagementIsDisabledByDefaultAndRequiresADistinctTokenWhenEnabled()
    {
        Assert.True(new LocalRagOptionsValidator().Validate(null, new LocalRagOptions()).Succeeded);

        var missing = new LocalRagOptions
        {
            Authentication = new AuthenticationOptions { Token = "standard" },
            Management = new ManagementOptions { Enabled = true }
        };
        var missingResult = new LocalRagOptionsValidator().Validate(null, missing);
        Assert.NotNull(missingResult.Failures);
        Assert.Contains(
            missingResult.Failures,
            failure => failure.Contains("Management.Token", StringComparison.Ordinal));

        var equal = new LocalRagOptions
        {
            Authentication = new AuthenticationOptions { Token = "same" },
            Management = new ManagementOptions { Enabled = true, Token = "same" }
        };
        var equalResult = new LocalRagOptionsValidator().Validate(null, equal);
        Assert.NotNull(equalResult.Failures);
        Assert.Contains(
            equalResult.Failures,
            failure => failure.Contains("distinct", StringComparison.Ordinal));
    }

    [Fact]
    public void LanceDbRequiresOwnedPathTableAndPositiveBatchSize()
    {
        var options = new LocalRagOptions
        {
            VectorStore = new VectorStoreOptions
            {
                Provider = VectorStoreProvider.LanceDb,
                LanceDb = new LanceDbOptions { Path = "", Table = "", BatchSize = 0 }
            }
        };

        var result = new LocalRagOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("LanceDb.Path", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("LanceDb.Table", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("LanceDb.BatchSize", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultProviderIsWeaviateForSchemaV1Compatibility()
    {
        var options = new LocalRagOptions();

        Assert.Equal(VectorStoreProvider.Weaviate, options.VectorStore.Provider);
        Assert.True(new LocalRagOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void SchemaV1WeaviateSettingsArePromotedWhenSchemaV2SectionIsAbsent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalRag:Weaviate:Endpoint"] = "http://127.0.0.1:9090"
            })
            .Build();
        var options = new LocalRagOptions
        {
            Weaviate = new WeaviateOptions { Endpoint = "http://127.0.0.1:9090", Collection = "Legacy" }
        };

        new LocalRagOptionsCompatibilityPostConfigure(configuration).PostConfigure(null, options);

        Assert.Equal("http://127.0.0.1:9090", options.VectorStore.Weaviate.Endpoint);
        Assert.Equal("Legacy", options.VectorStore.Weaviate.Collection);
    }

    [Fact]
    public void SchemaV2WeaviateSettingsTakePrecedenceOverLegacySettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalRag:VectorStore:Weaviate:Endpoint"] = "http://127.0.0.1:7070",
                ["LocalRag:Weaviate:Endpoint"] = "http://127.0.0.1:9090"
            })
            .Build();
        var options = new LocalRagOptions
        {
            VectorStore = new VectorStoreOptions
            {
                Provider = VectorStoreProvider.Weaviate,
                Weaviate = new WeaviateOptions { Endpoint = "http://127.0.0.1:7070", Collection = "Current" }
            },
            Weaviate = new WeaviateOptions { Endpoint = "http://127.0.0.1:9090", Collection = "Legacy" }
        };

        new LocalRagOptionsCompatibilityPostConfigure(configuration).PostConfigure(null, options);

        Assert.Equal("http://127.0.0.1:7070", options.VectorStore.Weaviate.Endpoint);
        Assert.Equal("Current", options.VectorStore.Weaviate.Collection);
    }
}
