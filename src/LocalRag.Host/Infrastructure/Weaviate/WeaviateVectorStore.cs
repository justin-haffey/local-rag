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
    private static readonly IReadOnlyDictionary<string, string> RequiredProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["chunkId"] = "text", ["sourceId"] = "text", ["fileId"] = "text", ["relativePath"] = "text",
        ["language"] = "text", ["symbolName"] = "text", ["startLine"] = "int", ["endLine"] = "int",
        ["ordinal"] = "int", ["content"] = "text", ["contentHash"] = "text", ["tokenCount"] = "int",
        ["embeddingProfileId"] = "text", ["lastIndexedUtc"] = "date"
    };

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
            "chunkId sourceId relativePath language symbolName startLine endLine content contentHash lastIndexedUtc _additional { score }" +
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
            results.Add(new SearchResult(
                row.GetProperty("chunkId").GetString()!, row.GetProperty("sourceId").GetString()!, row.GetProperty("relativePath").GetString()!,
                row.GetProperty("language").GetString()!, row.TryGetProperty("symbolName", out var symbol) && symbol.ValueKind != JsonValueKind.Null ? symbol.GetString() : null,
                row.GetProperty("startLine").GetInt32(), row.GetProperty("endLine").GetInt32(),
                additional.TryGetProperty("score", out var score) ? ReadScore(score) : 0, row.GetProperty("content").GetString()!,
                row.GetProperty("contentHash").GetString()!, DateTimeOffset.Parse(row.GetProperty("lastIndexedUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
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
            properties = new[]
            {
                new { name = "chunkId", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = false },
                new { name = "sourceId", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = false },
                new { name = "fileId", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = false },
                new { name = "relativePath", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = true },
                new { name = "language", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = true },
                new { name = "symbolName", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = true },
                new { name = "startLine", dataType = new[] { "int" }, indexFilterable = false, indexSearchable = false },
                new { name = "endLine", dataType = new[] { "int" }, indexFilterable = false, indexSearchable = false },
                new { name = "ordinal", dataType = new[] { "int" }, indexFilterable = false, indexSearchable = false },
                new { name = "content", dataType = new[] { "text" }, indexFilterable = false, indexSearchable = true },
                new { name = "contentHash", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = false },
                new { name = "tokenCount", dataType = new[] { "int" }, indexFilterable = false, indexSearchable = false },
                new { name = "embeddingProfileId", dataType = new[] { "text" }, indexFilterable = true, indexSearchable = false },
                new { name = "lastIndexedUtc", dataType = new[] { "date" }, indexFilterable = true, indexSearchable = false }
            }
        };
        using var response = await client.PostAsJsonAsync("v1/schema", schema, _json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task ValidateSchemaAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
        foreach (var required in RequiredProperties)
        {
            if (!actual.TryGetValue(required.Key, out var property) || !property.TryGetProperty("dataType", out var dataType) ||
                dataType.ValueKind != JsonValueKind.Array || !dataType.EnumerateArray().Any(value => string.Equals(value.GetString(), required.Value, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Weaviate collection '{_options.Collection}' is missing compatible property '{required.Key}' ({required.Value}).");
            }
        }
    }

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

    private static double ReadScore(JsonElement score) => score.ValueKind switch
    {
        JsonValueKind.Number => score.GetDouble(),
        JsonValueKind.String when double.TryParse(score.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) => value,
        _ => 0
    };
}
