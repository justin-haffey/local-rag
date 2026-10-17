using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class LanceDbVectorStoreTests
{
    [Fact]
    public async Task DisposalDrainsNativeWorkBeforeReleasingExclusiveWriterLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "localrag-lancedb-shutdown-" + Guid.NewGuid().ToString("N"));
        var vectors = Path.Combine(root, "vectors");
        var options = new LocalRagOptions { DataDirectory = Path.Combine(root, "data") };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new RecordingNativeApi { OnUpsert = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } };
        try
        {
            using var ownership = new InstallationOwnershipStore(Options.Create(options));
            using var store = new LanceDbVectorStore(
                new LanceDbOptions { Path = vectors, Table = "rag_chunks_v1", BatchSize = 100 },
                ownership, new LanceDbIpcCodec(), () => native);
            await store.EnsureReadyAsync(CancellationToken.None);
            var write = store.UpsertAsync(Documents(1), CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            var disposing = Task.Run(store.Dispose);
            try
            {
                Assert.Throws<IOException>(() => new FileStream(Path.Combine(vectors, ".localrag-exclusive.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                Assert.False(native.Disposed);
            }
            finally
            {
                release.Set();
            }
            await write;
            await disposing.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(native.Disposed);
            using var acquired = new FileStream(Path.Combine(vectors, ".localrag-exclusive.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            release.Set();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpsertRunsBoundedMaintenanceAtDocumentThreshold()
    {
        var root = Path.Combine(Path.GetTempPath(), "localrag-lancedb-store-" + Guid.NewGuid().ToString("N"));
        var vectors = Path.Combine(root, "vectors");
        var options = new LocalRagOptions { DataDirectory = Path.Combine(root, "data") };
        var native = new RecordingNativeApi();
        try
        {
            using var ownership = new InstallationOwnershipStore(Options.Create(options));
            using var store = new LanceDbVectorStore(
                new LanceDbOptions { Path = vectors, Table = "rag_chunks_v1", BatchSize = 100 },
                ownership,
                new LanceDbIpcCodec(),
                () => native);

            await store.UpsertAsync(Documents(999), CancellationToken.None);
            Assert.Equal(0, native.OptimizeCalls);

            await store.UpsertAsync(Documents(1, 999), CancellationToken.None);

            Assert.Equal(1, native.OptimizeCalls);
            Assert.Equal(11, native.UpsertCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static VectorDocument[] Documents(int count, int offset = 0) =>
        Enumerable.Range(offset, count).Select(index =>
        {
            var id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new VectorDocument(
                new ChunkRecord(
                    "chunk-" + id, "source-1", "file-" + id, $"src/{id}.cs", "csharp",
                    "Run", 1, 1, index, "void Run() { }", "hash-" + id, 4, "embedding-v1",
                    new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero),
                    "symbol", "Example.Run", "symbol:Example.Run", "csharp", "1", "profile-1"),
                Vector());
        }).ToArray();

    private static float[] Vector()
    {
        var vector = new float[LanceDbIpcCodec.VectorDimensions];
        vector[0] = 1f;
        return vector;
    }

    private sealed class RecordingNativeApi : ILanceDbNativeApi
    {
        public int UpsertCalls { get; private set; }
        public int OptimizeCalls { get; private set; }
        public Action? OnUpsert { get; init; }
        public bool Disposed { get; private set; }

        public LanceDbSafeHandle Open(string path, string table, string ownershipId) =>
            new(new IntPtr(1), _ => true);
        public void EnsureReady(LanceDbSafeHandle database, CancellationToken cancellationToken) { }
        public void Upsert(LanceDbSafeHandle database, byte[] ipc, CancellationToken cancellationToken)
        {
            OnUpsert?.Invoke();
            UpsertCalls++;
        }
        public void DeleteChunks(LanceDbSafeHandle database, byte[] ipc, CancellationToken cancellationToken) { }
        public void DeleteSource(LanceDbSafeHandle database, string sourceId, CancellationToken cancellationToken) { }
        public byte[] Search(LanceDbSafeHandle database, LanceDbSearchWireRequest request, CancellationToken cancellationToken) => [];
        public bool VerifyOwnership(LanceDbSafeHandle database, string ownershipId, CancellationToken cancellationToken) => true;
        public void ResetOwned(LanceDbSafeHandle database, string ownershipId, CancellationToken cancellationToken) { }
        public void Optimize(LanceDbSafeHandle database, CancellationToken cancellationToken) => OptimizeCalls++;
        public void Dispose() => Disposed = true;
    }
}
