using System.Diagnostics;
using System.Globalization;
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

/// <summary>Test-only, compatibility-first within-file microbatch feasibility experiment.</summary>
public sealed class MicrobatchEmbeddingExperimentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private const double MaximumComponentDelta = 0.00002;
    private const double MaximumL2Delta = 0.0001;
    private const double MinimumCosine = 0.999999;
    private const double MaximumNormError = 0.000001;
    private const double MaximumMetricDelta = 0.000000000001;
    private const long MaximumPeakBytes = 1024L * 1024 * 1024;

    [AllEnvironmentFact("LOCALRAG_MICROBATCH_EXPERIMENT=1", "LOCALRAG_ONNX_TESTS=1")]
    public async Task EvaluateMicrobatchCompatibilityBeforeBoundedTimings()
    {
        var repository = RepositoryRoot();
        var root = Environment.GetEnvironmentVariable("LOCALRAG_MICROBATCH_DIRECTORY") ?? Path.Combine(repository, "artifacts", "runs", "p6-microbatch-20261017", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = Options.Create(new LocalRagOptions());
        var modelDirectory = Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory);
        var modelPath = Path.Combine(modelDirectory, "model.onnx"); var vocabularyPath = Path.Combine(modelDirectory, "vocab.txt");
        Assert.Equal(EmbeddingSequencePolicy.ModelSha256, HashFile(modelPath)); Assert.Equal(EmbeddingSequencePolicy.VocabularySha256, HashFile(vocabularyPath));
        var tokenizer = new BertWordPieceTokenizer(vocabularyPath); var productionSources = ProductionSources(repository);
        var data = await PrepareAsync(repository, root, options, tokenizer);
        Assert.Equal(370, data.Inputs.Length); Assert.Equal(122, data.Judged.Length); Assert.Equal(144, data.Synthetic.Length); Assert.Equal(88, data.Queries.Length);
        var vectors = new Dictionary<int, Dictionary<string, VectorResult>>(); var proofVectors = new List<object>();
        var checks = new List<Check>(); var rankings = new List<Ranking>(); var atomics = new List<object>();
        var initialization = new List<object>(); var timings = new List<Timing>(); var decisions = new List<object>();
        var phase = "production-reference"; var complete = false; var parityPassed = false; int? recommendedBatch = null;
        using var process = Process.GetCurrentProcess(); object? metadata = null; object? failure = null;
        string[] currentBatch = [];
        try
        {
            using (var production = new BgeOnnxEmbeddingService(options, tokenizer))
            {
                await production.ValidateAsync(CancellationToken.None); Assert.True(production.UsesDynamicPadding);
                var reference = new Dictionary<string, VectorResult>(StringComparer.Ordinal);
                foreach (var input in data.Inputs)
                {
                    currentBatch = [input.Id];
                    reference.Add(input.Id, Capture(() => (input.Query ? production.EmbedQueryAsync(input.Text, CancellationToken.None) : production.EmbedPassageAsync(input.Text, CancellationToken.None)).GetAwaiter().GetResult().ToArray()));
                }
                vectors.Add(1, reference);
            }
            phase = "single-row-kernel-proof";
            using (var runner = new Runner(modelPath, options.Value.Embedding, tokenizer, ids => currentBatch = ids))
            {
                metadata = new
                {
                    inputs = runner.Session.InputMetadata.ToDictionary(pair => pair.Key, pair => new { dimensions = pair.Value.Dimensions, elementType = pair.Value.ElementType.FullName }),
                    outputs = runner.Session.OutputMetadata.ToDictionary(pair => pair.Key, pair => new { dimensions = pair.Value.Dimensions, elementType = pair.Value.ElementType.FullName })
                };
                initialization.Add(new { phase, batch = 1, runner.InitializationMilliseconds, runner.Options });
                foreach (var input in data.Inputs) CheckResult(phase, 1, input, vectors[1][input.Id], Capture(() => runner.Run([input])[0]));
            }
            Assert.True(checks.All(row => row.Result.Passed), "Independent single-row kernel is incompatible; stop before candidate inference/timing.");
            foreach (var width in new[] { 2, 4 })
            {
                phase = "candidate-" + width.ToString(CultureInfo.InvariantCulture);
                using var runner = new Runner(modelPath, options.Value.Embedding, tokenizer, ids => currentBatch = ids);
                initialization.Add(new { phase, batch = width, runner.InitializationMilliseconds, runner.Options });
                var accepted = data.Inputs.Where(input => vectors[1][input.Id].Vector is not null).ToArray(); Assert.Equal(366, accepted.Length);
                var actual = new Dictionary<string, VectorResult>(StringComparer.Ordinal); vectors.Add(width, actual);
                foreach (var group in accepted.Chunk(width))
                {
                    var results = runner.Run(group);
                    for (var row = 0; row < group.Length; row++) { var captured = new VectorResult(results[row], null, null); actual.Add(group[row].Id, captured); CheckResult("normal", width, group[row], vectors[1][group[row].Id], captured); }
                }
                foreach (var invalid in data.Inputs.Where(input => vectors[1][input.Id].Vector is null))
                {
                    var result = Capture(() => runner.Run([invalid])[0]); actual.Add(invalid.Id, result); CheckResult("normal-rejection", width, invalid, vectors[1][invalid.Id], result);
                    var batch = Enumerable.Repeat(accepted[0], width - 1).Append(invalid).ToArray();
                    var before = runner.Calls; var atomic = Capture(() => runner.Run(batch)[0]); var after = runner.Calls;
                    var comparison = Compare(width, invalid, vectors[1][invalid.Id], atomic, tokenizer, options.Value.Embedding);
                    atomics.Add(new { width, invalid.Id, callsBeforeBatch = before, callsAfterBatch = after, passed = comparison.Passed && before == after, result = atomic });
                    Assert.True(comparison.Passed && before == after, "Invalid batch published output or ran ONNX before rejecting all rows.");
                }
                Assert.True(checks.All(row => row.Result.Passed), "Candidate numerical/rejection incompatibility; stop before further proof/timing.");
                foreach (var group in accepted.Chunk(width))
                {
                    var reversed = group.Reverse().ToArray(); var results = runner.Run(reversed);
                    proofVectors.Add(new { stage = "reverse", width, ids = reversed.Select(input => input.Id).ToArray(), results });
                    for (var row = 0; row < reversed.Length; row++)
                    {
                        var captured = new VectorResult(results[row], null, null);
                        CheckResult("reverse/production", width, reversed[row], vectors[1][reversed[row].Id], captured);
                        CheckResult("reverse/original", width, reversed[row], actual[reversed[row].Id], captured);
                    }
                }
                foreach (var id in new[] { "edge:empty:passage", "edge:empty:query", "edge:cjk:passage", "edge:cjk:query", "edge:tokens-31:passage", "edge:tokens-129:query", "edge:tokens-511:passage", "edge:tokens-512:query" })
                {
                    var probe = data.Inputs.Single(input => input.Id == id); var anchor = data.Inputs.Single(input => input.Id == "edge:tokens-512:passage");
                    Input[] group = width == 2 ? [probe, anchor] : [probe, anchor, data.Inputs.Single(input => input.Id == "edge:empty:passage"), data.Inputs.Single(input => input.Id == "edge:cjk:passage")];
                    var results = runner.Run(group); proofVectors.Add(new { stage = "forced-512-padding", width, ids = group.Select(input => input.Id).ToArray(), results });
                    for (var row = 0; row < group.Length; row++) CheckResult("forced-512-padding", width, group[row], vectors[1][group[row].Id], new(results[row], null, null));
                }
                Assert.True(checks.All(row => row.Result.Passed), "Candidate row-order/padding incompatibility; stop before retrieval/timing.");
                AddRankings(data, vectors, width, rankings);
                Assert.True(rankings.All(row => row.Passed), "Strict mixed ranking incompatibility; stop before another candidate/timing. No tie exception is allowed.");
            }
            Assert.Equal(704, rankings.Count); parityPassed = true;
            phase = "timing";
            int[][] orders = [[1, 2, 4], [4, 2, 1], [2, 1, 4]];
            for (var repetition = 0; repetition < orders.Length; repetition++) foreach (var width in orders[repetition])
            {
                using var runner = new Runner(modelPath, options.Value.Embedding, tokenizer, ids => currentBatch = ids);
                initialization.Add(new { phase, repetition = repetition + 1, batch = width, runner.InitializationMilliseconds, runner.Options });
                _ = runner.Run([new("warm", "local rag embedding readiness probe", false)]);
                foreach (var workload in new[] { data.Judged, data.Synthetic }) _ = RunWorkload(runner, workload.GroupBy(chunk => chunk.RelativePath, StringComparer.Ordinal).First().ToArray(), width);
                foreach (var (name, workload) in new[] { ("judged", data.Judged), ("synthetic", data.Synthetic) })
                {
                    process.Refresh(); var starting = process.WorkingSet64; var lifetimeBefore = process.PeakWorkingSet64; var calls = runner.Calls;
                    using var stopSampling = new CancellationTokenSource();
                    var sampler = Task.Run(async () => { using var sampledProcess = Process.GetCurrentProcess(); var maximum = starting; try { while (true) { sampledProcess.Refresh(); maximum = Math.Max(maximum, sampledProcess.WorkingSet64); await Task.Delay(10, stopSampling.Token); } } catch (OperationCanceledException) when (stopSampling.IsCancellationRequested) { return maximum; } });
                    var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew(); var checksum = RunWorkload(runner, workload, width); timer.Stop(); process.Refresh();
                    var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds; stopSampling.Cancel(); var sampledPeak = await sampler;
                    timings.Add(new(width, repetition + 1, name, workload.Length, timer.Elapsed.TotalMilliseconds, cpuMilliseconds, process.WorkingSet64, checksum, runner.Calls - calls, starting, Math.Max(sampledPeak, process.WorkingSet64), lifetimeBefore, process.PeakWorkingSet64));
                }
            }
            Assert.Equal(18, timings.Count);
            var eligible = new List<(int Width, double Synthetic)>();
            foreach (var width in new[] { 2, 4 })
            {
                var synthetic = Median(timings, width, "synthetic", false); var baselineSynthetic = Median(timings, 1, "synthetic", false);
                var judged = Median(timings, width, "judged", false); var baselineJudged = Median(timings, 1, "judged", false);
                var cpu = Median(timings, width, "synthetic", true); var baselineCpu = Median(timings, 1, "synthetic", true);
                var qualifies = synthetic <= baselineSynthetic * 0.80 && cpu <= baselineCpu * 1.10 && judged <= baselineJudged * 1.10 && process.PeakWorkingSet64 < MaximumPeakBytes;
                decisions.Add(new { width, syntheticMedianMilliseconds = synthetic, syntheticWallRatio = synthetic / baselineSynthetic, syntheticCpuMilliseconds = cpu, syntheticCpuRatio = cpu / baselineCpu, judgedMedianMilliseconds = judged, judgedWallRatio = judged / baselineJudged, lifetimePeakBytes = process.PeakWorkingSet64, qualifies });
                if (qualifies) eligible.Add((width, synthetic));
            }
            if (eligible.Count > 0) { var fastest = eligible.Min(value => value.Synthetic); recommendedBatch = eligible.Where(value => value.Synthetic <= fastest * 1.05).Min(value => value.Width); }
            complete = true; phase = "completed";
        }
        catch (Exception error)
        {
            failure = new { phase, currentBatchIds = currentBatch, exceptionType = error.GetType().FullName, error.Message, detail = error.ToString() };
            throw;
        }
        finally
        {
            var sourcesUnchanged = productionSources.SequenceEqual(ProductionSources(repository));
            await File.WriteAllTextAsync(Path.Combine(root, "vectors.json"), JsonSerializer.Serialize(new { primary = vectors, proofVectors }, Json));
            await File.WriteAllTextAsync(Path.Combine(root, "microbatch.json"), JsonSerializer.Serialize(new
            {
                completedUtc = DateTimeOffset.UtcNow,
                phase,
                completedMatrix = complete,
                allParityGatesPassed = parityPassed,
                recommendedBatch,
                failure,
                thresholds = new { MaximumComponentDelta, MaximumL2Delta, MinimumCosine, MaximumNormError, MaximumMetricDelta, MaximumPeakBytes, maximumSyntheticWallRatio = 0.80, maximumSyntheticCpuRatio = 1.10, maximumJudgedWallRatio = 1.10, preferSmallerWithin = 0.05 },
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
                checks,
                rankings,
                atomics,
                initialization,
                timings,
                decisions,
                failedNumericalOrRejectionCases = checks.Count(row => !row.Result.Passed),
                failedRankings = rankings.Count(row => !row.Passed),
                productionSourcesUnchanged = sourcesUnchanged,
                productionSources,
                experimentSourceSha256 = HashFile(Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "MicrobatchEmbeddingExperimentTests.cs")),
                lifetimePeakWorkingSetBytes = process.PeakWorkingSet64,
                hardware = new { logicalProcessors = Environment.ProcessorCount, memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
                limitation = "Test-only component experiment; lifetime peak includes earlier parity sessions. Per-timing sampled peaks are separate. No production, provider, concurrency, profile or release change. A compatibility failure stops before timing."
            }, Json));
            Assert.True(sourcesUnchanged, "Production C# sources changed during the test-only experiment.");
        }

        void CheckResult(string stage, int width, Input input, VectorResult baseline, VectorResult candidate) => checks.Add(new(stage, Compare(width, input, baseline, candidate, tokenizer, options.Value.Embedding)));
    }

    private static double RunWorkload(Runner runner, ChunkRecord[] workload, int width)
    {
        double checksum = 0;
        foreach (var file in workload.GroupBy(chunk => chunk.RelativePath, StringComparer.Ordinal)) foreach (var batch in file.Select(chunk => new Input(PassageId(chunk), chunk.Content, false)).Chunk(width)) foreach (var vector in runner.Run(batch)) checksum += vector[0];
        return checksum;
    }

    private static void AddRankings(Data data, Dictionary<int, Dictionary<string, VectorResult>> vectors, int width, List<Ranking> rankings)
    {
        foreach (var query in data.Queries)
        {
            var pool = query.Pool == "judged" ? data.Judged : data.Judged.Concat(data.Synthetic).ToArray();
            var baseline = Rank(pool, vectors[1], vectors[1][query.InputId].Vector!); var relevantCount = pool.Count(chunk => Relevant(chunk, query)); Assert.True(relevantCount > 0);
            var baselineRank = Array.FindIndex(baseline, hit => Relevant(hit.Chunk, query)) + 1; var baselineRecall = baseline.Count(hit => Relevant(hit.Chunk, query)) / (double)relevantCount;
            foreach (var (combination, candidateQuery, candidateDocuments) in new[] { ("default/default", false, false), ("candidate/default", true, false), ("default/candidate", false, true), ("candidate/candidate", true, true) })
            {
                var results = Rank(pool, vectors[candidateDocuments ? width : 1], vectors[candidateQuery ? width : 1][query.InputId].Vector!);
                var rank = Array.FindIndex(results, hit => Relevant(hit.Chunk, query)) + 1; var recall = results.Count(hit => Relevant(hit.Chunk, query)) / (double)relevantCount;
                var reciprocal = rank == 0 ? 0 : 1d / rank; var baseReciprocal = baselineRank == 0 ? 0 : 1d / baselineRank;
                var passed = baseline.Select(hit => hit.Chunk.ChunkId).SequenceEqual(results.Select(hit => hit.Chunk.ChunkId), StringComparer.Ordinal) && rank == baselineRank && Math.Abs(recall - baselineRecall) <= MaximumMetricDelta && Math.Abs(reciprocal - baseReciprocal) <= MaximumMetricDelta;
                rankings.Add(new(width, query.InputId, query.Pool, combination, passed, rank, recall, reciprocal, baseline.Select(hit => hit.Chunk.ChunkId).ToArray(), results.Select(hit => hit.Chunk.ChunkId).ToArray(), results.Select(hit => hit.Score).ToArray()));
            }
        }
    }

    private sealed class Runner : IDisposable
    {
        private readonly SessionOptions _sessionOptions = new();
        private readonly EmbeddingOptions _options;
        private readonly BertWordPieceTokenizer _tokenizer;
        private readonly Action<string[]> _trackBatch;
        internal InferenceSession Session { get; }
        internal double InitializationMilliseconds { get; }
        internal int Calls { get; private set; }
        internal object Options => new { _sessionOptions.IntraOpNumThreads, _sessionOptions.InterOpNumThreads, executionMode = _sessionOptions.ExecutionMode.ToString(), graphOptimization = _sessionOptions.GraphOptimizationLevel.ToString(), allDefault = true };

        internal Runner(string modelPath, EmbeddingOptions options, BertWordPieceTokenizer tokenizer, Action<string[]> trackBatch)
        {
            _options = options; _tokenizer = tokenizer; _trackBatch = trackBatch; var timer = Stopwatch.StartNew();
            using var modelLock = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(EmbeddingSequencePolicy.ModelSha256, Convert.ToHexString(SHA256.HashData(modelLock))); Session = new InferenceSession(modelPath, _sessionOptions);
            foreach (var name in new[] { "input_ids", "attention_mask", "token_type_ids" })
            {
                var shape = Session.InputMetadata[name]; Assert.Equal(typeof(long), shape.ElementType); Assert.Equal(new[] { -1, -1 }, shape.Dimensions);
            }
            var output = Session.OutputMetadata.First().Value;
            Assert.Equal(typeof(float), output.ElementType);
            Assert.Equal(new[] { -1, -1, options.Dimensions }, output.Dimensions);
            timer.Stop(); InitializationMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        internal float[][] Run(Input[] inputs)
        {
            _trackBatch(inputs.Select(input => input.Id).ToArray());
            // All validation precedes the call counter and inference, so an invalid row cannot publish a partial batch.
            var tokens = inputs.Select(input => _tokenizer.Encode(Prefix(input, _options), _options.MaximumTokens)).ToArray();
            var length = tokens.Max(ids => EmbeddingSequencePolicy.SelectLength(ids, true)); var rows = inputs.Length;
            var ids = new DenseTensor<long>([rows, length]); var mask = new DenseTensor<long>([rows, length]); var types = new DenseTensor<long>([rows, length]);
            for (var row = 0; row < rows; row++) for (var token = 0; token < length; token++) { ids[row, token] = tokens[row][token]; mask[row, token] = tokens[row][token] == 0 ? 0 : 1; }
            Calls++;
            using var results = Session.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask), NamedOnnxValue.CreateFromTensor("token_type_ids", types)]);
            var hidden = results[0].AsTensor<float>();
            if (hidden.Rank != 3 || hidden.Dimensions[0] != rows || hidden.Dimensions[1] < length || hidden.Dimensions[2] != _options.Dimensions) throw new InvalidOperationException("Experimental output shape mismatch.");
            var vectors = new float[rows][];
            for (var row = 0; row < rows; row++)
            {
                var vector = new float[_options.Dimensions]; var nonPadding = 0;
                for (var token = 0; token < length; token++) { if (mask[row, token] == 0) continue; nonPadding++; for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] += hidden[row, token, dimension]; }
                nonPadding = Math.Max(1, nonPadding); var magnitude = 0d;
                for (var dimension = 0; dimension < vector.Length; dimension++) { vector[dimension] /= nonPadding; magnitude += vector[dimension] * vector[dimension]; }
                magnitude = Math.Sqrt(magnitude); if (magnitude > 0) for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = (float)(vector[dimension] / magnitude);
                vectors[row] = vector;
            }
            return vectors;
        }
        public void Dispose() { Session.Dispose(); _sessionOptions.Dispose(); }
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
    private sealed record Check(string Stage, Comparison Result);
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
    private sealed record Timing(int Mode, int Repetition, string Workload, int Inputs, double ElapsedMilliseconds, double ProcessCpuMilliseconds, long WorkingSetBytes, double Checksum, int ModelCalls, long StartingWorkingSetBytes, long SampledPeakWorkingSetBytes, long LifetimePeakBefore, long LifetimePeakAfter);
    private sealed record SourceFile(string Path, string Sha256);
}



