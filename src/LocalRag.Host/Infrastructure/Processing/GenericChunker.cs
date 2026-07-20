using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Processing;

public sealed class GenericChunker(IOptions<LocalRagOptions> options) : IChunker
{
    private readonly ChunkingOptions _options = options.Value.Chunking;

    public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent)
    {
        var lines = normalizedContent.Split('\n');
        var chunks = new List<ChunkRecord>();
        var targetCharacters = Math.Max(400, _options.TargetTokens * 4);
        var maximumCharacters = Math.Max(targetCharacters, _options.MaximumTokens * 4);
        var start = 0;
        var ordinal = 0;

        while (start < lines.Length)
        {
            var end = start;
            var length = 0;
            while (end < lines.Length)
            {
                var projected = length + lines[end].Length + 1;
                if (end > start && projected > maximumCharacters) break;
                length = projected;
                end++;
                if (length >= targetCharacters && IsNaturalBoundary(lines, end)) break;
            }

            if (end == start) end++;
            var content = string.Join('\n', lines[start..end]);
            if (!string.IsNullOrWhiteSpace(content))
            {
                var contentHash = Hash(content);
                chunks.Add(new ChunkRecord(
                    ChunkId: Hash($"{source.SourceId}\n{file.RelativePath}\n{start + 1}\n{contentHash}"),
                    SourceId: source.SourceId,
                    FileId: file.FileId,
                    RelativePath: file.RelativePath,
                    Language: LanguageFor(file.RelativePath),
                    SymbolName: null,
                    StartLine: start + 1,
                    EndLine: end,
                    Ordinal: ordinal++,
                    Content: content,
                    ContentHash: contentHash,
                    TokenCount: EstimateTokens(content),
                    EmbeddingProfileId: source.EmbeddingProfileId,
                    LastIndexedUtc: DateTimeOffset.UtcNow));
            }

            if (end >= lines.Length) break;
            start = Math.Max(end - Math.Max(1, _options.OverlapTokens / 8), start + 1);
        }

        return chunks;
    }

    private static bool IsNaturalBoundary(string[] lines, int nextLine) =>
        nextLine >= lines.Length || string.IsNullOrWhiteSpace(lines[nextLine - 1]);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static int EstimateTokens(string content) => Math.Max(1, (int)Math.Ceiling(content.Length / 4d));

    private static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".ts" or ".tsx" => "typescript", ".js" or ".jsx" => "javascript",
        ".py" => "python", ".go" => "go", ".rs" => "rust", ".java" => "java",
        ".json" => "json", ".yml" or ".yaml" => "yaml", ".toml" => "toml",
        ".xml" or ".csproj" or ".props" or ".targets" => "xml", ".md" => "markdown",
        ".sql" => "sql", ".ps1" or ".sh" => "shell", ".docx" => "word", _ => "text"
    };
}
