using System.Text.Json;
using LocalRag.Api;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class CatalogProjectConfigurationTests
{
    [Fact]
    public async Task PreMetadataSchemaMigratesAdditivelyWithoutChangingLegacyRow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"localrag-old-schema-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new LocalRagOptions { DataDirectory = root });
            var database = new SqliteDatabase(options);
            await using (var connection = await database.OpenAsync(CancellationToken.None))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE Sources(SourceId TEXT PRIMARY KEY, CanonicalRootPath TEXT NOT NULL UNIQUE,
                      DisplayName TEXT NOT NULL, Status TEXT NOT NULL, CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL,
                      LastScanUtc TEXT NULL, LastSuccessfulIndexUtc TEXT NULL, EmbeddingProfileId TEXT NOT NULL, LastError TEXT NULL);
                    INSERT INTO Sources VALUES ('historical-id','C:\\legacy','Legacy label','Ready',
                      '2026-01-01T00:00:00+00:00','2026-01-01T00:00:00+00:00',NULL,NULL,'retained-profile',NULL);
                    CREATE TABLE ExistingChunks(ChunkId TEXT PRIMARY KEY, SourceId TEXT, Content TEXT);
                    INSERT INTO ExistingChunks VALUES ('retained-chunk','historical-id','retained content');
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var registry = new SqliteSourceRegistry(database, options);
            await registry.InitializeAsync(CancellationToken.None);
            var row = await registry.GetAsync("historical-id", CancellationToken.None);
            Assert.NotNull(row);
            Assert.Equal("Legacy label", row!.DisplayName);
            Assert.Equal("retained-profile", row.EmbeddingProfileId);
            Assert.Equal("unclassified", row.Scope);
            Assert.Equal("", row.Description);
            Assert.Null(row.OwningProjectRoot);
            Assert.Equal(0, row.MetadataRevision);
            await using var verify = await database.OpenAsync(CancellationToken.None);
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT Content FROM ExistingChunks WHERE ChunkId='retained-chunk';";
            Assert.Equal("retained content", await query.ExecuteScalarAsync());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PublishedExamplesShareManagedAndEditorValidationOutcomes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "docs", "project-configuration", "examples"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root!.FullName, "docs", "project-configuration", "examples"), "*.json"))
        {
            if (Path.GetFileName(file).StartsWith("valid", StringComparison.Ordinal)) ProjectConfigurationService.Parse(File.ReadAllText(file));
            else Assert.Throws<ArgumentException>(() => ProjectConfigurationService.Parse(File.ReadAllText(file)));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"solution\":null}")]
    public void InvalidShapesAreRepairableArgumentErrors(string content) =>
        Assert.Throws<ArgumentException>(() => ProjectConfigurationService.Parse(content));

    [Fact]
    public void ContractRejectsUnknownVersionSecretsDuplicateAndBlankReferences()
    {
        var good = Configuration("5b91d028-3978-4f40-8b78-b15dbecfb41b", "legacy-opaque");
        var text = JsonSerializer.Serialize(good, ProjectConfigurationService.Json);
        Assert.Equal("legacy-opaque", ProjectConfigurationService.Parse(text).KnowledgeBases[0].SourceId);
        Assert.Throws<ArgumentException>(() => ProjectConfigurationService.Parse(text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => ProjectConfigurationService.Parse(text[..^1] + ",\"token\":\"secret\"}"));
        Assert.Throws<ArgumentException>(() => ProjectConfigurationService.Parse(text.Replace("\"sourceId\": \"legacy-opaque\"", "\"sourceId\": \" \"", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => ProjectConfigurationService.Parse(JsonSerializer.Serialize(good with { KnowledgeBases = [new("same"), new("same")] }, ProjectConfigurationService.Json)));
    }

    [Fact]
    public async Task LegacyMigrationAndMetadataEditsRetainIdentityAndEmbeddingProfileAndRejectStaleRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = await fixture.Registry.RegisterAsync(fixture.Source("legacy"), "Legacy", CancellationToken.None);
        await fixture.Registry.InitializeAsync(CancellationToken.None);
        var migrated = (await fixture.Registry.GetAsync(legacy.SourceId, CancellationToken.None))!;
        Assert.Equal("", migrated.Description);
        Assert.Equal("unclassified", migrated.Scope);
        Assert.Equal(legacy.EmbeddingProfileId, migrated.EmbeddingProfileId);
        var updated = await fixture.Catalog.UpdateAsync(legacy.SourceId, new(" Renamed ", "<script>plain text</script>", "local", fixture.Root, 0), CancellationToken.None);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal(legacy.SourceId, updated.SourceId);
        Assert.Equal(legacy.CanonicalRootPath, updated.CanonicalRootPath);
        Assert.Equal(legacy.EmbeddingProfileId, updated.EmbeddingProfileId);
        Assert.Equal(1, updated.MetadataRevision);
        Assert.Equal(0, fixture.Coordinator.Queued);
        await Assert.ThrowsAsync<CatalogConflictException>(() => fixture.Catalog.UpdateAsync(legacy.SourceId, new("Lost update", "", "global", null, 0), CancellationToken.None));
        Assert.Equal("Renamed", (await fixture.Registry.GetAsync(legacy.SourceId, CancellationToken.None))!.DisplayName);
        var movedProject = fixture.Source("moved-project");
        var moved = await fixture.Catalog.UpdateAsync(legacy.SourceId, new("Renamed", "Description", "local", movedProject, 1), CancellationToken.None);
        Assert.Equal(movedProject, moved.OwningProjectRoot);
        Assert.Equal(legacy.SourceId, moved.SourceId);
        Assert.Equal(legacy.EmbeddingProfileId, moved.EmbeddingProfileId);
        Assert.Equal(0, fixture.Coordinator.Queued);
    }

    [Fact]
    public async Task RegistrationIsIdempotentAndRootOverlapRemainsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Source("one");
        var request = new CatalogRegistration(root, "One", "", "global", null);
        var created = await fixture.Catalog.RegisterAsync(request, CancellationToken.None);
        var repeated = await fixture.Catalog.RegisterAsync(request with { Name = "Ignored duplicate" }, CancellationToken.None);
        Assert.False(created.AlreadyRegistered);
        Assert.True(repeated.AlreadyRegistered);
        Assert.Equal(created.Source.SourceId, repeated.Source.SourceId);
        Assert.Equal(1, fixture.Coordinator.Queued);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.RegisterAsync(request with { RootPath = Path.Combine(root, "nested") }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Catalog.RegisterAsync(request with { Scope = "unclassified" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Catalog.RegisterAsync(request with { RootPath = "relative" }, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => SourceCatalogService.Validate(new("name", "", "local", null, 0)));
    }

    [Fact]
    public async Task ProjectSelectionsAreExactAndCrossProjectLocalReuseIsExplicit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var localA = await fixture.Register("a", "local");
        var localB = await fixture.Register("b", "local");
        var global = await fixture.Register("reference", "global");
        var id = await fixture.Projects.InstanceIdAsync(CancellationToken.None);
        Assert.Equal(id, await fixture.Projects.InstanceIdAsync(CancellationToken.None));
        var first = Configuration(id, localA.SourceId, global.SourceId) with { Solution = new("Project A", "A and shared reference") };
        var second = Configuration(id, localB.SourceId, global.SourceId) with { Solution = new("Project B", "B and shared reference") };
        Assert.NotEqual(localA.OwningProjectRoot, localB.OwningProjectRoot);
        await fixture.Projects.SaveAsync(localA.OwningProjectRoot!, JsonSerializer.Serialize(first, ProjectConfigurationService.Json), null, CancellationToken.None);
        await fixture.Projects.SaveAsync(localB.OwningProjectRoot!, JsonSerializer.Serialize(second, ProjectConfigurationService.Json), null, CancellationToken.None);
        await fixture.Projects.SearchAsync(first, new("query", [localB.SourceId]), CancellationToken.None);
        Assert.Equal(new[] { localA.SourceId, global.SourceId }, fixture.Search.Last!.SourceIds);
        await fixture.Projects.SearchAsync(second, new("query"), CancellationToken.None);
        Assert.Equal(new[] { localB.SourceId, global.SourceId }, fixture.Search.Last!.SourceIds);
        await fixture.Projects.SearchAsync(Configuration(id, localA.SourceId), new("query"), CancellationToken.None);
        Assert.Equal(new[] { localA.SourceId }, fixture.Search.Last!.SourceIds);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Projects.SearchAsync(Configuration(id), new("query"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Projects.SearchAsync(Configuration(Guid.NewGuid().ToString(), localA.SourceId), new("query"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Projects.SearchAsync(Configuration(id, "stale-id"), new("query"), CancellationToken.None));
        await fixture.Registry.SetStatusAsync(localA.SourceId, SourceStatus.Paused, null, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Projects.SearchAsync(first, new("query"), CancellationToken.None));
        Assert.Contains(localA.SourceId, error.Message, StringComparison.Ordinal);
        Assert.Equal(3, fixture.Search.Calls);
    }

    [Fact]
    public async Task FileSaveRoundTripsPreservesBytesAndDetectsConcurrentEditsAndInvalidExistingFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.Register("source", "global");
        var config = Configuration(await fixture.Projects.InstanceIdAsync(CancellationToken.None), source.SourceId);
        var content = JsonSerializer.Serialize(config, ProjectConfigurationService.Json) + "\n";
        Assert.Null((await fixture.Projects.ReadAsync(fixture.Root, CancellationToken.None)).Content);
        var saved = await fixture.Projects.SaveAsync(fixture.Root, content, null, CancellationToken.None);
        Assert.Equal(content, saved.Content);
        await Assert.ThrowsAsync<CatalogConflictException>(() => fixture.Projects.SaveAsync(fixture.Root, content, null, CancellationToken.None));
        File.AppendAllText(Path.Combine(fixture.Root, ".localrag.json"), " ");
        await Assert.ThrowsAsync<CatalogConflictException>(() => fixture.Projects.SaveAsync(fixture.Root, content, saved.Revision, CancellationToken.None));
        File.WriteAllText(Path.Combine(fixture.Root, ".localrag.json"), "{broken");
        var broken = await fixture.Projects.ReadAsync(fixture.Root, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Projects.SaveAsync(fixture.Root, content, broken.Revision, CancellationToken.None));
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(fixture.Root, ".localrag.json")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task McpAppendedParametersPreserveSearchContract()
    {
        var search = new RecordingSearch();
        var mcp = new RagMcpTools(search, null!, null!);
        await mcp.RagSearch("query", ["one"], 7, SearchMode.Lexical, .25, "csharp", "src/", CancellationToken.None);
        var rest = new SearchApiRequest("query", ["one"], 7, .25, SearchMode.Lexical, "csharp", "src/");
        Assert.Equal(rest.Query, search.Last!.Query);
        Assert.Equal(rest.SourceIds, search.Last.SourceIds);
        Assert.Equal(rest.Limit, search.Last.Limit);
        Assert.Equal(rest.Alpha, search.Last.Alpha);
        Assert.Equal(rest.Mode, search.Last.Mode);
        Assert.Equal(rest.Language, search.Last.Language);
        Assert.Equal(rest.PathPrefix, search.Last.PathPrefix);
        await mcp.RagSearch("legacy", ["one"], 12);
        Assert.Equal(SearchMode.Hybrid, search.Last.Mode);
    }

    private static ProjectConfiguration Configuration(string instanceId, params string[] ids) => new(1, new(" Project ", "Description"), instanceId, ids.Select(id => new KnowledgeBaseReference(id)).ToArray());

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"localrag-p2-{Guid.NewGuid():N}");
        public SqliteSourceRegistry Registry { get; }
        public RecordingCoordinator Coordinator { get; } = new();
        public RecordingSearch Search { get; } = new();
        public SourceCatalogService Catalog { get; }
        public ProjectConfigurationService Projects { get; }
        private Fixture()
        {
            Directory.CreateDirectory(Root);
            var options = Options.Create(new LocalRagOptions { DataDirectory = Path.Combine(Root, "data") });
            var database = new SqliteDatabase(options);
            Registry = new(database, options);
            Catalog = new(Registry, Coordinator);
            Projects = new(database, Registry, Search);
        }
        public static async Task<Fixture> CreateAsync() { var f = new Fixture(); await f.Registry.InitializeAsync(CancellationToken.None); return f; }
        public string Source(string name) { var root = Path.Combine(Root, name); Directory.CreateDirectory(root); return root; }
        public async Task<SourceRecord> Register(string name, string scope) => (await Catalog.RegisterAsync(new(Source(name), name, "", scope, scope == "local" ? Source("project-" + name) : null), CancellationToken.None)).Source;
        public ValueTask DisposeAsync() { Catalog.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
    private sealed class RecordingCoordinator : IIndexCoordinator
    {
        public int Queued { get; private set; }
        public Task QueueInitialIndexAsync(string sourceId, CancellationToken cancellationToken) { Queued++; return Task.CompletedTask; }
        public Task ReindexAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class RecordingSearch : IRagSearchService
    {
        public SearchRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) { Last = request; Calls++; return Task.FromResult(new SearchResponse(request.Query, [], 0, 0, false)); }
        public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) => Task.FromResult<ChunkRecord?>(null);
    }
}
