using System.Security.Cryptography;
using System.Text;
using LocalRag.Domain;

namespace LocalRag.Api;

public sealed record RegisterSourceRequest(string RootPath, string? DisplayName);
public sealed record SearchApiRequest(string Query, IReadOnlyList<string>? SourceIds, int? Limit, double? Alpha);
public sealed record SourceResponse(string SourceId, string RootPathHash, string DisplayName, SourceStatus Status, DateTimeOffset? LastScanUtc, DateTimeOffset? LastSuccessfulIndexUtc, string? LastError);

public static class ApiContractMapping
{
    public static SourceResponse ToResponse(this SourceRecord source) => new(
        source.SourceId,
        HashRootPath(source.CanonicalRootPath),
        source.DisplayName,
        source.Status,
        source.LastScanUtc,
        source.LastSuccessfulIndexUtc,
        source.LastError);

    private static string HashRootPath(string rootPath)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
