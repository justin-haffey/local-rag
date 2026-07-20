using LocalRag.Application;

namespace LocalRag.Infrastructure.Processing;

/// <summary>Selects the single extractor responsible for a path and normalizes its text for stable hashing and chunking.</summary>
public sealed class ContentExtractionService(IEnumerable<IContentExtractor> extractors)
{
    private readonly IReadOnlyList<IContentExtractor> _extractors = extractors.ToArray();

    public bool Supports(string path) => _extractors.Any(extractor => extractor.Supports(path));

    public async Task<string> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        var matches = _extractors.Where(extractor => extractor.Supports(path)).ToArray();
        if (matches.Length == 0) throw new NotSupportedException($"No content extractor supports '{Path.GetExtension(path)}' files.");
        if (matches.Length > 1) throw new InvalidOperationException($"Multiple content extractors support '{path}'.");

        var content = await matches[0].ExtractAsync(path, cancellationToken);
        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
