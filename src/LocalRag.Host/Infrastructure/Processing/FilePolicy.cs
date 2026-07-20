using LocalRag.Configuration;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Processing;

/// <summary>
/// Applies the configured size and repository-safety rules before a file enters indexing.
/// </summary>
public sealed class FilePolicy(IOptions<LocalRagOptions> options, ContentExtractionService contentExtraction)
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "coverage"
    };

    private readonly IndexingOptions _options = options.Value.Indexing;

    /// <summary>
    /// Determines whether <paramref name="file"/> is safe and supported for indexing under <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Reparse points and sensitive key material are excluded so traversal cannot escape the source tree
    /// and credentials are not persisted in the search index.
    /// </remarks>
    public bool IsEligible(string root, string fullPath, FileInfo file)
    {
        if (file.Length > _options.MaxFileBytes || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(IgnoredDirectories.Contains)) return false;
        var name = Path.GetFileName(fullPath);
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)) return false;
        return contentExtraction.Supports(fullPath);
    }
}
