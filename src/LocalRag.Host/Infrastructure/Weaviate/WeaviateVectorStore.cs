using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Weaviate;

public sealed class WeaviateVectorStore(HttpClient client, IOptions<LocalRagOptions> options) : IVectorStore
{
    private readonly WeaviateOptions _options = options.Value.Weaviate;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly PropertyDefinition[] RequiredProperties =
    [
        new("chunkId", "text", true, false),
        new("sourceId", "text", true, false),
        new("fileId", "text", true, false),
        new("relativePath", "text", true, true),
        new("language", "text", true, true),
        new("symbolName", "text", true, true),
        new("chunkKind", "text", true, true),
        new("qualifiedSymbolName", "text", true, true),
        new("structuralLocator", "text", true, false),
        new("chunkerId", "text", true, false),
        new("chunkerVersion", "text", true, false),
        new("chunkProfileFingerprint", "text", true, false),
        new("startLine", "int", false, false),
        new("endLine", "int", false, false),
        new("ordinal", "int", false, false),
        new("content", "text", false, true),
        new("contentHash", "text", true, false),
        new("tokenCount", "int", false, false),
        new("embeddingProfileId", "text", true, false),
        new("lastIndexedUtc", "date", true, false)
    ];

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        RequireExternalVectors();
        using var ready = await client.GetAsync("v1/.well-known/ready", cancellationToken);
        if (!ready.IsSuccessStatusCode) throw new InvalidOperationException($"Weaviate readiness returned {(int)ready.StatusCode}.");

