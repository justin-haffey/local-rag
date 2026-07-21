using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Processing;

internal sealed record StructuralUnit(
    string Kind,
    string? SymbolName,
    string? QualifiedSymbolName,
    string Locator,
    int StartLine,
    int EndLine);

internal interface IStructuralChunker
{
    string ChunkerId { get; }
    string ChunkerVersion { get; }
    bool Supports(string relativePath);
    bool TryChunk(string relativePath, string normalizedContent, out IReadOnlyList<StructuralUnit> units);
}

internal interface IChunkTokenCounter
{
    int CountTokens(string content);
}

internal sealed class CharacterUpperBoundTokenCounter : IChunkTokenCounter
{
    public int CountTokens(string content) => 2 + content.Count(character => !char.IsWhiteSpace(character));
}

internal static class ChunkingText
{
    public static string[] Lines(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Split('\n');

    public static string Slice(string[] lines, int startLine, int endLine) =>
        string.Join('\n', lines[(startLine - 1)..endLine]);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string NormalizePath(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

    public static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".py" => "python",
        ".go" => "go",
        ".rs" => "rust",
        ".java" => "java",
        ".json" => "json",
        ".yml" or ".yaml" => "yaml",
        ".toml" => "toml",
        ".xml" or ".csproj" or ".props" or ".targets" => "xml",
        ".md" => "markdown",
        ".sql" => "sql",
        ".ps1" or ".sh" => "shell",
        ".docx" => "word",
        ".pdf" => "pdf",
        _ => "text"
    };

    public static ChunkRecord CreateRecord(
        SourceRecord source,
        IndexedFile file,
        string content,
        int startLine,
        int endLine,
        int ordinal,
        string kind,
        string? symbolName,
        string? qualifiedSymbolName,
        string locator,
        string chunkerId,
        string chunkerVersion,
        string fingerprint,
        int tokenCount)
    {
        var normalizedPath = NormalizePath(file.RelativePath);
        var contentHash = Hash(content);
        var identity = string.Join('\n', source.SourceId, normalizedPath, locator, content, chunkerId, chunkerVersion, fingerprint);
        return new ChunkRecord(
            Hash(identity), source.SourceId, file.FileId, normalizedPath, LanguageFor(normalizedPath), symbolName,
            startLine, endLine, ordinal, content, contentHash, tokenCount, source.EmbeddingProfileId,
            DateTimeOffset.UtcNow, kind, qualifiedSymbolName, locator, chunkerId, chunkerVersion, fingerprint);
    }
}

public sealed class ChunkProfileProvider : IChunkProfileProvider
{
    public const int ProfileSchemaVersion = 1;
    private readonly IReadOnlyList<IStructuralChunker> _adapters;
    private readonly LocalRagOptions _options;

    internal ChunkProfileProvider(IEnumerable<IStructuralChunker> adapters, IOptions<LocalRagOptions> options)
    {
        _adapters = adapters.OrderBy(adapter => adapter.ChunkerId, StringComparer.Ordinal).ToArray();
        _options = options.Value;
        Fingerprint = ComputeFingerprint();
    }

    internal ChunkProfileProvider(IOptions<LocalRagOptions> options)
        : this([], options)
    {
    }

    public string Fingerprint { get; }
    public string ChunkerIdentity => _options.Chunking.EnabledAdapters.Any(id => !string.IsNullOrWhiteSpace(id))
        ? "structural-composite/1"
        : "generic/1";

    private string ComputeFingerprint()
    {
        var enabled = _options.Chunking.EnabledAdapters
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var adapterJson = string.Join(',', _adapters.Select(adapter =>
            $"{{\"id\":\"{Escape(adapter.ChunkerId)}\",\"version\":\"{Escape(adapter.ChunkerVersion)}\"}}"));
        var enabledJson = string.Join(',', enabled.Select(id => $"\"{Escape(id)}\""));
        var canonicalJson = $"{{\"profileSchemaVersion\":{ProfileSchemaVersion},\"chunkerIdentity\":\"{Escape(ChunkerIdentity)}\",\"enabledAdapterIds\":[{enabledJson}]," +
            $"\"adapters\":[{adapterJson}],\"generic\":{{\"id\":\"generic\",\"version\":\"1\"}}," +
            $"\"targetTokens\":{_options.Chunking.TargetTokens},\"maximumTokens\":{_options.Chunking.MaximumTokens}," +
            $"\"overlapTokens\":{_options.Chunking.OverlapTokens},\"embeddingProfileId\":\"{Escape(_options.Embedding.ProfileId)}\"," +
            $"\"tokenizerId\":\"{Escape(_options.Embedding.TokenizerId)}\"," +
            $"\"queryPrefix\":\"{Escape(_options.Embedding.QueryPrefix)}\",\"passagePrefix\":\"{Escape(_options.Embedding.PassagePrefix)}\"," +
            $"\"hardModelTokenLimit\":{_options.Embedding.MaximumTokens}}}";
        return ChunkingText.Hash(canonicalJson);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
