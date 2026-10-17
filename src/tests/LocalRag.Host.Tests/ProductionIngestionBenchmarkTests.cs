using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

/// <summary>Measurement-only benchmark. Completion is not a pass of the original engineering throughput target.</summary>
public sealed class ProductionIngestionBenchmarkTests
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int WarmRepetitions = 3;

    [AllEnvironmentFact("LOCALRAG_PRODUCTION_INGEST_BENCHMARK=1", "LOCALRAG_LANCEDB_NATIVE_MANIFEST", "WEAVIATE_TEST_ENDPOINT", "LOCALRAG_ONNX_TESTS=1")]
    public async Task MeasureActualProductionInitialIndexingWithUnchangedDefaults()
    {
        var repository = RepositoryRoot();
        var root = Environment.GetEnvironmentVariable("LOCALRAG_PRODUCTION_INGEST_DIRECTORY") ?? Path.Combine(repository, "artifacts", "runs", "production-ingestion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "Fixtures", "StructuralEvaluation", "sources");
        var syntheticRoot = Path.Combine(root, "synthetic-definition");
        Directory.CreateDirectory(syntheticRoot);
        for (var file = 0; file < 24; file++)
        {
            var content = new StringBuilder();
            for (var line = 0; line < 64; line++)
                content.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Reference {file:D2} paragraph {line:D2}: deterministic indexing explains source identities, durable recovery, vector search and lifecycle reconciliation.");
            await File.WriteAllTextAsync(Path.Combine(syntheticRoot, $"reference-{file:D2}.txt"), content.ToString(), new UTF8Encoding(false));
        }

        Corpus[] corpora = [Describe("complete-judged-24", sourceRoot), Describe("fixed-synthetic-24", syntheticRoot)];
        Assert.All(corpora, corpus => Assert.Equal(24, corpus.Files.Length));
        var nativeManifest = Path.Combine(AppContext.BaseDirectory, "lancedb-native.manifest.json");
        Assert.True(File.Exists(nativeManifest), "Stage the recorded native package in the test output directory first.");
        using var expectedNative = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!));
        using var stagedNative = JsonDocument.Parse(await File.ReadAllTextAsync(nativeManifest));
        Assert.Equal(expectedNative.RootElement.GetProperty("sha256").GetString(), stagedNative.RootElement.GetProperty("sha256").GetString());
        var process = Process.GetCurrentProcess();
        var report = new List<object>();
        var createdUtc = DateTimeOffset.UtcNow;
        try
        {
            // Fixed provider/corpus order and repetition count are declared before observing any production timings.
            foreach (var provider in new[] { "LanceDb", "Weaviate" })
            {
                var providerRoot = Path.Combine(root, provider);
                var collection = "ProductionIngest" + Guid.NewGuid().ToString("N");
                var endpoint = Environment.GetEnvironmentVariable("WEAVIATE_TEST_ENDPOINT")!;
                var create = Stopwatch.StartNew();
                await using var app = HostApplication.Create([
                    "--contentRoot", Path.Combine(repository, "src", "LocalRag.Host"), "--urls", "http://127.0.0.1:0", "--environment", "Production",
                    "--LocalRag:DataDirectory", Path.Combine(providerRoot, "state"), "--LocalRag:Authentication:Token", "bounded-ingestion-test-token",
                    "--LocalRag:VectorStore:Provider", provider, "--LocalRag:VectorStore:LanceDb:Path", Path.Combine(providerRoot, "vectors"),
                    "--LocalRag:VectorStore:Weaviate:Endpoint", endpoint, "--LocalRag:VectorStore:Weaviate:Collection", collection
                ]);
                create.Stop();
                var startup = Stopwatch.StartNew(); await app.StartAsync(); startup.Stop();
                var options = app.Services.GetRequiredService<IOptions<LocalRagOptions>>().Value;
                var productionEmbeddings = (LocalRag.Infrastructure.Embeddings.BgeOnnxEmbeddingService)app.Services.GetRequiredService<IEmbeddingService>();
                Assert.Equal(4, options.Indexing.MaxConcurrentFiles);
                Assert.Equal(1500, options.Indexing.StabilityIntervalMilliseconds);
                using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromMinutes(2) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", "bounded-ingestion-test-token");
                var rounds = new List<Round>();
                object? embeddingReplay = null;
                IReadOnlyList<ChunkRecord>? judgedReplay = null;
                try
                {
                    foreach (var corpus in corpora)
                    {
                        var firstRound = corpus.Name == corpora[0].Name ? 0 : 1;
                        for (var round = firstRound; round <= WarmRepetitions; round++)
                        {
                            var destination = Path.Combine(providerRoot, "sources", corpus.Name + "-" + round);
                            foreach (var file in corpus.Files)
                            {
                                var target = Path.Combine(destination, file.RelativePath);
                                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                File.Copy(Path.Combine(corpus.Root, file.RelativePath), target);
                            }
                            var cpuStart = process.TotalProcessorTime;
                            var indexedBefore = JsonSerializer.SerializeToElement(app.Services.GetRequiredService<LocalRag.Infrastructure.Diagnostics.OperationalMetrics>().Snapshot()).GetProperty("indexedFiles").GetInt64();
                            var timer = Stopwatch.StartNew();
                            var registration = await client.PostAsJsonAsync("/api/v1/catalog/sources", new { rootPath = destination, name = corpus.Name + "-" + round, description = "Predeclared production ingestion benchmark", scope = "global" });
                            registration.EnsureSuccessStatusCode();
                            var id = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source").GetProperty("sourceId").GetString()!;
                            var state = await WaitForCompletionAsync(app, id, 1);
                            timer.Stop();
                            Assert.True(productionEmbeddings.UsesDynamicPadding, "Approved optimization benchmark must exercise the validated dynamic path, not fixed fallback.");
                            var cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                            var chunks = await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(id, CancellationToken.None);
                            Assert.Equal(corpus.Files.Length, chunks.Select(chunk => chunk.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                            var indexedAfter = JsonSerializer.SerializeToElement(app.Services.GetRequiredService<LocalRag.Infrastructure.Diagnostics.OperationalMetrics>().Snapshot()).GetProperty("indexedFiles").GetInt64();
                            Assert.Equal(24, indexedAfter - indexedBefore);
                            Assert.Equal(0, state.LastDeletedFiles);
                            // Watcher hints can finish an unchanged successor before polling observes Clean.
                            // Measure immutable generation-one timestamps instead of mutable last-run counters.
                            var processingMilliseconds = await InitialProcessingMillisecondsAsync(app, id);
                            process.Refresh();
                            rounds.Add(new(corpus.Name, round, round != 0, id, corpus.Files.Length, chunks.Count, timer.Elapsed.TotalMilliseconds, processingMilliseconds, cpuMilliseconds, process.WorkingSet64, corpus.Files.Length / (processingMilliseconds / 1000), chunks.Count / (processingMilliseconds / 1000)));
                            if (round == WarmRepetitions)
                            {
                                var generation = state.CompletedGeneration;
                                var unchanged = Stopwatch.StartNew();
                                (await client.PostAsJsonAsync($"/api/v1/sources/{id}/reindex", new { })).EnsureSuccessStatusCode();
                                var reconciled = await WaitForCompletionAsync(app, id, generation + 1);
                                unchanged.Stop();
                                Assert.Equal(0, reconciled.LastChangedFiles); Assert.Equal(24, reconciled.LastUnchangedFiles);
                                var after = await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(id, CancellationToken.None);
                                Assert.Equal(chunks.OrderBy(chunk => chunk.ChunkId).Select(chunk => (chunk.ChunkId, chunk.LastIndexedUtc)), after.OrderBy(chunk => chunk.ChunkId).Select(chunk => (chunk.ChunkId, chunk.LastIndexedUtc)));
                                report.Add(new { provider, corpus = corpus.Name, unchangedReconciliationObservedMilliseconds = unchanged.Elapsed.TotalMilliseconds, changedFiles = reconciled.LastChangedFiles, unchangedFiles = reconciled.LastUnchangedFiles, distinctFromInitialIngestionTarget = true });
                                if (provider == "LanceDb" && corpus.Name == corpora[0].Name)
                                    judgedReplay = chunks;
                            }
                        }
                    }
                    if (judgedReplay is not null) embeddingReplay = await ReplayEmbeddingComponentAsync(app, judgedReplay, options.Embedding);
                    var summaries = corpora.Select(corpus =>
                    {
                        var samples = rounds.Where(round => round.Corpus == corpus.Name && round.WarmModelAndProvider).Select(round => round.ProcessingFilesPerSecond).Order().ToArray();
                        return new { corpus = corpus.Name, samples = samples.Length, minimumFilesPerSecond = samples[0], medianFilesPerSecond = samples[samples.Length / 2], maximumFilesPerSecond = samples[^1], originalEngineeringTargetLowerBound = 10, meetsOriginalEngineeringTarget = samples[samples.Length / 2] >= 10 };
                    }).ToArray();
                    report.Add(new { provider, collection = provider == "Weaviate" ? collection : null, productionDynamicPaddingEligible = productionEmbeddings.UsesDynamicPadding, constructionMilliseconds = create.Elapsed.TotalMilliseconds, startupMilliseconds = startup.Elapsed.TotalMilliseconds, defaults = new { options.Indexing, options.Chunking, embedding = new { options.Embedding.ProfileId, options.Embedding.MaximumTokens, options.Embedding.Dimensions }, vectorBatchSize = provider == "LanceDb" ? options.VectorStore.LanceDb.BatchSize : options.VectorStore.Weaviate.BatchSize }, rounds, summaries, embeddingReplay, metrics = app.Services.GetRequiredService<LocalRag.Infrastructure.Diagnostics.OperationalMetrics>().Snapshot() });
                }
                finally
                {
                    await app.StopAsync();
                    if (provider == "Weaviate")
                    {
                        using var cleanup = new HttpClient();
                        await cleanup.DeleteAsync(endpoint.TrimEnd('/') + "/v1/schema/" + collection);
                    }
                }
            }
        }
        finally
        {
            var modelDirectory = Environment.ExpandEnvironmentVariables(new EmbeddingOptions().ModelDirectory);
            var sourceFiles = Directory.EnumerateFiles(Path.Combine(repository, "src", "LocalRag.Host"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(Path.Combine(repository, "src", "LocalRag.Host"), path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                .Append(Path.Combine(repository, "src", "LocalRag.Host", "appsettings.json")).Append(Path.Combine(repository, "src", "tests", "LocalRag.Host.Tests", "ProductionIngestionBenchmarkTests.cs"))
                .Order(StringComparer.Ordinal).Select(path => new { path = Path.GetRelativePath(repository, path).Replace('\\', '/'), sha256 = HashFile(path) }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(root, "production-ingestion.json"), JsonSerializer.Serialize(new
            {
                createdUtc,
                completedUtc = DateTimeOffset.UtcNow,
                methodology = "Actual production HostApplication and HTTP registration -> durable queue -> IndexWorker -> IndexCoordinator -> filesystem policy/extraction -> production chunker -> real ONNX -> real provider upsert/delete -> SQLite completion. No indexing/chunking/embedding defaults overridden. Fixed provider order LanceDb then Weaviate; first judged run includes cold model/provider initialization; three warm fresh-source runs for each predefined corpus. Stopwatch request-to-Ready includes polling (50ms); immutable completed generation-one IndexJobs.StartedUtc-to-CompletedUtc is processing time. Copies/preparation excluded. All repetitions retained. Component replay is diagnostic, not ingestion throughput. Benchmark completion is not a target pass.",
                hardware = new { logicalProcessors = Environment.ProcessorCount, availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), description = Environment.GetEnvironmentVariable("LOCALRAG_BENCHMARK_HARDWARE_DESCRIPTION") },
                corpus = corpora.Select(corpus => new { corpus.Name, totalBytes = corpus.Files.Sum(file => file.Bytes), corpus.Files }),
                modelSha256 = HashFile(Path.Combine(modelDirectory, "model.onnx")),
                vocabularySha256 = HashFile(Path.Combine(modelDirectory, "vocab.txt")),
                nativeManifestSha256 = HashFile(nativeManifest),
                nativeDllSha256 = HashFile(Path.Combine(AppContext.BaseDirectory, stagedNative.RootElement.GetProperty("libraryFile").GetString()!)),
                peakProcessWorkingSetBytes = process.PeakWorkingSet64,
                sourceFiles,
                reports = report
            }, ReportJson));
        }
    }

    private static async Task<object> ReplayEmbeddingComponentAsync(WebApplication app, IReadOnlyList<ChunkRecord> chunks, EmbeddingOptions options)
    {
        var embeddings = app.Services.GetRequiredService<IEmbeddingService>();
        var tokens = app.Services.GetRequiredService<IChunkTokenCounter>();
        var samples = new List<object>(); var documents = new List<VectorDocument>(); var timer = Stopwatch.StartNew();
        foreach (var chunk in chunks)
        {
            var sample = Stopwatch.StartNew(); var vector = await embeddings.EmbedPassageAsync(chunk.Content, CancellationToken.None); sample.Stop();
            documents.Add(new(chunk, vector));
            samples.Add(new { chunk.ChunkId, chunk.RelativePath, tokens = tokens.CountTokens(options.PassagePrefix + chunk.Content), configuredMaximumTokens = options.MaximumTokens, actualSequenceLength = ((LocalRag.Infrastructure.Embeddings.BgeOnnxEmbeddingService)embeddings).UsesDynamicPadding ? Math.Max(3, tokens.CountTokens(options.PassagePrefix + chunk.Content)) : options.MaximumTokens, milliseconds = sample.Elapsed.TotalMilliseconds });
        }
        timer.Stop();
        var perFile = new List<object>(); var vectors = app.Services.GetRequiredService<IVectorStore>();
        foreach (var file in documents.GroupBy(document => document.Chunk.RelativePath))
        {
            var write = Stopwatch.StartNew(); await vectors.UpsertAsync(file.ToArray(), CancellationToken.None); write.Stop();
            perFile.Add(new { path = file.Key, chunks = file.Count(), milliseconds = write.Elapsed.TotalMilliseconds });
        }
        var session = (Lazy<Microsoft.ML.OnnxRuntime.InferenceSession>)typeof(LocalRag.Infrastructure.Embeddings.BgeOnnxEmbeddingService).GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(embeddings)!;
        var modelInputs = session.Value.InputMetadata.ToDictionary(pair => pair.Key, pair => pair.Value.Dimensions);
        var modelOutputs = session.Value.OutputMetadata.ToDictionary(pair => pair.Key, pair => pair.Value.Dimensions);
        return new { modelInputs, modelOutputs, description = "After all timed ingestion rounds: isolated warm production embedding replay and idempotent per-file vector-upsert replay of already-indexed judged chunks; diagnostic components, not pipeline spans", milliseconds = timer.Elapsed.TotalMilliseconds, chunks = samples, perFileVectorUpserts = perFile };
    }

    private static async Task<SourceReconciliation> WaitForCompletionAsync(WebApplication app, string id, long generation)
    {
        var store = app.Services.GetRequiredService<IReconciliationStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (true)
        {
            var state = await store.GetAsync(id, timeout.Token);
            if (state is not null && state.CompletedGeneration >= generation && state.State == ReconciliationState.Clean) return state;
            if (state?.State == ReconciliationState.Degraded) throw new InvalidOperationException(state.LastErrorSummary);
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task<double> InitialProcessingMillisecondsAsync(WebApplication app, string id)
    {
        await using var connection = await app.Services.GetRequiredService<LocalRag.Infrastructure.Sqlite.SqliteDatabase>().OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StartedUtc, CompletedUtc FROM IndexJobs WHERE SourceId = $id AND Generation = 1 AND Status = 'Completed';";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        var started = DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture);
        var completed = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
        return (completed - started).TotalMilliseconds;
    }
    private static Corpus Describe(string name, string root) => new(name, root, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => new CorpusFile(Path.GetRelativePath(root, path), new FileInfo(path).Length, HashFile(path))).ToArray());
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "LocalRag.Host", "LocalRag.Host.csproj"))) directory = directory.Parent;
        return directory!.FullName;
    }
    private sealed record Corpus(string Name, string Root, CorpusFile[] Files);
    private sealed record CorpusFile(string RelativePath, long Bytes, string Sha256);
    private sealed record Round(string Corpus, int Repetition, bool WarmModelAndProvider, string SourceId, int Files, int Chunks, double RequestToReadyMilliseconds, double ProcessingMilliseconds, double ProcessCpuMilliseconds, long WorkingSetBytes, double ProcessingFilesPerSecond, double ProcessingChunksPerSecond);
}
