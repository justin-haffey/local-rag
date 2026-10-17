using System.Diagnostics;
using System.Text.Json;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class LanceDbPerformanceIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [NativeIntegrationFact]
    [Trait("Category", "NativePerformance")]
    public async Task ThousandRowMaintenanceUnindexedTailAndSearchDuringIngestionStayBounded()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!;
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires a manifest.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOCALRAG_NATIVE_RUN_DIRECTORY")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "runs", "native-performance"), Guid.NewGuid().ToString("N"));
        var options = Options.Create(new LocalRagOptions { DataDirectory = Path.Combine(root, "state") });
        using var ownership = new InstallationOwnershipStore(options);
        using var store = new LanceDbVectorStore(
            new LanceDbOptions { Path = Path.Combine(root, "vectors"), Table = "rag_chunks_v1", BatchSize = 100 },
            ownership, new LanceDbIpcCodec(), () => new LanceDbNativeApi(manifest));
        await store.EnsureReadyAsync(CancellationToken.None);
        var vector = new float[384]; vector[0] = 1;
        var timer = Stopwatch.StartNew();
        await store.UpsertAsync(Enumerable.Range(0, 1000).Select(index => Document(index, vector)).ToArray(), CancellationToken.None);
        var ingestionMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await store.OptimizeAsync(CancellationToken.None);
        var maintenanceMilliseconds = timer.Elapsed.TotalMilliseconds;
        var tailVector = new float[384]; tailVector[1] = 1;
        var tail = Document(1000, tailVector) with { Chunk = Document(1000, tailVector).Chunk with { Content = "zebracorn exclusive unindexed tail" } };
        await store.UpsertAsync([tail], CancellationToken.None);
        var lexicalTail = new SearchRequest("zebracorn", ["benchmark"], 1, Mode: SearchMode.Lexical);
        var vectorTail = lexicalTail with { Mode = SearchMode.Vector };
        Assert.Equal(tail.Chunk.ChunkId, Assert.Single(await store.SearchAsync(lexicalTail, new float[384], CancellationToken.None)).ChunkId);
        Assert.Equal(tail.Chunk.ChunkId, Assert.Single(await store.SearchAsync(vectorTail, tailVector, CancellationToken.None)).ChunkId);
        timer.Restart();
        await store.OptimizeAsync(CancellationToken.None);
        var tailMaintenanceMilliseconds = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(tail.Chunk.ChunkId, Assert.Single(await store.SearchAsync(lexicalTail, new float[384], CancellationToken.None)).ChunkId);
        Assert.Equal(tail.Chunk.ChunkId, Assert.Single(await store.SearchAsync(vectorTail, tailVector, CancellationToken.None)).ChunkId);
        var samples = new List<double>();
        var writes = Task.Run(async () =>
        {
            for (var index = 1001; index < 1031; index++)
            {
                await store.UpsertAsync([Document(index, vector)], CancellationToken.None);
                await Task.Delay(5);
            }
        });
        for (var index = 0; index < 60; index++)
        {
            timer.Restart();
            var result = await store.SearchAsync(new SearchRequest("native benchmark", ["benchmark"], 5), vector, CancellationToken.None);
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.InRange(result.Count, 1, 5);
        }
        await writes;
        var ownerId = await ownership.GetOrCreateAsync(CancellationToken.None);
        var ownershipSamples = new List<double>();
        for (var index = 0; index < 100; index++)
        {
            timer.Restart();
            Assert.True(await store.VerifyOwnershipAsync(ownerId, CancellationToken.None));
            ownershipSamples.Add(timer.Elapsed.TotalMilliseconds);
        }
        var codec = new LanceDbIpcCodec();
        var batch = Enumerable.Range(0, 100).Select(index => Document(index, vector)).ToArray();
        timer.Restart();
        var encodedBytes = 0;
        for (var index = 0; index < 100; index++) encodedBytes = codec.EncodeDocuments(batch).Length;
        var encodeBatchMeanMilliseconds = timer.Elapsed.TotalMilliseconds / 100;
        var sorted = samples.Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        var process = Process.GetCurrentProcess();
        var report = new
        {
            createdUtc = DateTimeOffset.UtcNow,
            initialRows = 1000,
            dimensions = 384,
            batchSize = 100,
            ingestionMilliseconds,
            rowsPerSecondIncludingThresholdMaintenance = 1000 / (ingestionMilliseconds / 1000),
            maintenanceMilliseconds,
            tailMaintenanceMilliseconds,
            unindexedLexicalAndVectorTailComplete = true,
            concurrentIncrementalRows = 30,
            searchSamples = samples.Count,
            p50Milliseconds = sorted[sorted.Length / 2],
            p95Milliseconds = p95,
            ownershipAbiRoundTripMeanMilliseconds = ownershipSamples.Average(),
            ownershipAbiRoundTripSamples = ownershipSamples.Count,
            ownershipAbiBoundary = "Managed provider lease, FFI call and real native schema/ownership read; includes native work and is not an isolated empty-call overhead estimate.",
            encodeBatchMeanMilliseconds,
            encodedBatchRows = batch.Length,
            encodedBytes,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            nativeLibraryBytes = new FileInfo(Path.Combine(Path.GetDirectoryName(manifest)!, "localrag_lancedb.dll")).Length,
            providerDataBytes = Directory.GetFiles(Path.Combine(root, "vectors"), "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length),
            methodology = "Run this class alone after compiler/test workers are idle; real native 1000-row store, supplied finite vectors, automatic threshold maintenance, explicit optimization and unindexed-tail checks, warm hybrid search while 30 incremental upserts run. Excludes ONNX and filesystem extraction; does not claim end-to-end file throughput.",
            maximumSearchP95Milliseconds = 300,
            maximumPeakWorkingSetBytes = 1_073_741_824
        };
        var reportPath = Environment.GetEnvironmentVariable("LOCALRAG_NATIVE_PERFORMANCE_REPORT") ?? Path.Combine(root, "native-performance.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        Assert.True(p95 < 300, $"Search p95={p95:F2}ms; report={reportPath}");
        Assert.True(process.PeakWorkingSet64 < 1_073_741_824, $"Peak working set={process.PeakWorkingSet64}; report={reportPath}");
    }

    private static VectorDocument Document(int index, IReadOnlyList<float> vector) => new(
        new ChunkRecord($"benchmark-{index:D4}", "benchmark", $"file-{index}", $"files/{index}.txt", "text", null,
            1, 1, 0, $"Native benchmark document {index} supports local retrieval.", $"hash-{index}", 12,
            "test", DateTimeOffset.UnixEpoch, StructuralLocator: "line:1"), vector);
}
