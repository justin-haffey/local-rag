using LocalRag.Application;

namespace LocalRag.Infrastructure.Processing;

/// <summary>Reads the existing set of source-code and configuration formats as UTF-8 text.</summary>
public sealed class PlainTextContentExtractor : IContentExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".props", ".targets", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java",
        ".md", ".txt", ".json", ".yml", ".yaml", ".toml", ".xml", ".config", ".sql", ".ps1", ".sh", ".bat", ".cmd"
    };

    public bool Supports(string path) =>
        Extensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string path, CancellationToken cancellationToken) => File.ReadAllTextAsync(path, cancellationToken);
}