        using var schema = await client.GetAsync($"v1/schema/{Uri.EscapeDataString(_options.Collection)}", cancellationToken);
        if (schema.StatusCode == HttpStatusCode.NotFound)
        {
            await CreateSchemaAsync(cancellationToken);
            return;
        }
        await EnsureSuccessAsync(schema, cancellationToken);
        await ValidateSchemaAsync(schema, cancellationToken);
    }

    public async Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0) return;
        await EnsureReadyAsync(cancellationToken);
        foreach (var batch in documents.Chunk(Math.Max(1, _options.BatchSize)))
        {
            var body = new
            {
                objects = batch.Select(document => new
                {
                    @class = _options.Collection,
                    id = ToWeaviateId(document.Chunk.ChunkId),
                    properties = new
                    {
                        chunkId = document.Chunk.ChunkId,
                        sourceId = document.Chunk.SourceId,
                        fileId = document.Chunk.FileId,
                        relativePath = document.Chunk.RelativePath,
                        language = document.Chunk.Language,
                        symbolName = document.Chunk.SymbolName,
                        chunkKind = document.Chunk.ChunkKind,
                        qualifiedSymbolName = document.Chunk.QualifiedSymbolName,
                        structuralLocator = document.Chunk.StructuralLocator,
                        chunkerId = document.Chunk.ChunkerId,
                        chunkerVersion = document.Chunk.ChunkerVersion,
                        chunkProfileFingerprint = document.Chunk.ChunkProfileFingerprint,
                        startLine = document.Chunk.StartLine,
                        endLine = document.Chunk.EndLine,
                        ordinal = document.Chunk.Ordinal,
                        content = document.Chunk.Content,
                        contentHash = document.Chunk.ContentHash,
                        tokenCount = document.Chunk.TokenCount,
                        embeddingProfileId = document.Chunk.EmbeddingProfileId,
                        lastIndexedUtc = document.Chunk.LastIndexedUtc.ToString("O")
                    },
                    vector = document.Vector
                })
            };
            using var response = await client.PostAsJsonAsync("v1/batch/objects", body, _json, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            await EnsureBatchSucceededAsync(response, cancellationToken);
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
    {
        foreach (var chunkId in chunkIds.Distinct(StringComparer.Ordinal))
        {
            using var response = await client.DeleteAsync($"v1/objects/{Uri.EscapeDataString(_options.Collection)}/{ToWeaviateId(chunkId)}", cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    public async Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        var body = new
        {
            match = new
            {
                @class = _options.Collection,
                where = new { path = new[] { "sourceId" }, @operator = "Equal", valueText = sourceId }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Delete, "v1/batch/objects") { Content = JsonContent.Create(body, options: _json) };
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);
        var sourceFilter = request.SourceIds is { Count: > 0 }
            ? $", where: {{operator: Or, operands: [{string.Join(',', request.SourceIds.Select(sourceId => $"{{path: [\"sourceId\"], operator: Equal, valueText: {QuoteGraphQl(sourceId)}}}"))}]}}"
            : string.Empty;
        var vector = string.Join(',', queryVector.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var alpha = request.Alpha.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var query = "{ Get { " + _options.Collection +
            "(hybrid: {query: " + QuoteGraphQl(request.Query) + ", vector: [" + vector + "], alpha: " + alpha + "}" + sourceFilter + ", limit: " + request.Limit + ") { " +
            "chunkId sourceId relativePath language symbolName chunkKind qualifiedSymbolName structuralLocator chunkerId chunkerVersion chunkProfileFingerprint startLine endLine content contentHash lastIndexedUtc _additional { score }" +
            " } } }";
        using var response = await client.PostAsJsonAsync("v1/graphql", new { query }, _json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("errors", out var errors)) throw new InvalidOperationException($"Weaviate GraphQL error: {errors}");
        var results = new List<SearchResult>();
        var rows = document.RootElement.GetProperty("data").GetProperty("Get").GetProperty(_options.Collection);
        foreach (var row in rows.EnumerateArray())
        {
            var additional = row.GetProperty("_additional");
            var startLine = row.GetProperty("startLine").GetInt32();
            var endLine = row.GetProperty("endLine").GetInt32();
            results.Add(new SearchResult(
                row.GetProperty("chunkId").GetString()!, row.GetProperty("sourceId").GetString()!, row.GetProperty("relativePath").GetString()!,
                row.GetProperty("language").GetString()!, row.TryGetProperty("symbolName", out var symbol) && symbol.ValueKind != JsonValueKind.Null ? symbol.GetString() : null,
                startLine, endLine,
                additional.TryGetProperty("score", out var score) ? ReadScore(score) : 0, row.GetProperty("content").GetString()!,
                row.GetProperty("contentHash").GetString()!, DateTimeOffset.Parse(row.GetProperty("lastIndexedUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                ReadStringOrDefault(row, "chunkKind", "text"),
                row.TryGetProperty("qualifiedSymbolName", out var qualifiedSymbol) && qualifiedSymbol.ValueKind != JsonValueKind.Null ? qualifiedSymbol.GetString() : null,
                ReadStringOrDefault(row, "structuralLocator", $"lines:{startLine}-{endLine}"),
                ReadStringOrDefault(row, "chunkerId", "generic"), ReadStringOrDefault(row, "chunkerVersion", "1"),
                ReadStringOrDefault(row, "chunkProfileFingerprint", "legacy-generic-1")));
        }
        return results;
    }

    private async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = new
        {
            @class = _options.Collection,
            vectorizer = "none",
            vectorIndexConfig = new { distance = "cosine" },
            properties = RequiredProperties.Select(CreatePropertyBody).ToArray()
        };
        using var response = await client.PostAsJsonAsync("v1/schema", schema, _json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private Task ValidateSchemaAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ValidateSchemaAsync(response, addMissingProperties: true, cancellationToken);

    private async Task ValidateSchemaAsync(
        HttpResponseMessage response,
        bool addMissingProperties,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("vectorizer", out var vectorizer) || !string.Equals(vectorizer.GetString(), "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Weaviate collection '{_options.Collection}' must use vectorizer 'none'.");
        }

        if (!root.TryGetProperty("vectorIndexConfig", out var vectorIndex) || !vectorIndex.TryGetProperty("distance", out var distance) ||
            !string.Equals(distance.GetString(), "cosine", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Weaviate collection '{_options.Collection}' must use cosine distance.");
        }

        if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Weaviate collection '{_options.Collection}' has no compatible property schema.");
        }

        var actual = properties.EnumerateArray()
            .Where(property => property.TryGetProperty("name", out _))
            .ToDictionary(property => property.GetProperty("name").GetString()!, property => property, StringComparer.Ordinal);

        var missing = RequiredProperties.Where(required => !actual.ContainsKey(required.Name)).ToArray();
        if (missing.Length > 0 && !addMissingProperties)
        {
            throw new InvalidOperationException(
                $"Weaviate collection '{_options.Collection}' did not persist required properties: {string.Join(", ", missing.Select(property => property.Name))}.");
        }

        foreach (var required in missing)
        {
            using var addResponse = await client.PostAsJsonAsync(
                $"v1/schema/{Uri.EscapeDataString(_options.Collection)}/properties",
                CreatePropertyBody(required),
                _json,
                cancellationToken);
            await EnsureSuccessAsync(addResponse, cancellationToken);
        }

        if (missing.Length > 0)
        {
            using var refreshed = await client.GetAsync(
                $"v1/schema/{Uri.EscapeDataString(_options.Collection)}",
                cancellationToken);
            await EnsureSuccessAsync(refreshed, cancellationToken);
            await ValidateSchemaAsync(refreshed, addMissingProperties: false, cancellationToken);
            return;
        }

        foreach (var required in RequiredProperties)
        {
            var property = actual[required.Name];
            if (!property.TryGetProperty("dataType", out var dataType) || dataType.ValueKind != JsonValueKind.Array ||
                 dataType.GetArrayLength() != 1 ||
                 !string.Equals(dataType[0].GetString(), required.DataType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Weaviate collection '{_options.Collection}' has incompatible property '{required.Name}'; expected type '{required.DataType}'.");
            }
            if (!property.TryGetProperty("indexFilterable", out var filterable) || filterable.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
                 filterable.GetBoolean() != required.IndexFilterable ||
                 !property.TryGetProperty("indexSearchable", out var searchable) || searchable.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
                 searchable.GetBoolean() != required.IndexSearchable)
            {
                throw new InvalidOperationException(
                    $"Weaviate collection '{_options.Collection}' has incompatible indexing flags for property '{required.Name}'.");
            }
        }
    }

    private static object CreatePropertyBody(PropertyDefinition property) => new
    {
        name = property.Name,
        dataType = new[] { property.DataType },
        indexFilterable = property.IndexFilterable,
        indexSearchable = property.IndexSearchable
    };

    private static async Task EnsureBatchSucceededAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var objects = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty("objects", out var nested) ? nested : default;
        if (objects.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Weaviate batch response did not contain object results.");
        }

        var errors = new List<string>();
        foreach (var item in objects.EnumerateArray())
        {
            if (item.TryGetProperty("result", out var result))
            {
                if (result.TryGetProperty("errors", out var itemErrors) && itemErrors.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined && itemErrors.ToString().Length > 2)
                {
                    errors.Add(itemErrors.ToString());
                }
                if (result.TryGetProperty("status", out var status) && !string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"status={status.GetString()}");
                }
            }
            else if (item.TryGetProperty("errors", out var itemErrors) && itemErrors.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined && itemErrors.ToString().Length > 2)
            {
                errors.Add(itemErrors.ToString());
            }
        }

        if (errors.Count > 0) throw new InvalidOperationException($"Weaviate batch upsert contained object failures: {string.Join("; ", errors)}");
    }

    private void RequireExternalVectors()
    {
        if (!string.Equals(_options.Vectorizer, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This MVP only supports Weaviate vectorizer 'none'; embeddings are generated by the local ONNX service.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Weaviate request failed ({(int)response.StatusCode}): {content}");
    }

    private static string ToWeaviateId(string chunkId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(chunkId));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes[..16]).ToString();
    }

    private static string QuoteGraphQl(string value) => JsonSerializer.Serialize(value);

    private static string ReadStringOrDefault(JsonElement element, string propertyName, string defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? defaultValue
            : defaultValue;

    private static double ReadScore(JsonElement score) => score.ValueKind switch
    {
        JsonValueKind.Number => score.GetDouble(),
        JsonValueKind.String when double.TryParse(score.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) => value,
        _ => 0
    };

    private sealed record PropertyDefinition(string Name, string DataType, bool IndexFilterable, bool IndexSearchable);
}
