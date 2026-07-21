using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class WeaviateVectorStoreProvenanceTests
{
    private static readonly string[] ProvenancePropertyNames =
    [
        "chunkKind", "qualifiedSymbolName", "structuralLocator", "chunkerId", "chunkerVersion", "chunkProfileFingerprint"
    ];

    [Fact]
    public async Task EnsureReadyAddsMissingProvenancePropertiesIdempotentlyWithExactIndexingFlags()
    {
        var handler = SchemaHandler.Legacy();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://weaviate/") };
        var store = CreateStore(client);

        await store.EnsureReadyAsync(CancellationToken.None);
        await store.EnsureReadyAsync(CancellationToken.None);

        Assert.True(handler.SchemaReads >= 3);
        Assert.Equal(ProvenancePropertyNames.OrderBy(name => name), handler.AddedProperties.Select(property => property.Name).OrderBy(name => name));
        Assert.Equal(6, handler.AddedProperties.Count);
        AssertProperty(handler, "chunkKind", indexFilterable: true, indexSearchable: true);
        AssertProperty(handler, "qualifiedSymbolName", indexFilterable: true, indexSearchable: true);
        AssertProperty(handler, "structuralLocator", indexFilterable: true, indexSearchable: false);
        AssertProperty(handler, "chunkerId", indexFilterable: true, indexSearchable: false);
        AssertProperty(handler, "chunkerVersion", indexFilterable: true, indexSearchable: false);
        AssertProperty(handler, "chunkProfileFingerprint", indexFilterable: true, indexSearchable: false);
    }

    [Fact]
    public async Task EnsureReadyAddsMissingPropertiesBeforeRejectingWrongExistingType()
    {
        var handler = SchemaHandler.Legacy();
        handler.ReplaceProperty(new PropertyPayload("symbolName", ["int"], true, true));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://weaviate/") };
        var store = CreateStore(client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureReadyAsync(CancellationToken.None));

        Assert.Contains("symbolName", error.Message, StringComparison.Ordinal);
        Assert.Contains("expected type 'text'", error.Message, StringComparison.Ordinal);
        Assert.Equal(ProvenancePropertyNames.OrderBy(name => name), handler.AddedProperties.Select(property => property.Name).OrderBy(name => name));
    }

    [Fact]
    public async Task EnsureReadyRejectsWrongExistingIndexingFlags()
    {
        var handler = SchemaHandler.Complete();
        handler.ReplaceProperty(new PropertyPayload("chunkKind", ["text"], true, false));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://weaviate/") };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateStore(client).EnsureReadyAsync(CancellationToken.None));

        Assert.Contains("chunkKind", error.Message, StringComparison.Ordinal);
        Assert.Contains("indexing flags", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAndSearchPreserveAllChunkProvenance()
    {
        var handler = SchemaHandler.Complete();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://weaviate/") };
        var store = CreateStore(client);
        var chunk = new ChunkRecord(
            "chunk-1", "source-1", "file-1", "src/Widget.cs", "csharp", "Run", 5, 12, 0,
            "void Run() { }", "chunk-hash", 7, "bge", DateTimeOffset.Parse("2026-07-21T12:01:00Z", CultureInfo.InvariantCulture),
            "member", "Example.Widget.Run", "type:Example.Widget/member:Run", "csharp-lexical", "2.0.0", "abc123");

        await store.UpsertAsync([new VectorDocument(chunk, [1f, 0f, 0f])], CancellationToken.None);
        var result = Assert.Single(await store.SearchAsync(new SearchRequest("Run", [chunk.SourceId], 1), [1f, 0f, 0f], CancellationToken.None));

        Assert.NotNull(handler.BatchBody);
        var properties = handler.BatchBody!.RootElement.GetProperty("objects")[0].GetProperty("properties");
        Assert.Equal(chunk.ChunkKind, properties.GetProperty("chunkKind").GetString());
        Assert.Equal(chunk.QualifiedSymbolName, properties.GetProperty("qualifiedSymbolName").GetString());
        Assert.Equal(chunk.StructuralLocator, properties.GetProperty("structuralLocator").GetString());
        Assert.Equal(chunk.ChunkerId, properties.GetProperty("chunkerId").GetString());
        Assert.Equal(chunk.ChunkerVersion, properties.GetProperty("chunkerVersion").GetString());
        Assert.Equal(chunk.ChunkProfileFingerprint, properties.GetProperty("chunkProfileFingerprint").GetString());
        Assert.Contains("chunkKind qualifiedSymbolName structuralLocator chunkerId chunkerVersion chunkProfileFingerprint", handler.GraphQlQuery, StringComparison.Ordinal);
        Assert.Equal(chunk.ChunkKind, result.ChunkKind);
        Assert.Equal(chunk.QualifiedSymbolName, result.QualifiedSymbolName);
        Assert.Equal(chunk.StructuralLocator, result.StructuralLocator);
        Assert.Equal(chunk.ChunkerId, result.ChunkerId);
        Assert.Equal(chunk.ChunkerVersion, result.ChunkerVersion);
        Assert.Equal(chunk.ChunkProfileFingerprint, result.ChunkProfileFingerprint);
    }

    [Fact]
    public async Task SearchAppliesLegacyProvenanceDefaultsWhenStoredObjectHasNoValues()
    {
        var handler = SchemaHandler.Complete();
        handler.UseLegacySearchRow = true;
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://weaviate/") };

        var result = Assert.Single(await CreateStore(client).SearchAsync(
            new SearchRequest("legacy", ["source-1"], 1), [1f, 0f, 0f], CancellationToken.None));

        Assert.Equal("text", result.ChunkKind);
        Assert.Null(result.QualifiedSymbolName);
        Assert.Equal("lines:5-12", result.StructuralLocator);
        Assert.Equal("generic", result.ChunkerId);
        Assert.Equal("1", result.ChunkerVersion);
        Assert.Equal("legacy-generic-1", result.ChunkProfileFingerprint);
    }

    private static WeaviateVectorStore CreateStore(HttpClient client) => new(client, Options.Create(new LocalRagOptions
    {
        Weaviate = new WeaviateOptions { Collection = "RagChunk_v1", Vectorizer = "none", BatchSize = 10 }
    }));

    private static void AssertProperty(SchemaHandler handler, string name, bool indexFilterable, bool indexSearchable)
    {
        var property = Assert.Single(handler.AddedProperties, property => property.Name == name);
        Assert.Equal("text", Assert.Single(property.DataType));
        Assert.Equal(indexFilterable, property.IndexFilterable);
        Assert.Equal(indexSearchable, property.IndexSearchable);
    }

    private sealed class SchemaHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, PropertyPayload> _properties;

        private SchemaHandler(IEnumerable<PropertyPayload> properties) =>
            _properties = properties.ToDictionary(property => property.Name, StringComparer.Ordinal);

        public List<PropertyPayload> AddedProperties { get; } = [];
        public JsonDocument? BatchBody { get; private set; }
        public string GraphQlQuery { get; private set; } = string.Empty;
        public bool UseLegacySearchRow { get; set; }
        public int SchemaReads { get; private set; }

        public static SchemaHandler Legacy() => new(LegacyProperties());

        public static SchemaHandler Complete() => new(LegacyProperties().Concat(
        [
            new("chunkKind", ["text"], true, true),
            new("qualifiedSymbolName", ["text"], true, true),
            new("structuralLocator", ["text"], true, false),
            new("chunkerId", ["text"], true, false),
            new("chunkerVersion", ["text"], true, false),
            new("chunkProfileFingerprint", ["text"], true, false)
        ]));

        public void ReplaceProperty(PropertyPayload property) => _properties[property.Name] = property;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (request.Method == HttpMethod.Get && path == "v1/.well-known/ready") return Json(HttpStatusCode.OK, "{}");
            if (request.Method == HttpMethod.Get && path == "v1/schema/RagChunk_v1")
            {
                SchemaReads++;
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    vectorizer = "none",
                    vectorIndexConfig = new { distance = "cosine" },
                    properties = _properties.Values
                }, JsonOptions));
            }

            if (request.Method == HttpMethod.Post && path == "v1/schema/RagChunk_v1/properties")
            {
                var property = JsonSerializer.Deserialize<PropertyPayload>(await request.Content!.ReadAsStringAsync(cancellationToken), JsonOptions)!;
                AddedProperties.Add(property);
                _properties[property.Name] = property;
                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.Method == HttpMethod.Post && path == "v1/batch/objects")
            {
                BatchBody?.Dispose();
                BatchBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.OK, "[{\"result\":{\"status\":\"SUCCESS\"}}]");
            }

            if (request.Method == HttpMethod.Post && path == "v1/graphql")
            {
                using var requestBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                GraphQlQuery = requestBody.RootElement.GetProperty("query").GetString()!;
                var response = UseLegacySearchRow
                    ? """
                    {"data":{"Get":{"RagChunk_v1":[{
                      "chunkId":"chunk-1","sourceId":"source-1","relativePath":"legacy.cs","language":"csharp","symbolName":null,
                      "chunkKind":null,"qualifiedSymbolName":null,"structuralLocator":null,"chunkerId":null,"chunkerVersion":null,"chunkProfileFingerprint":null,
                      "startLine":5,"endLine":12,"content":"legacy","contentHash":"chunk-hash","lastIndexedUtc":"2026-07-21T12:01:00Z",
                      "_additional":{"score":"0.9"}
                    }]}}}
                    """
                    : """
                    {"data":{"Get":{"RagChunk_v1":[{
                      "chunkId":"chunk-1","sourceId":"source-1","relativePath":"src/Widget.cs","language":"csharp","symbolName":"Run",
                      "chunkKind":"member","qualifiedSymbolName":"Example.Widget.Run","structuralLocator":"type:Example.Widget/member:Run",
                      "chunkerId":"csharp-lexical","chunkerVersion":"2.0.0","chunkProfileFingerprint":"abc123",
                      "startLine":5,"endLine":12,"content":"void Run() { }","contentHash":"chunk-hash","lastIndexedUtc":"2026-07-21T12:01:00Z",
                      "_additional":{"score":"0.9"}
                    }]}}}
                    """;
                return Json(HttpStatusCode.OK, response);
            }

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static IEnumerable<PropertyPayload> LegacyProperties() =>
        [
            new("chunkId", ["text"], true, false), new("sourceId", ["text"], true, false),
            new("fileId", ["text"], true, false), new("relativePath", ["text"], true, true),
            new("language", ["text"], true, true), new("symbolName", ["text"], true, true),
            new("startLine", ["int"], false, false), new("endLine", ["int"], false, false),
            new("ordinal", ["int"], false, false), new("content", ["text"], false, true),
            new("contentHash", ["text"], true, false), new("tokenCount", ["int"], false, false),
            new("embeddingProfileId", ["text"], true, false), new("lastIndexedUtc", ["date"], true, false)
        ];

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string content) => new(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed record PropertyPayload(string Name, string[] DataType, bool IndexFilterable, bool IndexSearchable);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
