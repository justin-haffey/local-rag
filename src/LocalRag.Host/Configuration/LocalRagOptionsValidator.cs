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
        if (options.Indexing.ReconciliationIntervalMinutes is < 1 or > 1_440)
        {
            errors.Add("Indexing.ReconciliationIntervalMinutes must be between 1 and 1440.");
        }
        if (options.Indexing.ReconciliationLeaseDurationSeconds is < 30 or > 3_600)
        {
            errors.Add("Indexing.ReconciliationLeaseDurationSeconds must be between 30 and 3600.");
        }
        if (options.Indexing.ReconciliationLeaseRenewalSeconds is < 5 or > 1_200)
        {
            errors.Add("Indexing.ReconciliationLeaseRenewalSeconds must be between 5 and 1200.");
        }
        if (options.Indexing.ReconciliationLeaseRenewalSeconds >= options.Indexing.ReconciliationLeaseDurationSeconds)
        {
            errors.Add("Indexing.ReconciliationLeaseRenewalSeconds must be shorter than ReconciliationLeaseDurationSeconds.");
        }
        if (options.Indexing.MaxConcurrentReconciliations is < 1 or > 32)
        {
            errors.Add("Indexing.MaxConcurrentReconciliations must be between 1 and 32.");
        }
        if (options.Indexing.ReconciliationDispatchPollSeconds is < 1 or > 60)
        {
            errors.Add("Indexing.ReconciliationDispatchPollSeconds must be between 1 and 60.");
        }
        if (options.Indexing.ReconciliationHistoryLimit is < 1 or > 100)
        {
            errors.Add("Indexing.ReconciliationHistoryLimit must be between 1 and 100.");
        }
        if (options.Management.ConfirmationLifetimeSeconds is < 30 or > 600)
        {
            errors.Add("Management.ConfirmationLifetimeSeconds must be between 30 and 600.");
        }
        if (options.Management.MaintenanceDrainTimeoutSeconds is < 5 or > 300)
        {
            errors.Add("Management.MaintenanceDrainTimeoutSeconds must be between 5 and 300.");
        }
        if (options.Management.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Management.Token))
            {
                errors.Add("Management.Token is required when management is enabled.");
            }
            else if (string.Equals(options.Management.Token, options.Authentication.Token, StringComparison.Ordinal))
            {
                errors.Add("Management.Token must be distinct from Authentication.Token.");
            }
        }
        var enabled = options.Chunking.EnabledAdapters ?? [];
        var unknown = enabled.Where(adapter => !SupportedAdapters.Contains(adapter)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) errors.Add($"Unknown structural adapters: {string.Join(", ", unknown)}.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
