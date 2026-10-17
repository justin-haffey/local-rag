using LocalRag.Domain;
using LocalRag.Infrastructure.LanceDb;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class LanceDbNativeIntegrationTests
{
    private static readonly string[] ExpectedVectorOrder = ["vector-a", "vector-z", "lexical"];

    [NativeIntegrationFact]
    [Trait("Category", "NativeIntegration")]
    public void MissingCorruptWrongRidAndAbiPackagesFailBeforeOpeningAnyStore()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!;
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires a manifest.");
        var root = RunRoot();
        Directory.CreateDirectory(root);
        try
        {
            var original = JsonNode.Parse(File.ReadAllText(manifest))!;
            var packageRoot = Path.GetDirectoryName(manifest)!;
            foreach (var fault in new[] { "missing", "corrupt", "rid", "abi" })
            {
                var folder = Path.Combine(root, fault);
                Directory.CreateDirectory(folder);
                foreach (var file in Directory.GetFiles(packageRoot)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                var mutated = original.DeepClone();
                var library = Path.Combine(folder, "localrag_lancedb.dll");
                if (fault == "missing") File.Delete(library);
                if (fault == "corrupt")
                {
                    File.WriteAllBytes(library, [1, 2, 3, 4]);
                    mutated["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library)));
                }
                if (fault == "rid") mutated["rid"] = "linux-x64";
                if (fault == "abi") mutated["abiVersion"] = 2;
                var mutatedManifest = Path.Combine(folder, "lancedb-native.manifest.json");
                File.WriteAllText(mutatedManifest, mutated.ToJsonString());
                Assert.Throws<LanceDbConfigurationException>(() => new LanceDbNativeApi(mutatedManifest));
                Assert.False(Directory.Exists(Path.Combine(folder, "vectors")));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [NativeIntegrationFact]
    [Trait("Category", "NativeIntegration")]
    public void HybridAlphaEndpointsAndEqualVectorScoresHaveStableOrdering()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!;
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires a manifest.");
        var root = RunRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var api = new LanceDbNativeApi(manifest);
            using var database = api.Open(root, "rag_chunks_v1", "ranking-owner");
            var codec = new LanceDbIpcCodec();
            api.EnsureReady(database, CancellationToken.None);
            var original = Document();
            var perpendicular = new float[384];
            perpendicular[1] = 1;
            var lexical = original with { Chunk = original.Chunk with { ChunkId = "lexical", Content = "zebracorn" }, Vector = perpendicular };
            var vectorZ = original with { Chunk = original.Chunk with { ChunkId = "vector-z", Content = "unrelated" } };
            var vectorA = vectorZ with { Chunk = vectorZ.Chunk with { ChunkId = "vector-a" } };
            api.Upsert(database, codec.EncodeDocuments([vectorZ, lexical, vectorA]), CancellationToken.None);
            var request = new LanceDbSearchWireRequest("zebracorn", null, null, codec.EncodeChunkIds([]), Vector(), 3, 0, 0);
            Assert.Equal("lexical", Assert.Single(codec.DecodeResults(api.Search(database, request, CancellationToken.None))).ChunkId);
            Assert.Empty(codec.DecodeResults(api.Search(database, request with { Query = "missingword" }, CancellationToken.None)));
            var vectorResults = codec.DecodeResults(api.Search(database, request with { Alpha = 1 }, CancellationToken.None));
            Assert.Equal(ExpectedVectorOrder, vectorResults.Select(result => result.ChunkId));
            Assert.Equal(vectorResults[0].Score, vectorResults[1].Score);
            api.Optimize(database, CancellationToken.None);
            Assert.Equal(vectorResults.Select(result => result.ChunkId), codec.DecodeResults(api.Search(database, request with { Alpha = 1 }, CancellationToken.None)).Select(result => result.ChunkId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeIntegrationFact]
    [Trait("Category", "NativeIntegration")]
    public void RestartReplaceSourceDeleteFiltersAndLexicalTailPreserveContract()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!;
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires a manifest.");
        var root = RunRoot();
        Directory.CreateDirectory(root);
        try
        {
            var codec = new LanceDbIpcCodec();
            using var api = new LanceDbNativeApi(manifest);
            var original = Document();
            var replacement = original with { Chunk = original.Chunk with { Content = "The zebracorn replacement persists locally." } };
            var other = original with { Chunk = original.Chunk with { ChunkId = "chunk-2", SourceId = "source-2", Language = "python", RelativePath = "other/test.py" } };
            using (var database = api.Open(root, "rag_chunks_v1", "contract-owner"))
            {
                api.EnsureReady(database, CancellationToken.None);
                api.Upsert(database, codec.EncodeDocuments([original, other]), CancellationToken.None);
                api.Optimize(database, CancellationToken.None);
                api.Upsert(database, codec.EncodeDocuments([replacement]), CancellationToken.None);
                api.Upsert(database, codec.EncodeDocuments([replacement]), CancellationToken.None);
                var request = new LanceDbSearchWireRequest("zebracorn", "csharp", "src/", codec.EncodeChunkIds(["source-1"]), new float[384], 5, 0, 1);
                Assert.Equal("chunk-1", Assert.Single(codec.DecodeResults(api.Search(database, request, CancellationToken.None))).ChunkId);
                Assert.Empty(codec.DecodeResults(api.Search(database, request with { Language = "python" }, CancellationToken.None)));
                Assert.Empty(codec.DecodeResults(api.Search(database, request with { PathPrefix = "other/" }, CancellationToken.None)));
                Assert.Empty(codec.DecodeResults(api.Search(database, request with { SourceIdsIpc = codec.EncodeChunkIds(["absent"]) }, CancellationToken.None)));
                api.Optimize(database, CancellationToken.None);
                Assert.Single(codec.DecodeResults(api.Search(database, request, CancellationToken.None)));
            }
            using (var database = api.Open(root, "rag_chunks_v1", "contract-owner"))
            {
                api.EnsureReady(database, CancellationToken.None);
                foreach (var mode in new uint[] { 0, 1, 2 })
                {
                    var request = new LanceDbSearchWireRequest("zebracorn", null, null, codec.EncodeChunkIds(["source-1"]), Vector(), 1, 0.65f, mode);
                    var result = Assert.Single(codec.DecodeResults(api.Search(database, request, CancellationToken.None)));
                    Assert.Equal("chunk-1", result.ChunkId);
                    Assert.Equal("source-1", result.SourceId);
                }
                api.DeleteSource(database, "source-1", CancellationToken.None);
                api.DeleteSource(database, "source-1", CancellationToken.None);
                var remaining = codec.DecodeResults(api.Search(database, new LanceDbSearchWireRequest("", null, null, codec.EncodeChunkIds([]), Vector(), 5, 1, 2), CancellationToken.None));
                Assert.Equal("chunk-2", Assert.Single(remaining).ChunkId);
                api.DeleteChunks(database, codec.EncodeChunkIds(["chunk-2", "chunk-2"]), CancellationToken.None);
                api.DeleteChunks(database, codec.EncodeChunkIds(["chunk-2"]), CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeIntegrationFact]
    [Trait("Category", "NativeIntegration")]
    public void InvalidPayloadCancellationAndApiDisposalDoNotReleaseLiveHandleCode()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!;
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires a manifest.");
        var root = RunRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var api = new LanceDbNativeApi(manifest);
            using var database = api.Open(root, "rag_chunks_v1", "fault-owner");
            api.EnsureReady(database, CancellationToken.None);
            var codec = new LanceDbIpcCodec();
            api.Upsert(database, codec.EncodeDocuments([Document()]), CancellationToken.None);
            Assert.Throws<LanceDbNativeException>(() => api.Upsert(database, [1, 2, 3], CancellationToken.None));
            Assert.Throws<LanceDbNativeException>(() => api.Upsert(database, new byte[16 * 1024 * 1024 + 1], CancellationToken.None));
            Assert.Throws<LanceDbNativeException>(() => api.ResetOwned(database, "wrong-owner", CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => api.DeleteSource(database, "source-1", cancelled.Token));
            var request = new LanceDbSearchWireRequest("embedded", null, null, codec.EncodeChunkIds([]), Vector(), 5, 0.65f, 0);
            Assert.Single(codec.DecodeResults(api.Search(database, request, CancellationToken.None)));
            Assert.Throws<LanceDbNativeException>(() => api.Search(database, request with { Alpha = float.NaN }, CancellationToken.None));
            Assert.Throws<LanceDbNativeException>(() => api.Search(database, request with { Mode = 3 }, CancellationToken.None));
            Assert.Throws<LanceDbNativeException>(() => api.Search(database, request with { Limit = 51 }, CancellationToken.None));
            Assert.Throws<LanceDbNativeException>(() => api.Search(database, request with { Vector = new float[383] }, CancellationToken.None));
            Assert.Throws<LanceDbNativeException>(() => api.Search(database, request with { SourceIdsIpc = [1, 2, 3] }, CancellationToken.None));
            Assert.Empty(codec.DecodeResults(api.Search(database, request with { Language = "csharp' OR true --" }, CancellationToken.None)));
            Assert.Empty(codec.DecodeResults(api.Search(database, request with { PathPrefix = "src/%' OR true --" }, CancellationToken.None)));
            Assert.Empty(codec.DecodeResults(api.Search(database, request with { SourceIdsIpc = codec.EncodeChunkIds(["source-1') OR true --"]) }, CancellationToken.None)));
            api.DeleteSource(database, "source-1' OR true --", CancellationToken.None);
            Assert.Single(codec.DecodeResults(api.Search(database, request, CancellationToken.None)));
            api.Dispose();
            Assert.Throws<ObjectDisposedException>(() => api.EnsureReady(database, CancellationToken.None));
            // The SafeHandle still calls the DLL's close export after API disposal.
            database.Dispose();
            api.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeIntegrationFact]
    [Trait("Category", "NativeIntegration")]
    public void ManagedAbiPerformsCrudSearchOptimizeOwnershipAndReset()
    {
        var manifest = Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST");
        Assert.False(string.IsNullOrWhiteSpace(manifest), "The native release lane requires LOCALRAG_LANCEDB_NATIVE_MANIFEST.");
        Assert.True(File.Exists(manifest), $"Native manifest was not found: {manifest}");
        var root = RunRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var api = new LanceDbNativeApi(manifest);
            using var database = api.Open(root, "rag_chunks_v1", "managed-integration-owner");
            var codec = new LanceDbIpcCodec();
            api.EnsureReady(database, CancellationToken.None);
            api.Upsert(database, codec.EncodeDocuments([Document()]), CancellationToken.None);
            api.Optimize(database, CancellationToken.None);

            var resultBytes = api.Search(
                database,
                new LanceDbSearchWireRequest(
                    "embedded database",
                    "csharp",
                    "src/",
                    codec.EncodeChunkIds(["source-1"]),
                    Vector(),
                    5,
                    0.65f,
                    0),
                CancellationToken.None);
            var result = Assert.Single(codec.DecodeResults(resultBytes));
            Assert.Equal("chunk-1", result.ChunkId);
            Assert.True(api.VerifyOwnership(database, "managed-integration-owner", CancellationToken.None));
            Assert.False(api.VerifyOwnership(database, "different-owner", CancellationToken.None));

            api.DeleteChunks(database, codec.EncodeChunkIds(["chunk-1"]), CancellationToken.None);
            Assert.Empty(codec.DecodeResults(api.Search(
                database,
                new LanceDbSearchWireRequest(
                    "embedded database",
                    null,
                    null,
                    codec.EncodeChunkIds([]),
                    Vector(),
                    5,
                    1f,
                    1),
                CancellationToken.None)));
            api.ResetOwned(database, "managed-integration-owner", CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static VectorDocument Document() =>
        new(
            new ChunkRecord(
                "chunk-1",
                "source-1",
                "file-1",
                "src/example.cs",
                "csharp",
                "Run",
                1,
                4,
                0,
                "The embedded database can search locally.",
                "hash-1",
                8,
                "embedding-v1",
                new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero),
                "symbol",
                "Example.Run",
                "symbol:Example.Run",
                "csharp",
                "1",
                "profile-1"),
            Vector());

    private static float[] Vector()
    {
        var vector = new float[LanceDbIpcCodec.VectorDimensions];
        vector[0] = 1f;
        return vector;
    }

    private static string RunRoot() => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALRAG_NATIVE_RUN_DIRECTORY") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "runs", "native-tests"),
        Guid.NewGuid().ToString("N"));
}

/// <summary>Managed-only runs report an explicit skip; release runs fail closed.</summary>
public sealed class NativeIntegrationFactAttribute : FactAttribute
{
    public NativeIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST"))
            && !string.Equals(Environment.GetEnvironmentVariable("LOCALRAG_NATIVE_RELEASE"), "1", StringComparison.Ordinal))
        {
            Skip = "Native package unavailable. Set LOCALRAG_LANCEDB_NATIVE_MANIFEST; LOCALRAG_NATIVE_RELEASE=1 makes it mandatory.";
        }
    }
}
