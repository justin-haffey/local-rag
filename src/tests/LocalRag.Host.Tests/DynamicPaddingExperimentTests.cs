using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace LocalRag.Host.Tests;

/// <summary>Opt-in vector experiment only; production inference remains unchanged.</summary>
public sealed class DynamicPaddingExperimentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const double MaximumComponentDelta = 0.00002;
    private const double MaximumL2Delta = 0.0001;
    private const double MinimumCosine = 0.999999;
    private const double MaximumNormError = 0.000001;
    private const double MaximumRetrievalMetricDelta = 0.000000000001;

    [AllEnvironmentFact("LOCALRAG_DYNAMIC_PADDING_EXPERIMENT=1", "LOCALRAG_ONNX_TESTS=1")]
    public async Task CompareFixedProductionAndDynamicPaddingWithoutChangingProfile()
    {
        var repository = RepositoryRoot();
        var root = Environment.GetEnvironmentVariable("LOCALRAG_DYNAMIC_PADDING_DIRECTORY") ?? Path.Combine(repository, "artifacts", "runs", "dynamic-padding", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = Options.Create(new LocalRagOptions());
        var modelDirectory = Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory);
        var tokenizer = new BertWordPieceTokenizer(Path.Combine(modelDirectory, "vocab.txt"));
        Assert.Equal("[PAD]", File.ReadLines(Path.Combine(modelDirectory, "vocab.txt")).First());
        using var production = new BgeOnnxEmbeddingService(options, tokenizer);
        var cold = Stopwatch.StartNew(); await production.ValidateAsync(CancellationToken.None); cold.Stop();
        Assert.True(production.UsesDynamicPadding, "The validated production runtime must actually enable dynamic padding; fallback cannot certify this test.");
        var session = ((Lazy<InferenceSession>)typeof(BgeOnnxEmbeddingService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(production)!).Value;
        var fixture = Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "Fixtures", "StructuralEvaluation");
        var judgmentsText = await File.ReadAllTextAsync(Path.Combine(fixture, "judgments.json"));
        var judgments = JsonSerializer.Deserialize<Judgment[]>(judgmentsText, Json)!;
        Assert.Equal(32, judgments.Length);
        IStructuralChunker[] adapters = [new CSharpStructuralChunker(), new TypeScriptJavaScriptStructuralChunker(), new PythonStructuralChunker(), new MarkdownStructuralChunker(), new JsonStructuralChunker(), new YamlStructuralChunker(), new TomlStructuralChunker(), new XmlStructuralChunker()];
        var profile = new ChunkProfileProvider(adapters, options);
        var chunker = new CompositeChunker(adapters, new GenericChunker(options, profile, tokenizer), profile, options, tokenizer, NullLogger<CompositeChunker>.Instance);
        var sourceRoot = Path.Combine(fixture, "sources");
        var source = new SourceRecord("judged", sourceRoot, "provider parity", SourceStatus.Ready, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, production.ProfileId);
        var chunks = new List<ChunkRecord>(); var corpusIdentity = new StringBuilder(judgmentsText);
        var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(24, files.Length);
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            var content = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var hash = Hash(Encoding.UTF8.GetBytes(content)); corpusIdentity.Append(relative).Append(hash);
            var file = new IndexedFile(relative, source.SourceId, relative, hash, content.Length, DateTimeOffset.UnixEpoch);
            chunks.AddRange(chunker.Chunk(source, file, content));
        }
        Assert.Equal(122, chunks.Count);
        var inputs = chunks.Select(chunk => new Input("chunk:" + chunk.ChunkId, chunk.Content, false)).ToList();
        inputs.AddRange(judgments.Select((judgment, index) => new Input("query:" + index.ToString(CultureInfo.InvariantCulture), judgment.Query, true)));
        var edges = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["empty"] = "",
            ["whitespace"] = " \t\r\n ",
            ["cjk"] = "日本語 中文 한글",
            ["composed"] = "Café naïve résumé",
            ["decomposed"] = "Cafe\u0301 nai\u0308ve re\u0301sume\u0301",
            ["emoji"] = "👩🏽‍💻 🪸 🦜",
            ["unknown"] = "qzxwvutsrqponmlkjihgfedcba_9876543210",
            ["mixed"] = "public class 日本語 { string x = \"🌏 café\"; }"
        };
        foreach (var length in new[] { 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 512, 513, 514 })
            edges.Add("tokens-" + length.ToString(CultureInfo.InvariantCulture), string.Join(' ', Enumerable.Repeat("test", length - 2)));
        foreach (var edge in edges)
            foreach (var query in new[] { false, true }) inputs.Add(new("edge:" + edge.Key + (query ? ":query" : ":passage"), edge.Value, query));
        var vectors = new Dictionary<string, Pair>(StringComparer.Ordinal);
        var comparisons = new List<object>(); var gates = new List<bool>(); var retrieval = new List<object>(); var timings = new List<object>();
        var process = Process.GetCurrentProcess();
        try
        {
            foreach (var input in inputs)
            {
                float[]? fixedVector = null, dynamicVector = null, actualVector = null; Exception? fixedError = null, dynamicError = null, actualError = null;
                try { fixedVector = (await FixedAsync(input)).ToArray(); } catch (InvalidOperationException error) { fixedError = error; }
                try { dynamicVector = Dynamic(input); } catch (InvalidOperationException error) { dynamicError = error; }
                try { actualVector = (await ActualAsync(input)).ToArray(); } catch (InvalidOperationException error) { actualError = error; }
                var prefixed = Prefix(input); var tokenCount = tokenizer.CountTokens(prefixed);
                if (fixedError is not null || dynamicError is not null || actualError is not null)
                {
                    var matching = fixedError is not null && dynamicError is not null && fixedError.GetType() == dynamicError.GetType() && fixedError.Message == dynamicError.Message && actualError is not null && fixedError.GetType() == actualError.GetType() && fixedError.Message == actualError.Message && tokenCount > options.Value.Embedding.MaximumTokens;
                    gates.Add(matching);
                    comparisons.Add(new { input.Id, input.Query, tokenCount, rejected = true, fixedError = fixedError?.Message, dynamicError = dynamicError?.Message, actualError = actualError?.Message, passed = matching });
                    continue;
                }
                Assert.NotNull(fixedVector); Assert.NotNull(dynamicVector); Assert.NotNull(actualVector);
                var delta = fixedVector!.Zip(dynamicVector!, (left, right) => Math.Abs((double)left - right)).ToArray();
                var l2 = Math.Sqrt(delta.Sum(value => value * value)); var cosine = Cosine(fixedVector!, dynamicVector!);
                var fixedNorm = Norm(fixedVector); var dynamicNorm = Norm(dynamicVector!);
                var passed = fixedVector.Length == 384 && dynamicVector!.Length == 384 && fixedVector.All(float.IsFinite) && dynamicVector.All(float.IsFinite) &&
                    Math.Abs(fixedNorm - 1) <= MaximumNormError && Math.Abs(dynamicNorm - 1) <= MaximumNormError && delta.Max() <= MaximumComponentDelta && l2 <= MaximumL2Delta && cosine >= MinimumCosine;
                var actualDelta = fixedVector.Zip(actualVector, (left, right) => Math.Abs((double)left - right)).ToArray();
                passed &= actualVector.Length == 384 && actualVector.All(float.IsFinite) && actualDelta.Max() <= MaximumComponentDelta && Math.Sqrt(actualDelta.Sum(value => value * value)) <= MaximumL2Delta && Cosine(fixedVector, actualVector) >= MinimumCosine && Math.Abs(Norm(actualVector) - 1) <= MaximumNormError;
                gates.Add(passed); vectors.Add(input.Id, new(fixedVector, actualVector));
                comparisons.Add(new { input.Id, input.Query, tokenCount, dynamicSequenceLength = Math.Max(3, tokenCount), maximumComponentDelta = delta.Max(), actualProductionMaximumComponentDelta = actualDelta.Max(), l2Delta = l2, cosine, fixedNorm, dynamicNorm, passed });
            }
            for (var queryIndex = 0; queryIndex < judgments.Length; queryIndex++)
            {
                var judgment = judgments[queryIndex]; var queryPair = vectors["query:" + queryIndex.ToString(CultureInfo.InvariantCulture)];
                var baseline = Rank(queryPair.Fixed, false); var relevantCount = chunks.Count(IsRelevant);
                Assert.True(relevantCount > 0);
                var baselineRank = Array.FindIndex(baseline, IsRelevant) + 1; var baselineRecall = baseline.Count(IsRelevant) / (double)relevantCount;
                foreach (var (name, dynamicQuery, dynamicDocuments) in new[] { ("fixed/fixed", false, false), ("dynamic/fixed", true, false), ("fixed/dynamic", false, true), ("dynamic/dynamic", true, true) })
                {
                    var ranked = Rank(dynamicQuery ? queryPair.Dynamic : queryPair.Fixed, dynamicDocuments);
                    var rank = Array.FindIndex(ranked, IsRelevant) + 1; var recall = ranked.Count(IsRelevant) / (double)relevantCount;
                    var passed = baseline.Select(chunk => chunk.ChunkId).SequenceEqual(ranked.Select(chunk => chunk.ChunkId), StringComparer.Ordinal) && rank == baselineRank && Math.Abs(recall - baselineRecall) <= MaximumRetrievalMetricDelta;
                    gates.Add(passed);
                    retrieval.Add(new { queryIndex, judgment.Query, combination = name, firstRelevantRank = rank, recallAt10 = recall, reciprocalRankAt10 = rank == 0 ? 0 : 1d / rank, exactOrderedTop10 = passed, resultIds = ranked.Select(chunk => chunk.ChunkId).ToArray() });
                }
                bool IsRelevant(ChunkRecord chunk) => chunk.RelativePath == judgment.Path && chunk.StartLine <= judgment.EndLine && chunk.EndLine >= judgment.StartLine;
                ChunkRecord[] Rank(float[] query, bool dynamicDocuments) => chunks.OrderByDescending(chunk => Cosine(query, dynamicDocuments ? vectors["chunk:" + chunk.ChunkId].Dynamic : vectors["chunk:" + chunk.ChunkId].Fixed)).ThenBy(chunk => chunk.ChunkId, StringComparer.Ordinal).Take(10).ToArray();
            }
            var accepted = inputs.Where(input => vectors.ContainsKey(input.Id)).ToArray();
            // Predeclared alternating order controls warm drift without concurrent inference.
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                foreach (var dynamic in repetition == 2 ? new[] { true, false } : new[] { false, true })
                {
                    var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew(); double checksum = 0;
                    foreach (var input in accepted)
                    {
                        var vector = dynamic ? Dynamic(input) : (await FixedAsync(input)).ToArray(); checksum += vector[0];
                    }
                    timer.Stop(); process.Refresh();
                    timings.Add(new { repetition, mode = dynamic ? "dynamic" : "fixed512", inputs = accepted.Length, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, processCpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds, workingSetBytes = process.WorkingSet64, checksum });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(root, "vectors.json"), JsonSerializer.Serialize(vectors, Json));
        }
        finally
        {
            var sourceFiles = Directory.EnumerateFiles(Path.Combine(repository, "src", "LocalRag.Host"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(Path.Combine(repository, "src", "LocalRag.Host"), path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                .Append(Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "DynamicPaddingExperimentTests.cs"))
                .Order(StringComparer.Ordinal).Select(path => new { path = Path.GetRelativePath(repository, path).Replace('\\', '/'), sha256 = Hash(File.ReadAllBytes(path)) }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(root, "experiment.json"), JsonSerializer.Serialize(new
            {
                completedUtc = DateTimeOffset.UtcNow,
                profileId = production.ProfileId,
                productionDynamicPaddingEligible = production.UsesDynamicPadding,
                coldInitializationMilliseconds = cold.Elapsed.TotalMilliseconds,
                thresholds = new { MaximumComponentDelta, MaximumL2Delta, MinimumCosine, MaximumNormError, MaximumRetrievalMetricDelta, ranking = "exact ordered top10 IDs and first relevant rank for every query/combination; no post-hoc tie exemption" },
                modelInputs = session.InputMetadata.ToDictionary(pair => pair.Key, pair => new { dimensions = pair.Value.Dimensions, elementType = pair.Value.ElementType.FullName }),
                modelOutputs = session.OutputMetadata.ToDictionary(pair => pair.Key, pair => pair.Value.Dimensions),
                ortAssemblyVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                ortManagedSha256 = Hash(File.ReadAllBytes(typeof(InferenceSession).Assembly.Location)),
                ortNativeModules = process.Modules.Cast<ProcessModule>().Where(module => module.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)).Select(module => new { module.ModuleName, sha256 = Hash(File.ReadAllBytes(module.FileName)) }).ToArray(),
                modelSha256 = Hash(File.ReadAllBytes(Path.Combine(modelDirectory, "model.onnx"))),
                vocabularySha256 = Hash(File.ReadAllBytes(Path.Combine(modelDirectory, "vocab.txt"))),
                corpusSha256 = Hash(Encoding.UTF8.GetBytes(corpusIdentity.ToString())),
                judgmentSha256 = Hash(Encoding.UTF8.GetBytes(judgmentsText)),
                files = files.Length,
                chunks = chunks.Count,
                judgments = judgments.Length,
                inputDefinitions = inputs.Select(input => new { input.Id, input.Query, input.Text, sha256 = Hash(Encoding.UTF8.GetBytes(Prefix(input))) }),
                options.Value.Embedding,
                padTokenNumericId = 0,
                sourceFiles,
                comparisons,
                retrieval,
                timings,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                hardware = new { logicalProcessors = Environment.ProcessorCount, memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
                allParityGatesPassed = gates.All(value => value),
                passedGates = gates.Count(value => value),
                failedGates = gates.Count(value => !value),
                limitation = "Original fixed512 kernel retained independently; actual guarded production vectors used in all mixed ranking cases. Exhaustive rankings do not certify provider ranking/ingestion throughput."
            }, Json));
        }
        Assert.All(gates, Assert.True);

        string Prefix(Input input) => (input.Query ? options.Value.Embedding.QueryPrefix : options.Value.Embedding.PassagePrefix) + input.Text;
        Task<IReadOnlyList<float>> ActualAsync(Input input) => input.Query ? production.EmbedQueryAsync(input.Text, CancellationToken.None) : production.EmbedPassageAsync(input.Text, CancellationToken.None);
        Task<IReadOnlyList<float>> FixedAsync(Input input) => Task.FromResult<IReadOnlyList<float>>(Run(input, false));
        float[] Dynamic(Input input) => Run(input, true);
        float[] Run(Input input, bool dynamic)
        {
            // Keep production hard-limit validation before trimming only trailing numeric-zero padding.
            var tokenIds = tokenizer.Encode(Prefix(input), options.Value.Embedding.MaximumTokens);
            var length = dynamic ? Math.Max(3, Array.FindLastIndex(tokenIds, id => id != 0) + 1) : options.Value.Embedding.MaximumTokens;
            var inputIds = new DenseTensor<long>(tokenIds.AsMemory(0, length), [1, length]);
            var attentionMask = new DenseTensor<long>([1, length]); var tokenTypeIds = new DenseTensor<long>([1, length]);
            for (var index = 0; index < length; index++) attentionMask[0, index] = tokenIds[index] == 0 ? 0 : 1;
            using var result = session.Run([
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds), NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask), NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            ]);
            var hidden = result[0].AsTensor<float>();
            if (hidden.Rank != 3 || hidden.Dimensions[0] != 1 || hidden.Dimensions[1] < length || hidden.Dimensions[2] != options.Value.Embedding.Dimensions) throw new InvalidOperationException("Experimental model shape mismatch.");
            var vector = new float[options.Value.Embedding.Dimensions];
            var nonPadding = Math.Max(1, attentionMask.ToArray().Count(value => value == 1));
            for (var token = 0; token < length; token++)
            {
                if (attentionMask[0, token] == 0) continue;
                for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] += hidden[0, token, dimension];
            }
            var magnitude = 0d;
            for (var dimension = 0; dimension < vector.Length; dimension++) { vector[dimension] /= nonPadding; magnitude += vector[dimension] * vector[dimension]; }
            magnitude = Math.Sqrt(magnitude);
            if (magnitude > 0) for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = (float)(vector[dimension] / magnitude);
            return vector;
        }
    }

    private static double Norm(float[] vector) => Math.Sqrt(vector.Sum(value => (double)value * value));
    private static double Cosine(float[] left, float[] right) => left.Zip(right, (a, b) => (double)a * b).Sum() / (Norm(left) * Norm(right));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "LocalRag.Host", "LocalRag.Host.csproj"))) directory = directory.Parent;
        return directory!.FullName;
    }
    private sealed record Input(string Id, string Text, bool Query);
    private sealed record Pair(float[] Fixed, float[] Dynamic);
    private sealed record Judgment(string Path, string Query, int StartLine, int EndLine);
}
