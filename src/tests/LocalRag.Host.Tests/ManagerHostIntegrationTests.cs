using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.LanceDb;
using LocalRag.Infrastructure.Management;
using LocalRag.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ManagerHostIntegrationTests
{
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };
    private static readonly string[] SentinelDataType = ["text"];

    [EnvironmentFact("LOCALRAG_P3_BROWSER_HARNESS", "1")]
    public async Task ProductionHostBrowserHarness()
    {
        var external = Environment.GetEnvironmentVariable("LOCALRAG_P3_BROWSER_EXTERNAL_FIXTURE") == "1";
        var embedded = Environment.GetEnvironmentVariable("LOCALRAG_P3_BROWSER_EMBEDDED") == "1";
        if (embedded) AssertNativePackageStaged();
        var runId = Guid.NewGuid().ToString("N");
        var root = external ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ContentRoot()))!, "artifacts", "runs", "p3-browser-external-20261017", runId) : NewRoot();
        Directory.CreateDirectory(root);
        var collection = "P3Browser" + runId;
        var live = Environment.GetEnvironmentVariable("LOCALRAG_P3_WEAVIATE_TESTS") == "1";
        var sourceRoot = external ? Path.Combine(Path.GetTempPath(), "localrag-plan-20261017-" + runId, "external source 日本語") : Path.Combine(root, "external source 日本語");
        var outcome = "Interrupted";
        try
        {
            await using var app = Build(root, live || embedded, collection, embedded);
            await app.StartAsync();
            using var client = Client(app);
            Directory.CreateDirectory(sourceRoot);
            var projectRoot = Path.Combine(root, "chosen project"); Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "notes #1.md"), "# Coral reef\nA coral reef shelters a colorful parrotfish.");
            await RecordBrowserFixtureAsync();
            var ticket = app.Services.GetRequiredService<LocalRag.Authentication.BrowserLaunchTicketStore>().Issue(true);
            var connection = new { url = new Uri(client.BaseAddress!, "/manage#ticket=" + ticket).ToString(), root, sourceRoot, projectRoot, provider = embedded ? "LanceDb" : "Weaviate", externalFixture = external, installedProduct = false, startedUtc = DateTimeOffset.UtcNow, deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(20), stopFile = Path.Combine(root, "browser-complete.txt") };
            var evidenceRoot = external ? root : Path.GetDirectoryName(root)!;
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "browser-connection.json"), JsonSerializer.Serialize(connection, ManifestJson));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            while (!File.Exists(connection.stopFile)) await Task.Delay(250, timeout.Token);
            await app.StopAsync();
            outcome = "StoppedByBrowserOperator";
        }
        finally
        {
            if (live && !embedded)
            {
                using var http = new HttpClient();
                try { await http.DeleteAsync($"http://127.0.0.1:18080/v1/schema/{collection}"); } catch (HttpRequestException) { }
            }
            if (Directory.Exists(sourceRoot)) await RecordBrowserFixtureAsync();
            RecordManifest(root);
        }

        Task RecordBrowserFixtureAsync()
        {
            var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Select(path => new { path = Path.GetRelativePath(sourceRoot, path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
            return File.WriteAllTextAsync(Path.Combine(root, "external-fixture.json"), JsonSerializer.Serialize(new { sourceRoot, externalFixture = external, purpose = "Synthetic external-folder development-host browser verification; not installed-product acceptance", retention = "Retain source fixture, disposable host data and all evidence for review; no automatic deletion", outcome, files }, ManifestJson));
        }
    }
    [Fact]
    public async Task ProductionRazorSessionAndAssetsRemainAvailableWithMissingModelsAndProvider()
    {
        var root = NewRoot();
        try
        {
            await using var app = Build(root, false);
            await app.StartAsync();
            using var browser = Client(app);
            var page = await browser.GetStringAsync("/manage");
            Assert.Contains("Local RAG Manager", page, StringComparison.Ordinal);
            Assert.Contains("csrf-token", page, StringComparison.Ordinal);
            Assert.Contains("/manager.js", page, StringComparison.Ordinal);
            Assert.Contains("textContent", await browser.GetStringAsync("/manager.js"), StringComparison.Ordinal);
            Assert.Contains(":focus-visible", await browser.GetStringAsync("/manager.css"), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/catalog")).StatusCode);
            using var bearer = Client(app); bearer.DefaultRequestHeaders.Authorization = new("Bearer", "integration-normal-token");
            var ticket = await bearer.PostAsJsonAsync("/manage/tickets", new { managementToken = "integration-management-token" });
            var value = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString();
            browser.DefaultRequestHeaders.Add("Origin", browser.BaseAddress!.GetLeftPart(UriPartial.Authority));
            browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", Csrf(page));
            Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/manage/session-ticket", new { ticket = value })).StatusCode);
            page = await browser.GetStringAsync("/manage");
            browser.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", Csrf(page));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/catalog")).StatusCode);
            var unavailableRoot = Path.Combine(root, "paused-source"); Directory.CreateDirectory(unavailableRoot);
            var pausedSource = await app.Services.GetRequiredService<ISourceRegistry>().RegisterAsync(unavailableRoot, "Paused", CancellationToken.None);
            await app.Services.GetRequiredService<SourceLifecycleService>().SetPausedAsync(pausedSource.SourceId, true, CancellationToken.None);
            var pausedSearch = await browser.PostAsJsonAsync("/api/v1/search", new { query = "nectar", sourceIds = new[] { pausedSource.SourceId }, mode = "Lexical" });
            Assert.Equal(HttpStatusCode.Conflict, pausedSearch.StatusCode);
            Assert.Contains("paused", await pausedSearch.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var system = await browser.GetFromJsonAsync<JsonElement>("/api/v1/system");
            Assert.Equal("Degraded", system.GetProperty("checks").GetProperty("embedding-assets").GetProperty("status").GetString());
            Assert.Equal("Degraded", system.GetProperty("checks").GetProperty("vector-store").GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/manage/session-ticket", new { ticket = value })).StatusCode);
            await app.StopAsync();
        }
        finally { RecordManifest(root); }
    }

    [Fact]
    public async Task ActualStartupRetainsInterruptedPauseWithoutDeletingMissingRoot()
    {
        var root = NewRoot(); string sourceId;
        try
        {
            await using (var first = Build(root, false))
            {
                await first.StartAsync();
                var path = Path.Combine(root, "outside-workspace"); Directory.CreateDirectory(path);
                var sources = first.Services.GetRequiredService<ISourceRegistry>();
                var source = await sources.RegisterAsync(path, "Retained pause", CancellationToken.None); sourceId = source.SourceId;
                await first.Services.GetRequiredService<SourceIndexingStateStore>().SetAsync(sourceId, "Paused", "Pausing", CancellationToken.None);
                Directory.Delete(path);
                await first.StopAsync();
            }
            await using (var second = Build(root, false))
            {
                await second.StartAsync();
                var source = await second.Services.GetRequiredService<ISourceRegistry>().GetAsync(sourceId, CancellationToken.None);
                Assert.NotNull(source); Assert.Equal(SourceStatus.Paused, source.Status);
                var state = await second.Services.GetRequiredService<SourceIndexingStateStore>().GetAsync(sourceId, CancellationToken.None);
                Assert.Equal("Paused", state.State); Assert.False(state.QueryVisible);
                await second.StopAsync();
            }
        }
        finally { RecordManifest(root); }
    }

    [EnvironmentFact("LOCALRAG_P3_WEAVIATE_TESTS", "1")]
    public Task ConfirmedHttpRemoveAndResetPreserveSourceFilesAndUnrelatedState() => ResetCoreAsync(false);

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task ConfirmedEmbeddedHttpRemoveAndResetPreserveSourceFilesAndUnrelatedState() => ResetCoreAsync(true);

    private static async Task ResetCoreAsync(bool embedded)
    {
        if (embedded) AssertNativePackageStaged();
        var root = NewRoot(); var collection = "P3Reset" + Guid.NewGuid().ToString("N");
        var sentinelCollection = "P3Sentinel" + Guid.NewGuid().ToString("N");
        using var provider = new HttpClient { BaseAddress = new("http://127.0.0.1:18080") };
        LanceDbVectorStore? nativeSentinel = null;
        try
        {
            var sentinelId = Guid.NewGuid().ToString();
            if (!embedded)
            {
                var schema = await provider.PostAsJsonAsync("/v1/schema", new { @class = sentinelCollection, vectorizer = "none", properties = new[] { new { name = "sentinel", dataType = SentinelDataType } } });
                schema.EnsureSuccessStatusCode();
                (await provider.PostAsJsonAsync("/v1/objects", new { @class = sentinelCollection, id = sentinelId, properties = new { sentinel = "unrelated provider data" } })).EnsureSuccessStatusCode();
            }
            var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(sourceRoot);
            var sourceFile = Path.Combine(sourceRoot, "original.txt"); const string sourceText = "A lighthouse guides passing ships through the night.";
            await File.WriteAllTextAsync(sourceFile, sourceText);
            var siblingFile = Path.Combine(root, "unrelated.txt"); await File.WriteAllTextAsync(siblingFile, "unrelated sibling file");
            await using var app = Build(root, true, collection, embedded);
            await app.StartAsync();
            if (embedded)
            {
                nativeSentinel = new LanceDbVectorStore(new LanceDbOptions { Path = Path.Combine(root, "unrelated-vectors"), Table = "sentinel_v1" }, app.Services.GetRequiredService<InstallationOwnershipStore>(), new LanceDbIpcCodec(), () => new LanceDbNativeApi(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!));
                var sentinelVector = new float[384]; sentinelVector[0] = 1;
                var sentinelChunk = new ChunkRecord("unrelated-chunk", "unrelated-source", "unrelated-file", "sentinel.txt", "text", null, 1, 1, 0, "unrelated provider sentinel", "sentinel-content-hash", 5, app.Services.GetRequiredService<IOptions<LocalRagOptions>>().Value.Embedding.ProfileId, DateTimeOffset.UtcNow);
                await nativeSentinel.UpsertAsync([new(sentinelChunk, sentinelVector)], CancellationToken.None);
            }
            var dataSentinel = Path.Combine(root, "data", "unrelated-data.keep"); await File.WriteAllTextAsync(dataSentinel, "unrelated data-directory file");
            using var client = Client(app); client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-normal-token");
            using var management = Client(app); management.DefaultRequestHeaders.Authorization = new("Bearer", "integration-management-token");
            async Task<string> RegisterAsync()
            {
                var response = await client.PostAsJsonAsync("/api/v1/catalog/sources", new { rootPath = sourceRoot, name = "Disposable confirmation test", description = "", scope = "global" }); response.EnsureSuccessStatusCode();
                var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source").GetProperty("sourceId").GetString()!;
                await UntilAsync(async () => (await app.Services.GetRequiredService<ISourceRegistry>().GetAsync(id, CancellationToken.None))?.Status == SourceStatus.Ready);
                return id;
            }
            var sourceId = await RegisterAsync();
            var removePath = $"/api/v1/management/sources/{sourceId}/remove";
            var prepareRemove = await management.PostAsJsonAsync(removePath, new { }); prepareRemove.EnsureSuccessStatusCode();
            var removeChallenge = (await prepareRemove.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmationToken").GetString();
            Assert.NotNull(await app.Services.GetRequiredService<ISourceRegistry>().GetAsync(sourceId, CancellationToken.None));
            var invalid = await management.PostAsJsonAsync(removePath, new { confirmationToken = "invalid" });
            Assert.Equal("InvalidConfirmation", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            var removed = await management.PostAsJsonAsync(removePath, new { confirmationToken = removeChallenge }); removed.EnsureSuccessStatusCode();
            Assert.Equal((int)ManagementOperationState.Completed, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetInt32());
            Assert.Null(await app.Services.GetRequiredService<ISourceRegistry>().GetAsync(sourceId, CancellationToken.None));
            Assert.Equal(sourceText, await File.ReadAllTextAsync(sourceFile));
            var replacementId = await RegisterAsync(); Assert.NotEqual(sourceId, replacementId);
            await app.Services.GetRequiredService<SourceLifecycleService>().SetPausedAsync(replacementId, true, CancellationToken.None);
            var prepared = await management.PostAsJsonAsync("/api/v1/management/reset", new { }); prepared.EnsureSuccessStatusCode();
            var challenge = (await prepared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmationToken").GetString();
            Assert.NotNull(await app.Services.GetRequiredService<ISourceRegistry>().GetAsync(replacementId, CancellationToken.None));
            var reset = await management.PostAsJsonAsync("/api/v1/management/reset", new { confirmationToken = challenge }); reset.EnsureSuccessStatusCode();
            var resetResult = await reset.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal((int)ManagementOperationState.Completed, resetResult.GetProperty("state").GetInt32());
            Assert.Empty(await app.Services.GetRequiredService<ISourceRegistry>().ListAsync(CancellationToken.None));
            Assert.Equal(sourceText, await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("unrelated sibling file", await File.ReadAllTextAsync(siblingFile));
            Assert.Equal("unrelated data-directory file", await File.ReadAllTextAsync(dataSentinel));
            if (nativeSentinel is not null)
                Assert.Single(await nativeSentinel.SearchAsync(new("sentinel", ["unrelated-source"], Mode: SearchMode.Lexical), new float[384], CancellationToken.None));
            else Assert.Equal(HttpStatusCode.OK, (await provider.GetAsync($"/v1/objects/{sentinelCollection}/{sentinelId}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/catalog")).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/v1/lifecycle")).EnumerateArray());
            await app.StopAsync();
        }
        finally
        {
            nativeSentinel?.Dispose();
            if (!embedded) foreach (var owned in new[] { collection, sentinelCollection })
                try { await provider.DeleteAsync($"/v1/schema/{owned}"); } catch (HttpRequestException) { }
            RecordManifest(root);
        }
    }

    [EnvironmentFact("LOCALRAG_P3_WEAVIATE_TESTS", "1")]
    public Task RealProviderPauseRestartResumeReconcilesEditsAndDeletesBeforeSearchVisibility() => LifecycleCoreAsync(false);

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task EmbeddedProviderPauseRestartResumeReconcilesEditsAndDeletesBeforeSearchVisibility() => LifecycleCoreAsync(true);

    [EnvironmentFact("LOCALRAG_P3_WEAVIATE_TESTS", "1")]
    public Task NestedWindowsPathsRemainSearchableAndReconcileEditsAndDeletes() => NestedPathsCoreAsync(false);

    [AllEnvironmentFact("LOCALRAG_LANCEDB_NATIVE_MANIFEST", "LOCALRAG_ONNX_TESTS=1")]
    public Task EmbeddedNestedWindowsPathsRemainSearchableAndReconcileEditsAndDeletes() => NestedPathsCoreAsync(true);

    private static async Task NestedPathsCoreAsync(bool embedded)
    {
        if (embedded) AssertNativePackageStaged();
        var root = NewRoot(); var collection = "P3Nested" + Guid.NewGuid().ToString("N");
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var nested = Path.Combine(sourceRoot, "Nested 日本語", "Deep #"); Directory.CreateDirectory(nested);
            var keptPath = Path.Combine(nested, "Keep.txt"); var removedPath = Path.Combine(nested, "Delete.txt");
            await File.WriteAllTextAsync(keptPath, "A hummingbird drinks sweet nectar.");
            await File.WriteAllTextAsync(removedPath, "An obsolete fossilmarker belongs to this source.");
            await using var app = Build(root, true, collection, embedded); await app.StartAsync();
            using var client = Client(app); client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-normal-token");
            var response = await client.PostAsJsonAsync("/api/v1/catalog/sources", new { rootPath = sourceRoot, name = "Nested regression", description = "Nested path correctness regression", scope = "global" }); response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source").GetProperty("sourceId").GetString()!;
            var reconciliations = app.Services.GetRequiredService<IReconciliationStore>();
            await UntilAsync(async () => (await reconciliations.GetAsync(id, CancellationToken.None)) is { State: ReconciliationState.Clean, CompletedGeneration: >= 1 });
            var initial = (await reconciliations.GetAsync(id, CancellationToken.None))!;
            Assert.Equal(0, initial.LastDeletedFiles);
            await AssertSearchAsync("hummingbird", "Nested 日本語/Deep #/Keep.txt", true);
            await AssertSearchAsync("fossilmarker", "Nested 日本語/Deep #/Delete.txt", true);
            var before = await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(id, CancellationToken.None);
            (await client.PostAsJsonAsync($"/api/v1/sources/{id}/reindex", new { })).EnsureSuccessStatusCode();
            await UntilAsync(async () => (await reconciliations.GetAsync(id, CancellationToken.None)) is { State: ReconciliationState.Clean } state && state.CompletedGeneration > initial.CompletedGeneration);
            var unchanged = (await reconciliations.GetAsync(id, CancellationToken.None))!;
            Assert.Equal(0, unchanged.LastChangedFiles); Assert.Equal(0, unchanged.LastDeletedFiles); Assert.Equal(2, unchanged.LastUnchangedFiles);
            var after = await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(id, CancellationToken.None);
            Assert.Equal(before.OrderBy(chunk => chunk.ChunkId).Select(chunk => (chunk.ChunkId, chunk.LastIndexedUtc)), after.OrderBy(chunk => chunk.ChunkId).Select(chunk => (chunk.ChunkId, chunk.LastIndexedUtc)));
            await AssertSearchAsync("hummingbird", "Nested 日本語/Deep #/Keep.txt", true);
            (await client.PostAsJsonAsync($"/api/v1/sources/{id}/pause", new { })).EnsureSuccessStatusCode();
            await File.WriteAllTextAsync(keptPath, "Jellyfish drift through a quiet ocean current."); File.Delete(removedPath);
            (await client.PostAsJsonAsync($"/api/v1/sources/{id}/resume", new { })).EnsureSuccessStatusCode();
            await UntilAsync(async () => (await app.Services.GetRequiredService<SourceIndexingStateStore>().GetAsync(id, CancellationToken.None)).QueryVisible);
            await AssertSearchAsync("Jellyfish", "Nested 日本語/Deep #/Keep.txt", true);
            await AssertSearchAsync("hummingbird", "Nested 日本語/Deep #/Keep.txt", false);
            await AssertSearchAsync("fossilmarker", "Nested 日本語/Deep #/Delete.txt", false);
            Assert.Null(await app.Services.GetRequiredService<IIndexStateStore>().GetFileAsync(id, Path.GetRelativePath(sourceRoot, removedPath), CancellationToken.None));
            Assert.DoesNotContain(await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(id, CancellationToken.None), chunk => chunk.RelativePath.EndsWith("Delete.txt", StringComparison.Ordinal));
            Assert.Equal("Jellyfish drift through a quiet ocean current.", await File.ReadAllTextAsync(keptPath));
            await app.StopAsync();

            async Task AssertSearchAsync(string query, string path, bool expected)
            {
                var search = await client.PostAsJsonAsync("/api/v1/search", new { query, sourceIds = new[] { id }, mode = "Lexical" }); search.EnsureSuccessStatusCode();
                var result = (await search.Content.ReadFromJsonAsync<SearchResponse>())!;
                Assert.Equal(expected, result.Results.Any(hit => hit.RelativePath == path));
            }
        }
        finally
        {
            using var http = new HttpClient();
            if (!embedded) try { await http.DeleteAsync($"http://127.0.0.1:18080/v1/schema/{collection}"); } catch (HttpRequestException) { }
            RecordManifest(root);
        }
    }

    private static async Task LifecycleCoreAsync(bool embedded)
    {
        if (embedded) AssertNativePackageStaged();
        var root = NewRoot(); var collection = "P3Lifecycle" + Guid.NewGuid().ToString("N"); string sourceId;
        try
        {
            var sourcePath = Path.Combine(root, "outside-workspace"); Directory.CreateDirectory(sourcePath);
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "keep.txt"), "A hummingbird feeder contains sweet nectar.");
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "delete.txt"), "Obsolete fossilmarker should disappear.");
            await using (var app = Build(root, true, collection, embedded))
            {
                await app.StartAsync(); using var client = Client(app); client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-normal-token");
                var response = await client.PostAsJsonAsync("/api/v1/catalog/sources", new { rootPath = sourcePath, name = "External source", description = "Test", scope = "global" }); response.EnsureSuccessStatusCode();
                sourceId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("source").GetProperty("sourceId").GetString()!;
                await UntilAsync(async () => (await app.Services.GetRequiredService<ISourceRegistry>().GetAsync(sourceId, CancellationToken.None))?.Status == SourceStatus.Ready);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/sources/{sourceId}/pause", new { })).StatusCode);
                Assert.False((await app.Services.GetRequiredService<SourceIndexingStateStore>().GetAsync(sourceId, CancellationToken.None)).QueryVisible);
                var hiddenChunk = (await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(sourceId, CancellationToken.None))[0];
                Assert.Null(await app.Services.GetRequiredService<IRagSearchService>().GetChunkAsync(hiddenChunk.ChunkId, CancellationToken.None));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.Services.GetRequiredService<IRagSearchService>().SearchAsync(new("nectar", [sourceId], Mode: SearchMode.Lexical), CancellationToken.None));
                await File.WriteAllTextAsync(Path.Combine(sourcePath, "keep.txt"), "Jellyfish drift through a quiet ocean current."); File.Delete(Path.Combine(sourcePath, "delete.txt"));
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/sources/{sourceId}/reindex", new { })).StatusCode);
                await app.StopAsync();
            }
            await using (var app = Build(root, true, collection, embedded))
            {
                await app.StartAsync(); using var client = Client(app); client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-normal-token");
                var states = app.Services.GetRequiredService<SourceIndexingStateStore>();
                Assert.Equal("Paused", (await states.GetAsync(sourceId, CancellationToken.None)).State);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/sources/{sourceId}/resume", new { })).StatusCode);
                await UntilAsync(async () => (await states.GetAsync(sourceId, CancellationToken.None)).QueryVisible);
                var chunks = await app.Services.GetRequiredService<IIndexStateStore>().GetChunksForSourceAsync(sourceId, CancellationToken.None);
                Assert.DoesNotContain(chunks, x => x.RelativePath == "delete.txt"); Assert.Contains(chunks, x => x.Content.Contains("Jellyfish", StringComparison.Ordinal));
                var result = await app.Services.GetRequiredService<IRagSearchService>().SearchAsync(new("Jellyfish", [sourceId], Mode: SearchMode.Lexical), CancellationToken.None);
                Assert.Contains(result.Results, x => x.Content.Contains("Jellyfish", StringComparison.Ordinal));
                await app.Services.GetRequiredService<IIndexCoordinator>().RemoveSourceAsync(sourceId, CancellationToken.None);
                Assert.True(File.Exists(Path.Combine(sourcePath, "keep.txt")));
                await app.StopAsync();
            }
        }
        finally
        {
            using var http = new HttpClient();
            if (!embedded) try { await http.DeleteAsync($"http://127.0.0.1:18080/v1/schema/{collection}"); } catch (HttpRequestException) { }
            RecordManifest(root);
        }
    }

    private static WebApplication Build(string root, bool live, string collection = "UnusedP3", bool embedded = false) => HostApplication.Create([
        "--contentRoot", ContentRoot(), "--urls", "http://127.0.0.1:0", "--environment", "Production",
        "--LocalRag:DataDirectory", Path.Combine(root, "data"), "--LocalRag:Authentication:Token", "integration-normal-token",
        "--LocalRag:Management:Enabled", "true", "--LocalRag:Management:Token", "integration-management-token",
        "--LocalRag:VectorStore:Provider", embedded ? "LanceDb" : "Weaviate", "--LocalRag:VectorStore:LanceDb:Path", Path.Combine(root, "vectors"), "--LocalRag:VectorStore:Weaviate:Endpoint", live ? "http://127.0.0.1:18080" : "http://127.0.0.1:1",
        "--LocalRag:VectorStore:Weaviate:Collection", collection,
        "--LocalRag:Embedding:ModelDirectory", live ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRag", "models", "bge-small-en-v1.5") : Path.Combine(root, "missing-model"),
        "--LocalRag:Indexing:StabilityIntervalMilliseconds", "0", "--LocalRag:Indexing:ReconciliationDispatchPollSeconds", "1"
    ]);
    private static void AssertNativePackageStaged()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "lancedb-native.manifest.json");
        Assert.True(File.Exists(staged), "Stage the fresh native package in the test output directory before production host native tests.");
        using var expected = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("LOCALRAG_LANCEDB_NATIVE_MANIFEST")!));
        using var actual = JsonDocument.Parse(File.ReadAllText(staged));
        Assert.True(string.Equals(expected.RootElement.GetProperty("sha256").GetString(), actual.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Staged host native package does not match the recorded test manifest.");
    }
    private static string ContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "LocalRag.Host", "LocalRag.Host.csproj"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src", "LocalRag.Host");
    }
    internal static string NewRoot()
    {
        var root = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ContentRoot()))!, "artifacts", "runs", "p3-web", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    internal static void RecordManifest(string root)
    {
        SqliteConnection.ClearAllPools();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => new { path = Path.GetRelativePath(root, path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new { completedUtc = DateTimeOffset.UtcNow, purpose = "P3 isolated in-process HTTP/lifecycle test; retained disposable files", outcomeEvidence = "../tests/*.trx", files }, ManifestJson));
    }
    private static string Csrf(string page) => WebUtility.HtmlDecode(Regex.Match(page, "name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value);
    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false })
    { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(30) };
    private static async Task UntilAsync(Func<Task<bool>> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await ready()) await Task.Delay(200, timeout.Token);
    }
}
