using System.Diagnostics;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;

namespace LocalRag.Application;

public sealed class RagSearchService(IEmbeddingService embeddings, IVectorStore vectors, IIndexStateStore indexState, ISourceRegistry sources, OperationalMetrics metrics) : IRagSearchService
{
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("Search query is required.", nameof(request));
        if (request.Limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(request), "Search limit must be between 1 and 50.");
        if (request.Alpha is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "Search alpha must be between 0 and 1.");

        var visibleSources = await sources.ListAsync(cancellationToken);
        var visibleIds = visibleSources.Where(source => source.Status != SourceStatus.Paused).Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        var requested = request.SourceIds?.Distinct(StringComparer.Ordinal).ToArray() ?? visibleIds.ToArray();
        if (requested.Any(id => !visibleIds.Contains(id))) throw new UnauthorizedAccessException("One or more requested source IDs are not visible.");

        var timer = Stopwatch.StartNew();
        var vector = await embeddings.EmbedQueryAsync(request.Query, cancellationToken);
        var results = await vectors.SearchAsync(request with { SourceIds = requested }, vector, cancellationToken);
        metrics.SearchExecuted();
        return new SearchResponse(request.Query, results, results.Count, timer.ElapsedMilliseconds, results.Count >= request.Limit);
    }

    public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) => indexState.GetChunkAsync(chunkId, cancellationToken);
}
