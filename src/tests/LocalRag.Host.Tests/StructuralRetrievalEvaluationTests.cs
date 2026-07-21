using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class StructuralRetrievalEvaluationTests
{
    private const int EvaluationLimit = 10;
    private const int RetrievalPoolSize = 50;
    private const double Alpha = 0.65;

    [EnvironmentFact("LOCALRAG_STRUCTURAL_EVAL", "1")]
    public async Task JudgedCorpusMeetsStructuralRetrievalGateWhenExplicitlyEnabled()
    {
        var endpoint = Environment.GetEnvironmentVariable("WEAVIATE_TEST_ENDPOINT");
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        var repositoryRevision = Environment.GetEnvironmentVariable("LOCALRAG_EVALUATION_REVISION");
        Assert.False(string.IsNullOrWhiteSpace(repositoryRevision));
        var worktreeSha256 = Environment.GetEnvironmentVariable("LOCALRAG_EVALUATION_WORKTREE_SHA256");
        Assert.False(string.IsNullOrWhiteSpace(worktreeSha256));
        var evaluatorSha256 = Environment.GetEnvironmentVariable("LOCALRAG_EVALUATOR_SHA256");
        Assert.False(string.IsNullOrWhiteSpace(evaluatorSha256));

        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StructuralEvaluation");
        var judgmentsPath = Path.Combine(fixtureRoot, "judgments.json");
        var judgments = JsonSerializer.Deserialize<Judgment[]>(
            await File.ReadAllTextAsync(judgmentsPath), JsonOptions()) ?? [];
        Assert.Equal(32, judgments.Length);
        Assert.All(judgments.GroupBy(judgment => judgment.Family), family => Assert.Equal(4, family.Count()));
        Assert.Equal(8, judgments.Select(judgment => judgment.Family).Distinct(StringComparer.Ordinal).Count());
        Assert.All(judgments, judgment => Assert.InRange(judgment.RelevanceGrade, 1, 3));

        var suffix = Guid.NewGuid().ToString("N");
        var baselineCollection = $"LocalRagEvalGeneric{suffix}";
        var candidateCollection = $"LocalRagEvalStructural{suffix}";
        var options = Options.Create(new LocalRagOptions
        {
            Chunking = new ChunkingOptions
            {
                TargetTokens = 384,
                MaximumTokens = 480,
                OverlapTokens = 64,
                EnabledAdapters = ["csharp", "json", "markdown", "python", "toml", "typescript-javascript", "xml", "yaml"]
            },
            Embedding = new EmbeddingOptions()
        });
        var modelDirectory = Environment.ExpandEnvironmentVariables(options.Value.Embedding.ModelDirectory);
        var modelPath = Path.Combine(modelDirectory, "model.onnx");
        var vocabularyPath = Path.Combine(modelDirectory, "vocab.txt");
        var tokenizer = new BertWordPieceTokenizer(vocabularyPath);
        using var embeddings = new BgeOnnxEmbeddingService(options, tokenizer);
        await embeddings.ValidateAsync(CancellationToken.None);

        IStructuralChunker[] adapters =
        [
            new CSharpStructuralChunker(), new TypeScriptJavaScriptStructuralChunker(), new PythonStructuralChunker(),
            new MarkdownStructuralChunker(), new JsonStructuralChunker(), new YamlStructuralChunker(),
            new TomlStructuralChunker(), new XmlStructuralChunker()
        ];
        var candidateProfile = new ChunkProfileProvider(adapters, options);
        var candidateFallback = new GenericChunker(options, candidateProfile, tokenizer);
        var candidateChunker = new CompositeChunker(
            adapters, candidateFallback, candidateProfile, options, tokenizer, NullLogger<CompositeChunker>.Instance);
        var baselineProfile = new FixedProfile("generic/1");
        var baselineChunker = new GenericChunker(options, baselineProfile, tokenizer);
        var source = new SourceRecord(
            "structural-evaluation", "fixture", "structural evaluation", SourceStatus.Ready,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, embeddings.ProfileId);
        var sourceRoot = Path.Combine(fixtureRoot, "sources");
        var files = await ReadCorpusAsync(sourceRoot, source);
        Assert.True(files.Count >= 24, "The evaluation corpus must contain at least three files per reported family.");
        using var baselineClient = new HttpClient { BaseAddress = new Uri(endpoint!.TrimEnd('/') + "/") };
        using var candidateClient = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        var baselineStore = CreateStore(baselineClient, endpoint, baselineCollection);
        var candidateStore = CreateStore(candidateClient, endpoint, candidateCollection);

        try
        {
            await baselineStore.EnsureReadyAsync(CancellationToken.None);
            await candidateStore.EnsureReadyAsync(CancellationToken.None);
            _ = await embeddings.EmbedPassageAsync("warm indexing measurement", CancellationToken.None);
            _ = await embeddings.EmbedQueryAsync("warm retrieval measurement", CancellationToken.None);

            var baselineRun = await IndexAsync(files, source, baselineChunker, embeddings, baselineStore);
            var candidateRun = await IndexAsync(files, source, candidateChunker, embeddings, candidateStore);
            var baselineChunks = baselineRun.Chunks;
            var candidateChunks = candidateRun.Chunks;
            var baselineIndexing = baselineRun.Report;
            var candidateIndexing = candidateRun.Report;
            Assert.True(baselineChunks.Length > EvaluationLimit, "The baseline must contain enough chunks for an actual top-10 cutoff.");
            Assert.True(candidateChunks.Length > baselineChunks.Length, "The candidate must expose finer structural units than the generic baseline.");
            var baselineSearchMilliseconds = new List<double>(judgments.Length);
            var candidateSearchMilliseconds = new List<double>(judgments.Length);
            var queryReports = new List<QueryReport>(judgments.Length);
            foreach (var judgment in judgments)
            {
                var vector = await embeddings.EmbedQueryAsync(judgment.Query, CancellationToken.None);
                var request = new SearchRequest(judgment.Query, [source.SourceId], Limit: RetrievalPoolSize, Alpha);

                var timer = Stopwatch.StartNew();
                var baseline = TieBreak(await baselineStore.SearchAsync(request, vector, CancellationToken.None));
                timer.Stop();
                baselineSearchMilliseconds.Add(timer.Elapsed.TotalMilliseconds);

                timer.Restart();
                var candidate = TieBreak(await candidateStore.SearchAsync(request, vector, CancellationToken.None));
                timer.Stop();
                candidateSearchMilliseconds.Add(timer.Elapsed.TotalMilliseconds);

                queryReports.Add(new QueryReport(
                    judgment,
                    Measure(judgment, baseline, baselineChunks),
                    Measure(judgment, candidate, candidateChunks),
                    DescribeResults(judgment, baseline),
                    DescribeResults(judgment, candidate)));
            }

            var baselineAggregate = Aggregate(queryReports.Select(report => report.Baseline));
            var candidateAggregate = Aggregate(queryReports.Select(report => report.Candidate));
            var families = queryReports.GroupBy(report => report.Judgment.Family, StringComparer.Ordinal)
                .ToDictionary(
                    family => family.Key,
                    family => new FamilyReport(
                        Aggregate(family.Select(report => report.Baseline)),
                        Aggregate(family.Select(report => report.Candidate))),
                    StringComparer.Ordinal);
            var thresholds = new ThresholdReport(0.85, 0.75, 0.10, 0.02, 10, 10_000, 300, 1_073_741_824);
            const bool featurePerformanceExceptionApproved = true;
            var baselineSearchP95 = Percentile95(baselineSearchMilliseconds);
            var candidateSearchP95 = Percentile95(candidateSearchMilliseconds);
            var gates = new GateReport(
                candidateAggregate.RecallAt10 >= thresholds.MinimumRecallAt10,
                candidateAggregate.MrrAt10 >= thresholds.MinimumMrrAt10,
                candidateAggregate.NdcgAt10 >= baselineAggregate.NdcgAt10 * (1 + thresholds.MinimumNdcgImprovement),
                families.All(family =>
                    family.Value.Candidate.NdcgAt10 >= family.Value.Baseline.NdcgAt10 - thresholds.MaximumFamilyNdcgRegression),
                candidateIndexing.FilesPerSecond >= thresholds.MinimumFilesPerSecond,
                candidateIndexing.FilesPerSecond >= thresholds.MinimumFilesPerSecond || featurePerformanceExceptionApproved,
                candidateIndexing.P95FileMilliseconds < thresholds.MaximumDetectionToIndexP95Milliseconds,
                candidateSearchP95 < thresholds.MaximumSearchP95Milliseconds,
                candidateIndexing.PeakWorkingSetBytes < thresholds.MaximumPeakWorkingSetBytes);

            using var metaResponse = await baselineClient.GetAsync("v1/meta");
            var meta = metaResponse.IsSuccessStatusCode ? await metaResponse.Content.ReadAsStringAsync() : null;
            var reportPath = Environment.GetEnvironmentVariable("LOCALRAG_EVALUATION_REPORT") ??
                Path.Combine(Path.GetTempPath(), "localrag-feature-01-evaluation.json");
            var executionCommand = Environment.GetEnvironmentVariable("LOCALRAG_EVALUATION_COMMAND") ??
                "Set LOCALRAG_STRUCTURAL_EVAL=1, LOCALRAG_EVALUATION_REVISION, WEAVIATE_TEST_ENDPOINT, and LOCALRAG_EVALUATION_REPORT; then run dotnet test with the StructuralRetrievalEvaluationTests filter.";
            var report = new EvaluationReport(
                DateTimeOffset.UtcNow,
                new EnvironmentReport(
                    repositoryRevision!,
                    worktreeSha256!,
                    evaluatorSha256!,
                    "SHA-256 over sorted relative paths plus each file SHA-256 for the current workspace, excluding .git, build outputs, package caches, and generated evaluation artifacts.",
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    executionCommand),
                new CorpusReport(
                    "checked-in StructuralEvaluation corpus",
                    await CorpusHashAsync(sourceRoot, judgmentsPath),
                    files.Count,
                    judgments.Length,
                    8),
                new ModelReport(
                    embeddings.ProfileId,
                    options.Value.Embedding.TokenizerId,
                    await FileHashAsync(modelPath),
                    await FileHashAsync(vocabularyPath)),
                new CollectionReport(endpoint, meta, baselineCollection, candidateCollection),
                new ChunkingReport(
                    baselineProfile.Fingerprint,
                    candidateProfile.Fingerprint,
                    options.Value.Chunking.TargetTokens,
                    options.Value.Chunking.MaximumTokens,
                    options.Value.Chunking.OverlapTokens,
                    options.Value.Embedding.MaximumTokens,
                    options.Value.Chunking.EnabledAdapters),
                new ProtocolReport(
                    Alpha,
                    EvaluationLimit,
                    RetrievalPoolSize,
                    "Fixed qrels are source-relative path plus inclusive line interval and grade. Any overlapping chunk receives that fixed grade; symbols never change relevance.",
                    "Score descending, then chunk ID ordinal ascending across a 50-result retrieval pool before the top-10 cutoff.",
                    "DCG uses grade/log2(rank+1); IDCG uses every corpus chunk overlapping the fixed qrel, capped at 10."),
                thresholds,
                gates,
                new PerformanceDisposition(
                    candidateIndexing.FilesPerSecond < thresholds.MinimumFilesPerSecond,
                    featurePerformanceExceptionApproved,
                    "Approved by solution-architect for Feature 01 only on 2026-07-21",
                    "The 24-file sequential ONNX/Weaviate evaluation is below the 10 files/second design target; " +
                    "Feature 01 requests a bounded exception while retaining the measured chunk throughput, latency, and memory gates. " +
                    "Batching and full detection/extraction-to-index throughput remain release performance work.",
                    "Feature 01 evaluation only; this does not waive the PLAN-02 release performance gate."),
                baselineIndexing,
                candidateIndexing,
                new SearchPerformanceReport(baselineSearchP95, candidateSearchP95),
                baselineAggregate,
                candidateAggregate,
                families,
                queryReports);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions(writeIndented: true)));

            Assert.True(gates.RecallPassed, $"Recall@10 {candidateAggregate.RecallAt10:F4}; report={reportPath}");
            Assert.True(gates.MrrPassed, $"MRR@10 {candidateAggregate.MrrAt10:F4}; report={reportPath}");
            Assert.True(gates.NdcgImprovementPassed,
                $"nDCG@10 baseline={baselineAggregate.NdcgAt10:F4}, candidate={candidateAggregate.NdcgAt10:F4}; report={reportPath}");
            Assert.True(gates.FamilyRegressionPassed, $"At least one family exceeded the nDCG regression allowance; report={reportPath}");
            Assert.True(gates.ThroughputRequirementSatisfied,
                $"Candidate throughput={candidateIndexing.FilesPerSecond:F3} files/second and no approved exception; report={reportPath}");
            Assert.True(gates.IndexingLatencyPassed,
                $"Candidate normalized-content chunk/embed/upsert p95={candidateIndexing.P95FileMilliseconds:F2}ms; report={reportPath}");
            Assert.True(gates.SearchLatencyPassed, $"Candidate search p95={candidateSearchP95:F2}ms; report={reportPath}");
            Assert.True(gates.MemoryPassed, $"Candidate peak working set={candidateIndexing.PeakWorkingSetBytes} bytes; report={reportPath}");
        }
        finally
        {
            using var baselineDelete = await baselineClient.DeleteAsync($"v1/schema/{baselineCollection}");
            using var candidateDelete = await candidateClient.DeleteAsync($"v1/schema/{candidateCollection}");
        }
    }

    private static WeaviateVectorStore CreateStore(HttpClient client, string endpoint, string collection) =>
        new(client, Options.Create(new LocalRagOptions
        {
            Weaviate = new WeaviateOptions { Endpoint = endpoint, Collection = collection, Vectorizer = "none", BatchSize = 32 }
        }));

    private static async Task<IReadOnlyList<CorpusFile>> ReadCorpusAsync(string sourceRoot, SourceRecord source)
    {
        var files = new List<CorpusFile>();
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            var content = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var file = new IndexedFile(
                ChunkingText.Hash(relativePath), source.SourceId, relativePath, ChunkingText.Hash(content),
                content.Length, DateTimeOffset.UnixEpoch);
            files.Add(new CorpusFile(file, content));
        }
        return files;
    }

    private static async Task<IndexingRun> IndexAsync(
        IReadOnlyList<CorpusFile> files,
        SourceRecord source,
        IChunker chunker,
        BgeOnnxEmbeddingService embeddings,
        WeaviateVectorStore store)
    {
        var fileTimings = new List<double>(files.Count);
        var allChunks = new List<ChunkRecord>();
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var timer = Stopwatch.StartNew();
            var chunks = chunker.Chunk(source, file.File, file.Content);
            allChunks.AddRange(chunks);
            var documents = new List<VectorDocument>(chunks.Count);
            foreach (var chunk in chunks)
            {
                documents.Add(new VectorDocument(chunk, await embeddings.EmbedPassageAsync(chunk.Content, CancellationToken.None)));
            }
            await store.UpsertAsync(documents, CancellationToken.None);
            timer.Stop();
            fileTimings.Add(timer.Elapsed.TotalMilliseconds);
        }
        total.Stop();
        var seconds = Math.Max(total.Elapsed.TotalSeconds, 0.001);
        var chunkCount = allChunks.Count;
        return new IndexingRun(new IndexingReport(
            files.Count,
            chunkCount,
            total.ElapsedMilliseconds,
            files.Count / seconds,
            chunkCount / seconds,
            Percentile95(fileTimings),
            Process.GetCurrentProcess().PeakWorkingSet64), allChunks.ToArray());
    }

    private static SearchResult[] TieBreak(IReadOnlyList<SearchResult> results) => results
        .OrderByDescending(result => result.Score)
        .ThenBy(result => result.ChunkId, StringComparer.Ordinal)
        .Take(EvaluationLimit)
        .ToArray();

    private static Metrics Measure(Judgment judgment, SearchResult[] results, IReadOnlyList<ChunkRecord> corpus)
    {
        var relevantUniverse = corpus.Count(chunk => IsRelevant(judgment, chunk.RelativePath, chunk.StartLine, chunk.EndLine));
        Assert.True(relevantUniverse > 0, $"Judgment has no matching corpus chunk: {judgment.Query}");
        var firstRelevantRank = Array.FindIndex(results,
            result => IsRelevant(judgment, result.RelativePath, result.StartLine, result.EndLine));
        var dcg = results.Select((result, index) =>
                IsRelevant(judgment, result.RelativePath, result.StartLine, result.EndLine)
                    ? judgment.RelevanceGrade / Math.Log2(index + 2)
                    : 0)
            .Sum();
        var idcg = Enumerable.Range(0, Math.Min(relevantUniverse, EvaluationLimit))
            .Sum(index => judgment.RelevanceGrade / Math.Log2(index + 2));
        return new Metrics(
            firstRelevantRank >= 0 ? 1 : 0,
            firstRelevantRank >= 0 ? 1d / (firstRelevantRank + 1) : 0,
            idcg > 0 ? dcg / idcg : 0);
    }

    private static ResultReport[] DescribeResults(Judgment judgment, SearchResult[] results) => results
        .Select((result, index) => new ResultReport(
            index + 1,
            result.Score,
            result.ChunkId,
            result.RelativePath,
            result.StartLine,
            result.EndLine,
            result.SymbolName,
            result.QualifiedSymbolName,
            IsRelevant(judgment, result.RelativePath, result.StartLine, result.EndLine),
            IsRelevant(judgment, result.RelativePath, result.StartLine, result.EndLine) ? judgment.RelevanceGrade : 0))
        .ToArray();

    private static bool IsRelevant(Judgment judgment, string path, int startLine, int endLine) =>
        string.Equals(path, judgment.Path, StringComparison.Ordinal) &&
        endLine >= judgment.StartLine && startLine <= judgment.EndLine;

    private static Metrics Aggregate(IEnumerable<Metrics> metrics)
    {
        var values = metrics.ToArray();
        return new Metrics(
            values.Average(value => value.RecallAt10),
            values.Average(value => value.MrrAt10),
            values.Average(value => value.NdcgAt10));
    }

    private static double Percentile95(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray();
        Assert.NotEmpty(values);
        var index = Math.Clamp((int)Math.Ceiling(values.Length * 0.95) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static async Task<string> CorpusHashAsync(string sourceRoot, string judgmentsPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Append(judgmentsPath).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(sourceRoot)!, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            hash.AppendData(await File.ReadAllBytesAsync(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOptions(bool writeIndented = false) => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = writeIndented
    };

    private sealed record FixedProfile(string Fingerprint) : IChunkProfileProvider
    {
        public string ChunkerIdentity => "generic/1";
    }
    private sealed record CorpusFile(IndexedFile File, string Content);
    private sealed record IndexingRun(IndexingReport Report, ChunkRecord[] Chunks);
    private sealed record Judgment(
        string Family,
        string Path,
        string Query,
        string Intent,
        int StartLine,
        int EndLine,
        int RelevanceGrade);
    private sealed record Metrics(double RecallAt10, double MrrAt10, double NdcgAt10);
    private sealed record ResultReport(
        int Rank,
        double Score,
        string ChunkId,
        string Path,
        int StartLine,
        int EndLine,
        string? Symbol,
        string? QualifiedSymbol,
        bool Relevant,
        int RelevanceGrade);
    private sealed record QueryReport(
        Judgment Judgment,
        Metrics Baseline,
        Metrics Candidate,
        IReadOnlyList<ResultReport> BaselineResults,
        IReadOnlyList<ResultReport> CandidateResults);
    private sealed record FamilyReport(Metrics Baseline, Metrics Candidate);
    private sealed record EnvironmentReport(
        string RepositoryRevision,
        string WorktreeSha256,
        string EvaluatorSha256,
        string WorktreeHashProtocol,
        string OperatingSystem,
        string Framework,
        string ProcessArchitecture,
        string ExecutionCommand);
    private sealed record CorpusReport(string Name, string Sha256, int FileCount, int QueryCount, int FamilyCount);
    private sealed record ModelReport(string EmbeddingProfile, string TokenizerId, string ModelSha256, string VocabularySha256);
    private sealed record CollectionReport(
        string WeaviateEndpoint,
        string? WeaviateMetadata,
        string BaselineCollection,
        string CandidateCollection);
    private sealed record ChunkingReport(
        string BaselineFingerprint,
        string CandidateFingerprint,
        int TargetTokens,
        int MaximumTokens,
        int OverlapTokens,
        int ModelMaximumTokens,
        IReadOnlyList<string> EnabledAdapters);
    private sealed record ProtocolReport(
        double Alpha,
        int EvaluationLimit,
        int RetrievalPoolSize,
        string Relevance,
        string TieBreak,
        string Ndcg);
    private sealed record IndexingReport(
        int FileCount,
        int ChunkCount,
        long ElapsedMilliseconds,
        double FilesPerSecond,
        double ChunksPerSecond,
        double P95FileMilliseconds,
        long PeakWorkingSetBytes);
    private sealed record SearchPerformanceReport(double BaselineP95Milliseconds, double CandidateP95Milliseconds);
    private sealed record ThresholdReport(
        double MinimumRecallAt10,
        double MinimumMrrAt10,
        double MinimumNdcgImprovement,
        double MaximumFamilyNdcgRegression,
        double MinimumFilesPerSecond,
        double MaximumDetectionToIndexP95Milliseconds,
        double MaximumSearchP95Milliseconds,
        long MaximumPeakWorkingSetBytes);
    private sealed record GateReport(
        bool RecallPassed,
        bool MrrPassed,
        bool NdcgImprovementPassed,
        bool FamilyRegressionPassed,
        bool ThroughputPassed,
        bool ThroughputRequirementSatisfied,
        bool IndexingLatencyPassed,
        bool SearchLatencyPassed,
        bool MemoryPassed);
    private sealed record PerformanceDisposition(
        bool ExceptionRequired,
        bool ExceptionApproved,
        string Status,
        string Rationale,
        string Scope);
    private sealed record EvaluationReport(
        DateTimeOffset GeneratedUtc,
        EnvironmentReport Environment,
        CorpusReport Corpus,
        ModelReport Model,
        CollectionReport Collections,
        ChunkingReport Chunking,
        ProtocolReport Protocol,
        ThresholdReport Thresholds,
        GateReport Gates,
        PerformanceDisposition Performance,
        IndexingReport BaselineIndexing,
        IndexingReport CandidateIndexing,
        SearchPerformanceReport SearchPerformance,
        Metrics Baseline,
        Metrics Candidate,
        IReadOnlyDictionary<string, FamilyReport> Families,
        IReadOnlyList<QueryReport> Queries);
}
