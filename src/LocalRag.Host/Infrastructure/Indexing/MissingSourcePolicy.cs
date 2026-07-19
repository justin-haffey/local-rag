using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Indexing;

/// <summary>Distinguishes briefly unavailable roots from abandoned registrations that should be removed.</summary>
public sealed class MissingSourcePolicy(IOptions<LocalRagOptions> options)
{
    public const string MissingRootMessage = "Source root is no longer accessible.";

    public bool ShouldCleanup(SourceRecord source, DateTimeOffset now)
    {
        if (source.Status != SourceStatus.Degraded ||
            !string.Equals(source.LastError, MissingRootMessage, StringComparison.Ordinal))
        {
            return false;
        }

        var grace = TimeSpan.FromMinutes(Math.Max(1, options.Value.Indexing.MissingSourceCleanupGraceMinutes));
        return now - source.UpdatedUtc >= grace;
    }
}
