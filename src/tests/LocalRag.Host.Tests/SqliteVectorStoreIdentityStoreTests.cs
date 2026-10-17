using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class SqliteVectorStoreIdentityStoreTests
{
    [Fact]
    public async Task EmptyStateAdoptsConfiguredIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var configured = fixture.CreateIdentity(VectorStoreProvider.LanceDb);

        var result = await fixture.Identities.EnforceAsync(configured, CancellationToken.None);

        Assert.Equal(VectorStoreIdentityEnforcementResult.Adopted, result);
        Assert.Equal(configured, await fixture.Identities.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LegacyIndexedStateAdoptsWeaviateIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddChunkAsync();
        var configured = fixture.CreateIdentity(VectorStoreProvider.Weaviate);

        var result = await fixture.Identities.EnforceAsync(configured, CancellationToken.None);

        Assert.Equal(VectorStoreIdentityEnforcementResult.Adopted, result);
        Assert.Equal(configured, await fixture.Identities.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LegacyIndexedStateRefusesLanceDbIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddChunkAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Identities.EnforceAsync(fixture.CreateIdentity(VectorStoreProvider.LanceDb), CancellationToken.None));

        Assert.Contains("legacy state", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await fixture.Identities.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MismatchWithIndexedStateRefusesAndPreservesPriorIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddChunkAsync();
        var original = fixture.CreateIdentity(VectorStoreProvider.Weaviate);
        await fixture.Identities.EnforceAsync(original, CancellationToken.None);
        var changed = fixture.CreateIdentity(VectorStoreProvider.Weaviate, collection: "RagChunk_v2");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Identities.EnforceAsync(changed, CancellationToken.None));

        Assert.Equal(original, await fixture.Identities.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MismatchWithEmptyStateReplacesIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.CreateIdentity(VectorStoreProvider.Weaviate);
        await fixture.Identities.EnforceAsync(original, CancellationToken.None);
        var replacement = fixture.CreateIdentity(VectorStoreProvider.LanceDb);

        var result = await fixture.Identities.EnforceAsync(replacement, CancellationToken.None);

        Assert.Equal(VectorStoreIdentityEnforcementResult.ReplacedEmptyState, result);
        Assert.Equal(replacement, await fixture.Identities.GetAsync(CancellationToken.None));
    }

    [Fact]
    public void FingerprintIsStableForEquivalentConfiguredIdentity()
    {
        var first = SqliteVectorStoreIdentityStore.CreateConfiguredIdentity(new LocalRagOptions
        {
            VectorStore = new VectorStoreOptions
            {
                Provider = VectorStoreProvider.Weaviate,
                Weaviate = new WeaviateOptions { Endpoint = "HTTP://127.0.0.1:8080/", Collection = " RagChunk_v1 " }
            }
        });
        var second = SqliteVectorStoreIdentityStore.CreateConfiguredIdentity(new LocalRagOptions
        {
            VectorStore = new VectorStoreOptions
            {
                Provider = VectorStoreProvider.Weaviate,
                Weaviate = new WeaviateOptions { Endpoint = "http://127.0.0.1:8080", Collection = "RagChunk_v1" }
            }
        });

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first.Fingerprint);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, SqliteDatabase database, SqliteSourceRegistry sources, SqliteIndexStateStore indexState, SqliteVectorStoreIdentityStore identities)
        {
            Root = root;
            Database = database;
            Sources = sources;
            IndexState = indexState;
            Identities = identities;
        }

        public string Root { get; }
        public SqliteDatabase Database { get; }
        public SqliteSourceRegistry Sources { get; }
        public SqliteIndexStateStore IndexState { get; }
        public SqliteVectorStoreIdentityStore Identities { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"local-rag-vector-identity-{Guid.NewGuid():N}");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(root, "source"));
            var options = Options.Create(new LocalRagOptions { DataDirectory = dataDirectory });
            var database = new SqliteDatabase(options);
            var sources = new SqliteSourceRegistry(database, options);
            var indexState = new SqliteIndexStateStore(database, options);
            var identities = new SqliteVectorStoreIdentityStore(database);
            await sources.InitializeAsync(CancellationToken.None);
            await indexState.InitializeAsync(CancellationToken.None);
            await identities.InitializeAsync(CancellationToken.None);
            return new(root, database, sources, indexState, identities);
        }

        public VectorStoreIdentity CreateIdentity(VectorStoreProvider provider, string? collection = null) =>
            SqliteVectorStoreIdentityStore.CreateConfiguredIdentity(new LocalRagOptions
            {
                DataDirectory = Path.Combine(Root, "data"),
                VectorStore = new VectorStoreOptions
                {
                    Provider = provider,
                    Weaviate = new WeaviateOptions { Endpoint = "http://127.0.0.1:8080", Collection = collection ?? "RagChunk_v1" },
                    LanceDb = new LanceDbOptions { Path = Path.Combine(Root, "vectors"), Table = "rag_chunks_v1" }
                }
            });

        public async Task AddChunkAsync()
        {
            var source = await Sources.RegisterAsync(Path.Combine(Root, "source"), null, CancellationToken.None);
            var file = new IndexedFile("file-1", source.SourceId, "sample.cs", "file-hash", 1, DateTimeOffset.UtcNow);
            var chunk = new ChunkRecord(
                "chunk-1", source.SourceId, file.FileId, file.RelativePath, "csharp", null, 1, 1, 0,
                "class Sample { }", "chunk-hash", 4, "bge-small-en-v1.5-384", DateTimeOffset.UtcNow,
                StructuralLocator: "lines:1-1");
            await IndexState.SaveFileAndChunksAsync(file, [chunk], CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
