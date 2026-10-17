using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Application;
using LocalRag.Domain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class CatalogMcpTransportTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] DeletedSources = ["deleted-source"];

    [EnvironmentFact("LOCALRAG_P2_MCP_TESTS", "1")]
    public async Task GivenProjectFileUsesExactIdsThroughProductionHttpMcpWithoutCatalogDiscovery()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LocalRag.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Path.Combine(repository!.FullName, "artifacts", "runs", "p2-mcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var endpoint = Environment.GetEnvironmentVariable("WEAVIATE_TEST_ENDPOINT") ?? "http://127.0.0.1:18080";
        var collection = "P2Mcp" + Guid.NewGuid().ToString("N");
        var calls = 0;
        try
        {
            await using var app = HostApplication.Create([
                "--contentRoot", Path.Combine(repository.FullName, "src", "LocalRag.Host"), "--urls", "http://127.0.0.1:0", "--environment", "Production",
                "--LocalRag:DataDirectory", Path.Combine(root, "data"), "--LocalRag:Authentication:Token", "p2-transport-integration-token",
                "--LocalRag:VectorStore:Provider", "Weaviate", "--LocalRag:VectorStore:Weaviate:Endpoint", endpoint,
                "--LocalRag:VectorStore:Weaviate:Collection", collection, "--LocalRag:Embedding:ModelDirectory", Path.Combine(root, "deliberately-missing-model")
            ]);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", "p2-transport-integration-token");
            var registry = app.Services.GetRequiredService<ISourceRegistry>();
            var profiles = app.Services.GetRequiredService<IChunkProfileStateStore>();
            var vectors = app.Services.GetRequiredService<IVectorStore>();
            var sources = new List<SourceRecord>();
            foreach (var name in new[] { "selected", "outside-project" })
            {
                var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
                var source = await registry.RegisterAsync(directory, name, CancellationToken.None);
                await profiles.GetOrCreateAsync(source.SourceId, "legacy-generic-1", false, CancellationToken.None);
                await registry.SetStatusAsync(source.SourceId, SourceStatus.Ready, null, CancellationToken.None);
                sources.Add(source);
            }
            // Seed deterministic rows through the real provider. No model is available in this host.
            var documents = new[] {
                Document(sources[0], "selected-markdown", "src/reference.md", "markdown", "coral needle selected knowledge"),
                Document(sources[0], "selected-wrong-language", "src/reference.cs", "csharp", "coral needle wrong language"),
                Document(sources[0], "selected-wrong-path", "other/reference.md", "markdown", "coral needle wrong path"),
                Document(sources[1], "outside-project", "src/reference.md", "markdown", "coral needle outside project")
            };
            await vectors.UpsertAsync(documents, CancellationToken.None);
            var instance = await app.Services.GetRequiredService<ProjectConfigurationService>().InstanceIdAsync(CancellationToken.None);
            var config = new ProjectConfiguration(1, new("Given project", "Explicit membership"), instance, [new(sources[0].SourceId)]);
            var projectFile = Path.Combine(root, ".localrag.json");
            await File.WriteAllTextAsync(projectFile, JsonSerializer.Serialize(config, ProjectConfigurationService.Json));

            await using var transport = new HttpClientTransport(new() { Endpoint = new(new Uri(address), "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            // MCP initialization is protocol setup. No tools/list, rag_list_sources or workspace handshake occurs.
            async Task<SearchResponse> QueryGivenFile(string content)
            {
                var file = ProjectConfigurationService.Parse(content);
                using var validation = await http.PostAsJsonAsync("/api/v1/project-config/validate", new { content }, timeout.Token);
                if (!validation.IsSuccessStatusCode || file.KnowledgeBases.Count == 0) throw new ArgumentException("Given project configuration needs repair.");
                calls++;
                var result = await mcp.CallToolAsync("rag_search", new Dictionary<string, object?>
                {
                    ["query"] = "coral",
                    ["sourceIds"] = file.KnowledgeBases.Select(kb => kb.SourceId).ToArray(),
                    ["limit"] = 1,
                    ["mode"] = "Lexical",
                    ["alpha"] = .25,
                    ["language"] = "markdown",
                    ["pathPrefix"] = "src/"
                }, cancellationToken: timeout.Token);
                Assert.False(result.IsError == true);
                return JsonSerializer.Deserialize<SearchResponse>(Assert.Single(result.Content.OfType<TextContentBlock>()).Text, Json)!;
            }
            var response = await QueryGivenFile(await File.ReadAllTextAsync(projectFile));
            var selected = Assert.Single(response.Results);
            Assert.Equal(sources[0].SourceId, selected.SourceId);
            Assert.Equal("selected-markdown", selected.ChunkId);
            Assert.Equal("markdown", selected.Language);
            Assert.StartsWith("src/", selected.RelativePath, StringComparison.Ordinal);

            using var rest = await http.PostAsJsonAsync("/api/v1/search", new { query = "coral", sourceIds = new[] { sources[0].SourceId }, limit = 1, mode = "Lexical", alpha = .25, language = "markdown", pathPrefix = "src/" });
            rest.EnsureSuccessStatusCode();
            var restResponse = (await rest.Content.ReadFromJsonAsync<SearchResponse>(Json))!;
            Assert.Equal(response.Results.Select(row => row.ChunkId), restResponse.Results.Select(row => row.ChunkId));
            foreach (var invalid in new[] {
                "{broken", JsonSerializer.Serialize(config with { SchemaVersion = 2 }, ProjectConfigurationService.Json),
                JsonSerializer.Serialize(config with { InstanceId = Guid.NewGuid().ToString("D") }, ProjectConfigurationService.Json),
                JsonSerializer.Serialize(config with { KnowledgeBases = [new("deleted-source")] }, ProjectConfigurationService.Json),
                JsonSerializer.Serialize(config with { KnowledgeBases = [] }, ProjectConfigurationService.Json) })
                await Assert.ThrowsAsync<ArgumentException>(() => QueryGivenFile(invalid));
            Assert.Equal(1, calls);
            var empty = await mcp.CallToolAsync("rag_search", new Dictionary<string, object?> { ["query"] = "coral", ["sourceIds"] = Array.Empty<string>(), ["mode"] = "Lexical" }, cancellationToken: timeout.Token);
            Assert.False(empty.IsError == true);
            Assert.Empty(JsonSerializer.Deserialize<SearchResponse>(Assert.Single(empty.Content.OfType<TextContentBlock>()).Text, Json)!.Results);
            var unknown = await mcp.CallToolAsync("rag_search", new Dictionary<string, object?> { ["query"] = "coral", ["sourceIds"] = DeletedSources, ["mode"] = "Lexical" }, cancellationToken: timeout.Token);
            Assert.True(unknown.IsError == true);
            await File.WriteAllTextAsync(Path.Combine(root, "transport-outcome.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                configuredSourceIds = config.KnowledgeBases,
                returnedSourceId = selected.SourceId,
                selected.ChunkId,
                successfulConfigAwareMcpCalls = calls,
                invalidConfigurationsStoppedBeforeMcp = 5,
                protocol = "Official SDK Streamable HTTP; initialized then direct tools/call; no catalog/workspace discovery",
                provider = "Real Weaviate deterministic seeded rows",
                model = "Deliberately missing; lexical succeeded",
                emptyScopeResults = 0,
                restMcpSameResults = true,
                projectFileSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(projectFile)))
            }, Json));
            await app.StopAsync();
        }
        finally
        {
            using var cleanup = new HttpClient();
            try { await cleanup.DeleteAsync(endpoint.TrimEnd('/') + "/v1/schema/" + collection); } catch (HttpRequestException) { }
            SqliteConnection.ClearAllPools();
        }
    }

    private static VectorDocument Document(SourceRecord source, string id, string path, string language, string content) => new(
        new(id, source.SourceId, id, path, language, null, 1, 1, 0, content, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), 12, source.EmbeddingProfileId, DateTimeOffset.UtcNow),
        Enumerable.Repeat(1f / MathF.Sqrt(384), 384).ToArray());
}
