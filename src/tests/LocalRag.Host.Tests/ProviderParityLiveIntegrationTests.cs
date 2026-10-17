using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ProviderParityLiveIntegrationTests
{
    // Defined before execution: use the existing Phase 2 absolute relevance and
    // warm-search limits; provider ranking scores need not be numerically equal.
    private const double MinimumRecallAt10 = 0.85;
    private const double MinimumMrrAt10 = 0.75;
    private const double MaximumWarmSearchP95Milliseconds = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "WEAVIATE_TEST_ENDPOINT", "LOCALRAG_ONNX_TESTS=1")]
    public async Task RealProvidersMeetSharedJudgedCorpusAndFilterContracts()
    {
        var endpoint = Environment.GetEnvironmentVariable("WEAVIATE_TEST_ENDPOINT")!;
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOCALRAG_NATIVE_RUN_DIRECTORY")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "runs", "provider-parity"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var collection = "LocalRagParity" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        var options = Options.Create(new LocalRagOptions
        {
            DataDirectory = Path.Combine(root, "state"),
            VectorStore = new VectorStoreOptions
            {
                Weaviate = new WeaviateOptions { Endpoint = endpoint, Collection = collection, BatchSize = 100 }
            }
        });
        using var ownership = new InstallationOwnershipStore(options);
        using var native = new LanceDbVectorStore(
            new LanceDbOptions { Path = Path.Combine(root, "vectors"), Table = "rag_chunks_v1", BatchSize = 100 },
            ownership, new LanceDbIpcCodec(),
            () => new LanceDbNativeApi(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!));
        var external = new WeaviateVectorStore(client, options);
        using var embeddings = new BgeOnnxEmbeddingService(options);
        try
        {
            await embeddings.ValidateAsync(CancellationToken.None);
            var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StructuralEvaluation");
            var judgmentsText = await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "judgments.json"));
            var judgments = JsonSerializer.Deserialize<Judgment[]>(judgmentsText, JsonOptions)!;
            Assert.Equal(32, judgments.Length);
            var documents = new List<VectorDocument>();
            var ingestion = Stopwatch.StartNew();
            var tokenizer = new BertWordPieceTokenizer(Path.Combine(Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory), "vocab.txt"));
            IStructuralChunker[] adapters = [new CSharpStructuralChunker(), new TypeScriptJavaScriptStructuralChunker(), new PythonStructuralChunker(), new MarkdownStructuralChunker(), new JsonStructuralChunker(), new YamlStructuralChunker(), new TomlStructuralChunker(), new XmlStructuralChunker()];
            var profile = new ChunkProfileProvider(adapters, options);
            var chunker = new CompositeChunker(adapters, new GenericChunker(options, profile, tokenizer), profile, options, tokenizer, NullLogger<CompositeChunker>.Instance);
            var sourceRoot = Path.Combine(fixtureRoot, "sources");
            var source = new SourceRecord("judged", sourceRoot, "provider parity", SourceStatus.Ready, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, embeddings.ProfileId);
            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            Assert.True(files.Length >= 24);
            var corpusIdentity = new StringBuilder(judgmentsText);
            foreach (var path in files)
            {
                var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
                var content = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                corpusIdentity.Append(relative).Append(contentHash);
                var file = new IndexedFile(relative, source.SourceId, relative, contentHash, content.Length, DateTimeOffset.UnixEpoch);
                foreach (var chunk in chunker.Chunk(source, file, content))
                    documents.Add(new VectorDocument(chunk, await embeddings.EmbedPassageAsync(chunk.Content, CancellationToken.None)));
            }
            ingestion.Stop();
            var reports = new List<object>();
            var gates = new List<bool>();
            foreach (var (name, provider) in new (string Name, IVectorStore Store)[] { ("LanceDb", native), ("Weaviate", external) })
            {
                await provider.EnsureReadyAsync(CancellationToken.None);
                var upsert = Stopwatch.StartNew();
                await provider.UpsertAsync(documents, CancellationToken.None);
                upsert.Stop();
                foreach (var mode in Enum.GetValues<SearchMode>())
                {
                    var ranks = new List<int>();
                    var recalls = new List<double>();
                    var queries = new List<object>();
                    var timings = new List<double>();
                    var warm = await embeddings.EmbedQueryAsync(judgments[0].Query, CancellationToken.None);
                    _ = await provider.SearchAsync(new SearchRequest(judgments[0].Query, ["judged"], 10, Mode: mode), warm, CancellationToken.None);
                    foreach (var judgment in judgments)
                    {
                        var timer = Stopwatch.StartNew();
                        var vector = mode == SearchMode.Lexical ? new float[384] : await embeddings.EmbedQueryAsync(judgment.Query, CancellationToken.None);
                        var results = await provider.SearchAsync(new SearchRequest(judgment.Query, ["judged"], 10, Mode: mode), vector, CancellationToken.None);
                        timings.Add(timer.Elapsed.TotalMilliseconds);
                        Assert.All(results, result => Assert.Equal("judged", result.SourceId));
                        Assert.InRange(results.Count, 0, 10);
                        var rank = Array.FindIndex(results.ToArray(), result => result.RelativePath == judgment.Path && result.StartLine <= judgment.EndLine && result.EndLine >= judgment.StartLine) + 1;
                        ranks.Add(rank);
                        var relevant = documents.Count(document => document.Chunk.RelativePath == judgment.Path && document.Chunk.StartLine <= judgment.EndLine && document.Chunk.EndLine >= judgment.StartLine);
                        Assert.True(relevant > 0, "Every original judgment must resolve to production chunks.");
                        var recall = results.Count(result => result.RelativePath == judgment.Path && result.StartLine <= judgment.EndLine && result.EndLine >= judgment.StartLine) / (double)relevant;
                        recalls.Add(recall);
                        queries.Add(new { judgment.Query, judgment.Path, judgment.Family, firstRelevantRank = rank, recallAt10 = recall, resultIds = results.Select(result => result.ChunkId).ToArray() });
                    }
                    var averageRecall = recalls.Average();
                    var mrr = ranks.Average(rank => rank == 0 ? 0 : 1.0 / rank);
                    var sorted = timings.Order().ToArray();
                    var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
                    reports.Add(new { provider = name, mode = mode.ToString(), recallAt10 = averageRecall, mrrAt10 = mrr, p50Milliseconds = sorted[sorted.Length / 2], p95Milliseconds = p95, samples = sorted.Length, upsertMilliseconds = upsert.Elapsed.TotalMilliseconds, normalizedFilesPerSecond = files.Length / (ingestion.Elapsed.TotalSeconds + upsert.Elapsed.TotalSeconds), queries });
                    gates.Add(averageRecall >= MinimumRecallAt10 && mrr >= MinimumMrrAt10 && p95 < MaximumWarmSearchP95Milliseconds);
                }
                var filtered = new SearchRequest("invoice", ["judged"], 3, Mode: SearchMode.Vector, Language: "csharp", PathPrefix: "csharp/");
                var filterVector = await embeddings.EmbedQueryAsync(filtered.Query, CancellationToken.None);
                var first = await provider.SearchAsync(filtered, filterVector, CancellationToken.None);
                Assert.All(first, result => { Assert.Equal("csharp", result.Language); Assert.StartsWith("csharp/", result.RelativePath, StringComparison.Ordinal); });
                Assert.Equal(first.Select(result => result.ChunkId), (await provider.SearchAsync(filtered, filterVector, CancellationToken.None)).Select(result => result.ChunkId));
                Assert.Empty(await provider.SearchAsync(filtered with { SourceIds = ["missing-source"] }, filterVector, CancellationToken.None));
            }
            var reportPath = Environment.GetEnvironmentVariable("LOCALRAG_PROVIDER_PARITY_REPORT") ?? Path.Combine(root, "provider-parity.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                createdUtc = DateTimeOffset.UtcNow,
                corpus = "complete StructuralEvaluation corpus with production structural chunkers",
                judgments = judgments.Length,
                sourceFiles = files.Length,
                chunks = documents.Count,
                corpusSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(corpusIdentity.ToString()))),
                judgmentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(judgmentsText))),
                nativeManifestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!))),
                thresholds = new { MinimumRecallAt10, MinimumMrrAt10, MaximumWarmSearchP95Milliseconds },
                methodology = "Complete checked-in source corpus including distractors; 32 original judgments; production structural chunkers; real BGE ONNX; identical documents in each provider; warm-up excluded; query embedding included except lexical; sequential local execution",
                peakProcessWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                normalizedChunkAndEmbeddingMilliseconds = ingestion.Elapsed.TotalMilliseconds,
                reports,
                allGatesPassed = gates.All(value => value)
            }, JsonOptions));
            Assert.All(gates, Assert.True);
        }
        finally
        {
            await client.DeleteAsync($"v1/schema/{collection}");
        }
    }

    private sealed record Judgment(string Family, string Path, string Query, string Intent, int StartLine, int EndLine);
}
