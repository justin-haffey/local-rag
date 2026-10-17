using System.Security.Cryptography;
using LocalRag.Configuration;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class EmbeddingSequencePolicyTests
{
    private static readonly EmbeddingRuntimeIdentity ValidRuntime = new(EmbeddingSequencePolicy.OrtManagedSha256, EmbeddingSequencePolicy.OrtNativeSha256, "1.27.1.0", true);

    [Fact]
    public void OnlyValidatedAssetsRuntimeAndDynamicMetadataEnableShortSequences()
    {
        Assert.True(Allow());
        Assert.False(Allow(model: "unknown")); Assert.False(Allow(vocabulary: "unknown"));
        Assert.False(Allow(runtime: ValidRuntime with { ManagedSha256 = "unknown" }));
        Assert.False(Allow(runtime: ValidRuntime with { NativeSha256 = "unknown" }));
        Assert.False(Allow(runtime: ValidRuntime with { AssemblyVersion = "other" }));
        Assert.False(Allow(runtime: ValidRuntime with { WindowsX64 = false }));
        Assert.False(EmbeddingSequencePolicy.AllowsDynamic(new(), EmbeddingSequencePolicy.ModelSha256, EmbeddingSequencePolicy.VocabularySha256, null, Shapes()));
    }

    [Fact]
    public void CompatibleStaticShapesRetainFullWidthAndConflictingShapesFailActionably()
    {
        Assert.False(Allow(shapes: Shapes(512)));
        var mixed = Shapes(); mixed["attention_mask"] = new(typeof(long), [1, 512]); Assert.False(Allow(shapes: mixed));
        foreach (var bad in new[] { new EmbeddingInputShape(typeof(float), [-1, -1]), new(typeof(long), [1, -1, 1]), new(typeof(long), [2, -1]), new(typeof(long), [-1, 128]), new(typeof(long), [-1, 0]) })
        {
            var shapes = Shapes(); shapes["input_ids"] = bad;
            Assert.Contains("input_ids", Assert.Throws<InvalidOperationException>(() => Allow(shapes: shapes)).Message, StringComparison.Ordinal);
        }
        var missing = Shapes(); missing.Remove("token_type_ids");
        Assert.Contains("missing required input", Assert.Throws<InvalidOperationException>(() => Allow(shapes: missing)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnvalidatedOptionsKeepFixedPaddingWithoutChangingTheirProfile()
    {
        foreach (var options in new[] {
            new EmbeddingOptions { MaximumTokens = 256 }, new() { Dimensions = 768 }, new() { ProfileId = "custom-profile" },
            new() { TokenizerId = "custom-tokenizer" }, new() { QueryPrefix = "query: " }, new() { PassagePrefix = "passage: " } })
            Assert.False(Allow(options));
    }

    [Fact]
    public void SelectionOnlyRemovesTrailingPaddingAndKeepsMinimumThree()
    {
        Assert.Equal(5, EmbeddingSequencePolicy.SelectLength([101, 0, 42, 102, 0], false));
        Assert.Equal(4, EmbeddingSequencePolicy.SelectLength([101, 0, 42, 102, 0], true));
        Assert.Equal(3, EmbeddingSequencePolicy.SelectLength([101, 102, 0, 0], true));
        Assert.Equal(4, EmbeddingSequencePolicy.SelectLength([101, 42, 42, 102], true));
    }

    [Fact]
    public async Task CancellationBeforeInitializationDoesNotLoadMissingModelOrVocabulary()
    {
        var root = ManagerHostIntegrationTests.NewRoot();
        using var embeddings = new BgeOnnxEmbeddingService(Options.Create(new LocalRagOptions { Embedding = new() { ModelDirectory = Path.Combine(root, "absent") } }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embeddings.EmbedPassageAsync("test", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embeddings.EmbedQueryAsync("test", cancelled.Token));
        Assert.Equal("bge-small-en-v1.5-384", embeddings.ProfileId);
        ManagerHostIntegrationTests.RecordManifest(root);
    }

    [Fact]
    public async Task TokenizerHashBindsLoadedVocabularyAndLimitStillRejectsInsteadOfTruncating()
    {
        var root = ManagerHostIntegrationTests.NewRoot(); var path = Path.Combine(root, "vocab.txt");
        await File.WriteAllLinesAsync(path, ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "test"]);
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var tokenizer = new BertWordPieceTokenizer(path);
        Assert.Equal(expected, tokenizer.VocabularySha256);
        var accepted = string.Join(' ', Enumerable.Repeat("test", 510));
        Assert.Equal(512, tokenizer.CountTokens(accepted)); Assert.Equal(512, tokenizer.Encode(accepted, 512).Length);
        Assert.Throws<InvalidOperationException>(() => tokenizer.Encode(accepted + " test", 512));
        Assert.Equal(2, tokenizer.CountTokens("")); Assert.Throws<InvalidOperationException>(() => tokenizer.Encode("", 2));
        await File.AppendAllTextAsync(path, "new-unused-token\n");
        Assert.Equal(expected, tokenizer.VocabularySha256); Assert.Equal(4, tokenizer.Encode("test", 512)[1]);
        Assert.NotEqual(tokenizer.VocabularySha256, new BertWordPieceTokenizer(path).VocabularySha256);
        ManagerHostIntegrationTests.RecordManifest(root);
    }

    [AllEnvironmentFact("LOCALRAG_ONNX_TESTS=1")]
    public async Task ActualValidatedRuntimeEnablesDynamicAndCustomVocabularyOrPrefixFallsBack()
    {
        var options = Options.Create(new LocalRagOptions());
        using (var embeddings = new BgeOnnxEmbeddingService(options))
        {
            await embeddings.ValidateAsync(CancellationToken.None); Assert.True(embeddings.UsesDynamicPadding);
            Assert.Equal(384, (await embeddings.EmbedPassageAsync("", CancellationToken.None)).Count);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embeddings.EmbedQueryAsync("test", cancelled.Token));
        }
        var root = ManagerHostIntegrationTests.NewRoot(); var vocabulary = Path.Combine(root, "vocab.txt");
        File.Copy(Path.Combine(Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory), "vocab.txt"), vocabulary);
        await File.AppendAllTextAsync(vocabulary, "\nunused-extra-entry");
        using (var custom = new BgeOnnxEmbeddingService(options, new BertWordPieceTokenizer(vocabulary)))
        {
            await custom.ValidateAsync(CancellationToken.None); Assert.False(custom.UsesDynamicPadding);
        }
        using (var prefixes = new BgeOnnxEmbeddingService(Options.Create(new LocalRagOptions { Embedding = new() { QueryPrefix = "query: ", PassagePrefix = "passage: " } })))
        {
            await prefixes.ValidateAsync(CancellationToken.None); Assert.False(prefixes.UsesDynamicPadding);
            Assert.Equal(384, (await prefixes.EmbedQueryAsync("test", CancellationToken.None)).Count);
        }
        ManagerHostIntegrationTests.RecordManifest(root);
    }

    private static Dictionary<string, EmbeddingInputShape> Shapes(int sequence = -1) => new(StringComparer.Ordinal)
    {
        ["input_ids"] = new(typeof(long), [-1, sequence]),
        ["attention_mask"] = new(typeof(long), [-1, sequence]),
        ["token_type_ids"] = new(typeof(long), [-1, sequence])
    };
    private static bool Allow(EmbeddingOptions? options = null, string? model = EmbeddingSequencePolicy.ModelSha256, string vocabulary = EmbeddingSequencePolicy.VocabularySha256, EmbeddingRuntimeIdentity? runtime = null, IReadOnlyDictionary<string, EmbeddingInputShape>? shapes = null) =>
        EmbeddingSequencePolicy.AllowsDynamic(options ?? new(), model, vocabulary, runtime ?? ValidRuntime, shapes ?? Shapes());
}
