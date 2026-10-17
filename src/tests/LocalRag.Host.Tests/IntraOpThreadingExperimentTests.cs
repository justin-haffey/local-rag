using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>Opt-in, test-only threading matrix. No production session options are changed.</summary>
public sealed class IntraOpThreadingExperimentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private const double MaximumComponentDelta = 0.00002;
    private const double MaximumL2Delta = 0.0001;
    private const double MinimumCosine = 0.999999;
    private const double MaximumNormError = 0.000001;
    private const double MaximumMetricDelta = 0.000000000001;
    private const double MinimumSyntheticImprovement = 0.20;
    private const double MaximumJudgedRegression = 0.10;
    private const double PreferFewerThreadsWithin = 0.05;

    [AllEnvironmentFact("LOCALRAG_INTRA_OP_EXPERIMENT=1", "LOCALRAG_ONNX_TESTS=1")]
    public async Task MeasurePreregisteredThreadingMatrixWithoutProductionChanges()
    {
        var repository = RepositoryRoot();
        var root = Environment.GetEnvironmentVariable("LOCALRAG_INTRA_OP_DIRECTORY") ?? Path.Combine(repository, "artifacts", "runs", "intra-op", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = Options.Create(new LocalRagOptions());
        var modelDirectory = Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory);
        var modelPath = Path.Combine(modelDirectory, "model.onnx"); var vocabularyPath = Path.Combine(modelDirectory, "vocab.txt");
        Assert.Equal(EmbeddingSequencePolicy.ModelSha256, HashFile(modelPath)); Assert.Equal(EmbeddingSequencePolicy.VocabularySha256, HashFile(vocabularyPath));
        var tokenizer = new BertWordPieceTokenizer(vocabularyPath);
        var productionSources = ProductionSources(repository);
        var data = await PrepareAsync(repository, root, options, tokenizer);
        Assert.Equal(370, data.Inputs.Length); Assert.Equal(122, data.Judged.Length); Assert.Equal(144, data.Synthetic.Length); Assert.Equal(88, data.Queries.Length);
        var vectors = new Dictionary<int, Dictionary<string, VectorResult>>();
        var comparisons = new List<Comparison>(); var rankings = new List<Ranking>(); var initialization = new List<object>(); var timings = new List<Timing>();
        var decisions = new List<Decision>(); var complete = false; var process = Process.GetCurrentProcess();
        object? metadata = null; int? recommendedThreads = null;
        try
        {
            // The actual default service is disposed before any candidate is created.
            using (var baseline = new Runner(0, modelPath, options, tokenizer))
            {
                initialization.Add(new { phase = "parity", mode = 0, baseline.InitializationMilliseconds, baseline.Options });
                metadata = baseline.Session.InputMetadata.ToDictionary(pair => pair.Key, pair => new { dimensions = pair.Value.Dimensions, elementType = pair.Value.ElementType.FullName });
                var actual = new Dictionary<string, VectorResult>(StringComparer.Ordinal);
                foreach (var input in data.Inputs)
                {
                    var reference = Capture(() => baseline.Run(input)); actual.Add(input.Id, reference);
                    var kernel = Capture(() => Kernel(baseline.Session, input, options.Value.Embedding, tokenizer));
                    comparisons.Add(Compare(0, input, reference, kernel, tokenizer, options.Value.Embedding));
                }
                vectors.Add(0, actual);
            }
            foreach (var mode in new[] { 1, 2, 4 })
            {
                using var candidate = new Runner(mode, modelPath, options, tokenizer);
                initialization.Add(new { phase = "parity", mode, candidate.InitializationMilliseconds, candidate.Options });
                var actual = new Dictionary<string, VectorResult>(StringComparer.Ordinal);
                foreach (var input in data.Inputs)
                {
                    var result = Capture(() => candidate.Run(input)); actual.Add(input.Id, result);
                    comparisons.Add(Compare(mode, input, vectors[0][input.Id], result, tokenizer, options.Value.Embedding));
                }
                vectors.Add(mode, actual);
            }
            foreach (var mode in new[] { 1, 2, 4 })
            {
                foreach (var query in data.Queries)
                {
                    var pool = query.Pool == "judged" ? data.Judged : data.Judged.Concat(data.Synthetic).ToArray();
                    var baseline = Rank(pool, vectors[0], vectors[0][query.InputId].Vector!);
                    var relevantCount = pool.Count(chunk => Relevant(chunk, query)); Assert.True(relevantCount > 0);
                    var baselineRank = Array.FindIndex(baseline, hit => Relevant(hit.Chunk, query)) + 1;
                    var baselineRecall = baseline.Count(hit => Relevant(hit.Chunk, query)) / (double)relevantCount;
                    foreach (var (combination, candidateQuery, candidateDocuments) in new[] { ("default/default", false, false), ("candidate/default", true, false), ("default/candidate", false, true), ("candidate/candidate", true, true) })
                    {
                        var queryVectors = vectors[candidateQuery ? mode : 0]; var documents = vectors[candidateDocuments ? mode : 0];
                        if (queryVectors[query.InputId].Vector is null || pool.Any(chunk => documents[PassageId(chunk)].Vector is null))
                        {
                            rankings.Add(new(mode, query.InputId, query.Pool, combination, false, 0, 0, 0, baseline.Select(hit => hit.Chunk.ChunkId).ToArray(), [], [])); continue;
                        }
                        var results = Rank(pool, documents, queryVectors[query.InputId].Vector!);
                        var rank = Array.FindIndex(results, hit => Relevant(hit.Chunk, query)) + 1; var recall = results.Count(hit => Relevant(hit.Chunk, query)) / (double)relevantCount;
                        var reciprocal = rank == 0 ? 0 : 1d / rank; var baseReciprocal = baselineRank == 0 ? 0 : 1d / baselineRank;
                        var passed = baseline.Select(hit => hit.Chunk.ChunkId).SequenceEqual(results.Select(hit => hit.Chunk.ChunkId), StringComparer.Ordinal) && rank == baselineRank && Math.Abs(recall - baselineRecall) <= MaximumMetricDelta && Math.Abs(reciprocal - baseReciprocal) <= MaximumMetricDelta;
                        rankings.Add(new(mode, query.InputId, query.Pool, combination, passed, rank, recall, reciprocal, baseline.Select(hit => hit.Chunk.ChunkId).ToArray(), results.Select(hit => hit.Chunk.ChunkId).ToArray(), results.Select(hit => hit.Score).ToArray()));
                    }
                }
            }
            Assert.Equal(1056, rankings.Count);
            await File.WriteAllTextAsync(Path.Combine(root, "vectors.json"), JsonSerializer.Serialize(vectors, Json));
            // Every parity/default service is already disposed. Keep exactly one timed session alive.
            int[][] orders = [[0, 1, 2, 4], [4, 2, 1, 0], [1, 0, 4, 2]];
            for (var repetition = 0; repetition < orders.Length; repetition++)
            {
                foreach (var mode in orders[repetition])
                {
                    using var runner = new Runner(mode, modelPath, options, tokenizer);
                    initialization.Add(new { phase = "timing", repetition = repetition + 1, mode, runner.InitializationMilliseconds, runner.Options });
                    _ = runner.Run(new("warm", "local rag embedding readiness probe", false));
                    _ = runner.Run(data.Inputs.First(input => input.Id == PassageId(data.Judged[0])));
                    _ = runner.Run(data.Inputs.First(input => input.Id == PassageId(data.Synthetic[0])));
                    foreach (var (name, workload) in new[] { ("judged", data.Judged), ("synthetic", data.Synthetic) })
                    {
                        var inputs = workload.Select(chunk => new Input(PassageId(chunk), chunk.Content, false)).ToArray();
                        var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew(); double checksum = 0;
                        foreach (var input in inputs) checksum += runner.Run(input)[0];
                        timer.Stop(); process.Refresh();
                        timings.Add(new(mode, repetition + 1, name, inputs.Length, timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds, process.WorkingSet64, checksum));
                    }
                }
            }
            Assert.Equal(24, timings.Count);
            foreach (var mode in new[] { 1, 2, 4 })
            {
                var synthetic = Median(timings, mode, "synthetic", false); var baselineSynthetic = Median(timings, 0, "synthetic", false);
                var judged = Median(timings, mode, "judged", false); var baselineJudged = Median(timings, 0, "judged", false);
                var cpu = Median(timings, mode, "synthetic", true); var baselineCpu = Median(timings, 0, "synthetic", true);
                var parity = comparisons.Where(row => row.Mode is 0 || row.Mode == mode).All(row => row.Passed) && rankings.Where(row => row.Mode == mode).All(row => row.Passed);
                var qualifies = parity && synthetic <= baselineSynthetic * (1 - MinimumSyntheticImprovement) && cpu < baselineCpu && judged <= baselineJudged * (1 + MaximumJudgedRegression);
                decisions.Add(new(mode, parity, synthetic, synthetic / baselineSynthetic, cpu, cpu / baselineCpu, judged, judged / baselineJudged, qualifies));
            }
            var eligible = decisions.Where(row => row.Qualifies).ToArray();
            if (eligible.Length > 0)
            {
                var fastest = eligible.Min(row => row.SyntheticMedianMilliseconds);
                recommendedThreads = eligible.Where(row => row.SyntheticMedianMilliseconds <= fastest * (1 + PreferFewerThreadsWithin)).Min(row => row.Mode);
            }
            complete = true;
        }
        finally
        {
            var currentSources = ProductionSources(repository);
            var sourcesUnchanged = productionSources.SequenceEqual(currentSources);
            await File.WriteAllTextAsync(Path.Combine(root, "threading.json"), JsonSerializer.Serialize(new
            {
                completedUtc = DateTimeOffset.UtcNow,
                completedMatrix = complete,
                recommendedThreads,
                thresholds = new { MaximumComponentDelta, MaximumL2Delta, MinimumCosine, MaximumNormError, MaximumMetricDelta, MinimumSyntheticImprovement, MaximumJudgedRegression, PreferFewerThreadsWithin },
                inputMetadata = metadata,
                options.Value.Embedding,
                padNumericId = 0,
                modelSha256 = HashFile(modelPath),
                vocabularySha256 = tokenizer.VocabularySha256,
                ortAssemblyVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                ortManagedSha256 = HashFile(typeof(InferenceSession).Assembly.Location),
                ortNativeModules = process.Modules.Cast<ProcessModule>().Where(module => module.ModuleName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase)).Select(module => new { module.ModuleName, sha256 = HashFile(module.FileName) }).ToArray(),
                corpora = data.Corpora,
                chunks = data.Judged.Concat(data.Synthetic).Select(chunk => new { chunk.ChunkId, chunk.SourceId, chunk.RelativePath, chunk.StartLine, chunk.EndLine, chunk.TokenCount, chunk.ContentHash, inputId = PassageId(chunk) }),
                inputs = data.Inputs.Select(input => new { input.Id, input.Query, input.Text, tokens = tokenizer.CountTokens(Prefix(input, options.Value.Embedding)), sha256 = Hash(Encoding.UTF8.GetBytes(Prefix(input, options.Value.Embedding))) }),
                queries = data.Queries,
                comparisons,
                rankings,
                initialization,
                timings,
                decisions,
                allParityGatesPassed = complete && comparisons.All(row => row.Passed) && rankings.All(row => row.Passed),
                failedNumericalOrRejectionCases = comparisons.Count(row => !row.Passed),
                failedRankings = rankings.Count(row => !row.Passed),
                productionSourcesUnchanged = sourcesUnchanged,
                productionSources,
                experimentSourceSha256 = HashFile(Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "IntraOpThreadingExperimentTests.cs")),
                peakWorkingSetBytes = process.PeakWorkingSet64,
                hardware = new { logicalProcessors = Environment.ProcessorCount, memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
                limitation = "Serial ONNX component experiment only. No production, provider, concurrency or batching changes; production throughput and release approval remain separate."
            }, Json));
            Assert.True(sourcesUnchanged, "Production C# sources changed during the test-only experiment.");
        }
        Assert.True(complete); Assert.All(comparisons, row => Assert.True(row.Passed, $"Numerical/rejection gate failed for mode {row.Mode}, input {row.InputId}."));
        Assert.All(rankings, row => Assert.True(row.Passed, $"Strict mixed ranking failed for mode {row.Mode}, query {row.QueryId}, pool {row.Pool}, combination {row.Combination}."));
    }

    private static async Task<Data> PrepareAsync(string repository, string root, IOptions<LocalRagOptions> options, BertWordPieceTokenizer tokenizer)
    {
        var fixture = Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "Fixtures", "StructuralEvaluation");
        var judgmentsText = await File.ReadAllTextAsync(Path.Combine(fixture, "judgments.json"));
        var judgments = JsonSerializer.Deserialize<Judgment[]>(judgmentsText, Json)!; Assert.Equal(32, judgments.Length);
        var judgedRoot = Path.Combine(root, "corpora", "judged"); var syntheticRoot = Path.Combine(root, "corpora", "synthetic"); Directory.CreateDirectory(syntheticRoot);
        foreach (var file in Directory.GetFiles(Path.Combine(fixture, "sources"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(judgedRoot, Path.GetRelativePath(Path.Combine(fixture, "sources"), file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        for (var file = 0; file < 24; file++)
        {
            var text = new StringBuilder();
            for (var line = 0; line < 64; line++) text.AppendLine(SyntheticLine(file, line));
            await File.WriteAllTextAsync(Path.Combine(syntheticRoot, $"reference-{file:D2}.txt"), text.ToString(), new UTF8Encoding(false));
        }
        IStructuralChunker[] adapters = [new CSharpStructuralChunker(), new TypeScriptJavaScriptStructuralChunker(), new PythonStructuralChunker(), new MarkdownStructuralChunker(), new JsonStructuralChunker(), new YamlStructuralChunker(), new TomlStructuralChunker(), new XmlStructuralChunker()];
        var profile = new ChunkProfileProvider(adapters, options); var chunker = new CompositeChunker(adapters, new GenericChunker(options, profile, tokenizer), profile, options, tokenizer, NullLogger<CompositeChunker>.Instance);
        var corpora = new List<Corpus>();
        var judged = await ChunkAsync("judged", judgedRoot, 9758); var synthetic = await ChunkAsync("synthetic", syntheticRoot, 216576);
        var inputs = judged.Concat(synthetic).Select(chunk => new Input(PassageId(chunk), chunk.Content, false)).ToList();
        var queries = new List<QueryCase>();
        for (var index = 0; index < judgments.Length; index++)
        {
            var id = "query:judged:" + index.ToString(CultureInfo.InvariantCulture); var judgment = judgments[index]; inputs.Add(new(id, judgment.Query, true));
            queries.Add(new(id, "judged", "judged", judgment.Path, judgment.StartLine, judgment.EndLine)); queries.Add(new(id, "combined", "judged", judgment.Path, judgment.StartLine, judgment.EndLine));
        }
        for (var file = 0; file < 24; file++)
        {
            var id = "query:synthetic:" + file.ToString(CultureInfo.InvariantCulture); inputs.Add(new(id, SyntheticLine(file, 7), true));
            queries.Add(new(id, "combined", "synthetic", $"reference-{file:D2}.txt", 8, 8));
        }
        var edges = new Dictionary<string, string>(StringComparer.Ordinal) { ["empty"] = "", ["whitespace"] = " \t\r\n ", ["cjk"] = "日本語 中文 한글", ["composed"] = "Café naïve résumé", ["decomposed"] = "Cafe\u0301 nai\u0308ve re\u0301sume\u0301", ["emoji"] = "👩🏽‍💻 🪸 🦜", ["unknown"] = "qzxwvutsrqponmlkjihgfedcba_9876543210", ["mixed"] = "public class 日本語 { string x = \"🌏 café\"; }" };
        foreach (var count in new[] { 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 512, 513, 514 }) edges.Add("tokens-" + count.ToString(CultureInfo.InvariantCulture), string.Join(' ', Enumerable.Repeat("test", count - 2)));
        foreach (var edge in edges) foreach (var query in new[] { false, true }) inputs.Add(new("edge:" + edge.Key + (query ? ":query" : ":passage"), edge.Value, query));
        return new(judged, synthetic, inputs.ToArray(), queries.ToArray(), corpora.ToArray());

        async Task<ChunkRecord[]> ChunkAsync(string name, string path, int expectedBytes)
        {
            var source = new SourceRecord(name, path, name, SourceStatus.Ready, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, options.Value.Embedding.ProfileId);
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(24, files.Length);
            var definitions = files.Select(file => new CorpusFile(Path.GetRelativePath(path, file).Replace('\\', '/'), new FileInfo(file).Length, HashFile(file))).ToArray();
            Assert.Equal(expectedBytes, definitions.Sum(file => file.Bytes)); corpora.Add(new(name, expectedBytes, definitions)); var records = new List<ChunkRecord>();
            foreach (var filePath in files)
            {
                var content = (await File.ReadAllTextAsync(filePath)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'); var relative = Path.GetRelativePath(path, filePath).Replace('\\', '/');
                var file = new IndexedFile(relative, name, relative, Hash(Encoding.UTF8.GetBytes(content)), new FileInfo(filePath).Length, DateTimeOffset.UnixEpoch); records.AddRange(chunker.Chunk(source, file, content));
            }
            return records.ToArray();
        }
    }

    private sealed class Runner : IDisposable
    {
        private readonly BgeOnnxEmbeddingService? _production;
        private readonly SessionOptions _sessionOptions;
        private readonly EmbeddingOptions _options;
        private readonly BertWordPieceTokenizer _tokenizer;
        internal InferenceSession Session { get; }
        internal double InitializationMilliseconds { get; }
        internal object Options { get; }

        internal Runner(int mode, string modelPath, IOptions<LocalRagOptions> options, BertWordPieceTokenizer tokenizer)
        {
            var timer = Stopwatch.StartNew();
            _options = options.Value.Embedding; _tokenizer = tokenizer; _sessionOptions = new SessionOptions();
            if (mode != 0) _sessionOptions.IntraOpNumThreads = mode;
            Options = new { requestedIntraOpThreads = mode, _sessionOptions.IntraOpNumThreads, _sessionOptions.InterOpNumThreads, executionMode = _sessionOptions.ExecutionMode.ToString(), graphOptimization = _sessionOptions.GraphOptimizationLevel.ToString(), onlyIntraOpChanged = true };
            if (mode == 0)
            {
                _production = new BgeOnnxEmbeddingService(options, tokenizer); _production.ValidateAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(_production.UsesDynamicPadding, "Baseline must exercise the actual candidate3-compatible guarded dynamic path.");
                Session = ((Lazy<InferenceSession>)typeof(BgeOnnxEmbeddingService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_production)!).Value;
            }
            else
            {
                using var modelLock = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.Equal(EmbeddingSequencePolicy.ModelSha256, Convert.ToHexString(SHA256.HashData(modelLock))); Session = new InferenceSession(modelPath, _sessionOptions);
            }
            timer.Stop(); InitializationMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        internal float[] Run(Input input)
        {
            if (_production is null) return Kernel(Session, input, _options, _tokenizer);
            var vector = (input.Query ? _production.EmbedQueryAsync(input.Text, CancellationToken.None) : _production.EmbedPassageAsync(input.Text, CancellationToken.None)).GetAwaiter().GetResult();
            return vector as float[] ?? vector.ToArray();
        }
        public void Dispose() { if (_production is null) Session.Dispose(); else _production.Dispose(); _sessionOptions.Dispose(); }
    }

    private static float[] Kernel(InferenceSession session, Input input, EmbeddingOptions options, BertWordPieceTokenizer tokenizer)
    {
        var tokenIds = tokenizer.Encode(Prefix(input, options), options.MaximumTokens); var length = EmbeddingSequencePolicy.SelectLength(tokenIds, true);
        var ids = new DenseTensor<long>(tokenIds.AsMemory(0, length), [1, length]); var mask = new DenseTensor<long>([1, length]); var types = new DenseTensor<long>([1, length]);
        for (var index = 0; index < length; index++) mask[0, index] = tokenIds[index] == 0 ? 0 : 1;
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask), NamedOnnxValue.CreateFromTensor("token_type_ids", types)]);
        var hidden = results[0].AsTensor<float>(); if (hidden.Rank != 3 || hidden.Dimensions[0] != 1 || hidden.Dimensions[1] < length || hidden.Dimensions[2] != options.Dimensions) throw new InvalidOperationException("Experimental output shape mismatch.");
        var vector = new float[options.Dimensions]; var nonPadding = Math.Max(1, mask.ToArray().Count(value => value == 1));
        for (var token = 0; token < length; token++) { if (mask[0, token] == 0) continue; for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] += hidden[0, token, dimension]; }
        var magnitude = 0d; for (var dimension = 0; dimension < vector.Length; dimension++) { vector[dimension] /= nonPadding; magnitude += vector[dimension] * vector[dimension]; }
        magnitude = Math.Sqrt(magnitude); if (magnitude > 0) for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = (float)(vector[dimension] / magnitude);
        return vector;
    }

    private static Comparison Compare(int mode, Input input, VectorResult baseline, VectorResult candidate, BertWordPieceTokenizer tokenizer, EmbeddingOptions options)
    {
        var tokens = tokenizer.CountTokens(Prefix(input, options));
        if (baseline.Vector is null || candidate.Vector is null)
            return new(mode, input.Id, tokens, tokens > options.MaximumTokens && baseline.ErrorType is not null && baseline.ErrorType == candidate.ErrorType && baseline.ErrorMessage == candidate.ErrorMessage, null, null, null, null, baseline.ErrorMessage, candidate.ErrorMessage);
        var deltas = baseline.Vector.Zip(candidate.Vector, (a, b) => Math.Abs((double)a - b)).ToArray(); var l2 = Math.Sqrt(deltas.Sum(value => value * value)); var cosine = Cosine(baseline.Vector, candidate.Vector); var norm = Norm(candidate.Vector);
        var passed = baseline.Vector.Length == 384 && candidate.Vector.Length == 384 && baseline.Vector.All(float.IsFinite) && candidate.Vector.All(float.IsFinite) && Math.Abs(Norm(baseline.Vector) - 1) <= MaximumNormError && Math.Abs(norm - 1) <= MaximumNormError && deltas.Max() <= MaximumComponentDelta && l2 <= MaximumL2Delta && cosine >= MinimumCosine;
        return new(mode, input.Id, tokens, passed, deltas.Max(), l2, cosine, norm, null, null);
    }
    private static VectorResult Capture(Func<float[]> operation) { try { return new(operation(), null, null); } catch (InvalidOperationException error) { return new(null, error.GetType().FullName, error.Message); } }
    private static Hit[] Rank(ChunkRecord[] pool, Dictionary<string, VectorResult> vectors, float[] query) => pool.Select(chunk => new Hit(chunk, Cosine(query, vectors[PassageId(chunk)].Vector!))).OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Chunk.ChunkId, StringComparer.Ordinal).Take(10).ToArray();
    private static bool Relevant(ChunkRecord chunk, QueryCase query) => chunk.SourceId == query.RelevantSource && chunk.RelativePath == query.Path && chunk.StartLine <= query.EndLine && chunk.EndLine >= query.StartLine;
    private static string SyntheticLine(int file, int line) => FormattableString.Invariant($"Reference {file:D2} paragraph {line:D2}: deterministic indexing explains source identities, durable recovery, vector search and lifecycle reconciliation.");
    private static string Prefix(Input input, EmbeddingOptions options) => (input.Query ? options.QueryPrefix : options.PassagePrefix) + input.Text;
    private static string PassageId(ChunkRecord chunk) => "passage:" + chunk.SourceId + ":" + chunk.ChunkId;
    private static double Norm(float[] vector) => Math.Sqrt(vector.Sum(value => (double)value * value));
    private static double Cosine(float[] left, float[] right) => left.Zip(right, (a, b) => (double)a * b).Sum() / (Norm(left) * Norm(right));
    private static double Median(List<Timing> values, int mode, string workload, bool cpu) => values.Where(row => row.Mode == mode && row.Workload == workload).Select(row => cpu ? row.ProcessCpuMilliseconds : row.ElapsedMilliseconds).Order().ToArray()[1];
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static SourceFile[] ProductionSources(string repository) => Directory.EnumerateFiles(Path.Combine(repository, "src", "LocalRag.Host"), "*.cs", SearchOption.AllDirectories).Where(path => !Path.GetRelativePath(Path.Combine(repository, "src", "LocalRag.Host"), path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")).Order(StringComparer.Ordinal).Select(path => new SourceFile(Path.GetRelativePath(repository, path).Replace('\\', '/'), HashFile(path))).ToArray();
    private static string RepositoryRoot() { var path = new DirectoryInfo(AppContext.BaseDirectory); while (path is not null && !File.Exists(Path.Combine(path.FullName, "src", "LocalRag.Host", "LocalRag.Host.csproj"))) path = path.Parent; return path!.FullName; }
    private sealed record Input(string Id, string Text, bool Query);
    private sealed record VectorResult(float[]? Vector, string? ErrorType, string? ErrorMessage);
    private sealed record Judgment(string Path, string Query, int StartLine, int EndLine);
    private sealed record QueryCase(string InputId, string Pool, string RelevantSource, string Path, int StartLine, int EndLine);
    private sealed record Hit(ChunkRecord Chunk, double Score);
    private sealed record CorpusFile(string Path, long Bytes, string Sha256);
    private sealed record Corpus(string Name, int TotalBytes, CorpusFile[] Files);
    private sealed record Data(ChunkRecord[] Judged, ChunkRecord[] Synthetic, Input[] Inputs, QueryCase[] Queries, Corpus[] Corpora);
    private sealed record Comparison(int Mode, string InputId, int Tokens, bool Passed, double? MaximumComponentDelta, double? L2Delta, double? Cosine, double? Norm, string? BaselineError, string? CandidateError);
    private sealed record Ranking(int Mode, string QueryId, string Pool, string Combination, bool Passed, int FirstRelevantRank, double RecallAt10, double ReciprocalRankAt10, string[] BaselineIds, string[] ResultIds, double[] Scores);
    private sealed record Timing(int Mode, int Repetition, string Workload, int Inputs, double ElapsedMilliseconds, double ProcessCpuMilliseconds, long WorkingSetBytes, double Checksum);
    private sealed record Decision(int Mode, bool AllParityGatesPassed, double SyntheticMedianMilliseconds, double SyntheticWallRatio, double SyntheticCpuMilliseconds, double SyntheticCpuRatio, double JudgedMedianMilliseconds, double JudgedWallRatio, bool Qualifies);
    private sealed record SourceFile(string Path, string Sha256);
}
