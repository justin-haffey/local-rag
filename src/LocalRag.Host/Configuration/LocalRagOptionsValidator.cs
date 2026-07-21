using Microsoft.Extensions.Options;

namespace LocalRag.Configuration;

public sealed class LocalRagOptionsValidator : IValidateOptions<LocalRagOptions>
{
    private static readonly HashSet<string> SupportedAdapters =
    [
        "csharp", "typescript-javascript", "python", "markdown", "json", "yaml", "toml", "xml"
    ];

    public ValidateOptionsResult Validate(string? name, LocalRagOptions options)
    {
        var errors = new List<string>();
        var hardLimit = Math.Min(options.Chunking.MaximumTokens, options.Embedding.MaximumTokens);
        if (options.Chunking.MaximumTokens < 3) errors.Add("Chunking.MaximumTokens must be at least 3.");
        if (options.Embedding.MaximumTokens < 3) errors.Add("Embedding.MaximumTokens must be at least 3.");
        if (options.Chunking.TargetTokens < 3 || options.Chunking.TargetTokens > hardLimit)
        {
            errors.Add("Chunking.TargetTokens must be between 3 and the effective hard token limit.");
        }
        if (options.Chunking.OverlapTokens < 0) errors.Add("Chunking.OverlapTokens cannot be negative.");
        if (string.IsNullOrWhiteSpace(options.Embedding.TokenizerId)) errors.Add("Embedding.TokenizerId is required.");
        var enabled = options.Chunking.EnabledAdapters ?? [];
        var unknown = enabled.Where(adapter => !SupportedAdapters.Contains(adapter)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) errors.Add($"Unknown structural adapters: {string.Join(", ", unknown)}.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
