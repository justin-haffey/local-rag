using LocalRag.Domain;

namespace LocalRag.Api;

public sealed record RegisterSourceRequest(string RootPath, string? DisplayName);
public sealed record SearchApiRequest(string Query, IReadOnlyList<string>? SourceIds, int? Limit, double? Alpha);
public sealed record SourceResponse(string SourceId, string DisplayName, SourceStatus Status, DateTimeOffset? LastScanUtc, DateTimeOffset? LastSuccessfulIndexUtc, string? LastError);

public static class ApiContractMapping
{
    public static SourceResponse ToResponse(this SourceRecord source) => new(source.SourceId, source.DisplayName, source.Status, source.LastScanUtc, source.LastSuccessfulIndexUtc, source.LastError);
}
