using System.Diagnostics;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;

namespace LocalRag.Application;

public sealed class RagSearchService(
    IEmbeddingService embeddings,
    IVectorStore vectors,
    IIndexStateStore indexState,
    ISourceRegistry sources,
    IChunkProfileStateStore chunkProfiles,
    IChunkProfileOperationGate profileGate,
    OperationalMetrics metrics) : IRagSearchService
{
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("Search query is required.", nameof(request));
        if (request.Limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(request), "Search limit must be between 1 and 50.");
        if (request.Alpha is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "Search alpha must be between 0 and 1.");

        var registeredSources = await sources.ListAsync(cancellationToken);
        var candidateIds = request.SourceIds?.Distinct(StringComparer.Ordinal).ToArray() ?? registeredSources
            .Where(source => source.Status != SourceStatus.Paused)
            .Select(source => source.SourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidateIds.Length == 0)
        {
            metrics.SearchExecuted();
            return new SearchResponse(request.Query, [], 0, 0, false);
        }

        await using var lease = await profileGate.AcquireAsync(candidateIds, cancellationToken);
        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in registeredSources.Where(source =>
                     source.Status != SourceStatus.Paused && candidateIds.Contains(source.SourceId, StringComparer.Ordinal)))
        {
            if (await chunkProfiles.IsQueryVisibleAsync(source.SourceId, cancellationToken)) visibleIds.Add(source.SourceId);
        }
        if (request.SourceIds is not null && candidateIds.Any(id => !visibleIds.Contains(id)))
        {
            throw new UnauthorizedAccessException("One or more requested source IDs are not visible.");
        }
        var requested = request.SourceIds is null ? visibleIds.Order(StringComparer.Ordinal).ToArray() : candidateIds;
        if (requested.Length == 0)
        {
            metrics.SearchExecuted();
            return new SearchResponse(request.Query, [], 0, 0, false);
        }

        var timer = Stopwatch.StartNew();
        var vector = await embeddings.EmbedQueryAsync(request.Query, cancellationToken);
        var results = await vectors.SearchAsync(request with { SourceIds = requested }, vector, cancellationToken);
        metrics.SearchExecuted();
        return new SearchResponse(request.Query, results, results.Count, timer.ElapsedMilliseconds, results.Count >= request.Limit);
    }

    public async Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken)
    {
        var chunk = await indexState.GetChunkAsync(chunkId, cancellationToken);
        if (chunk is null) return null;
        await using var lease = await profileGate.AcquireAsync([chunk.SourceId], cancellationToken);
        chunk = await indexState.GetChunkAsync(chunkId, cancellationToken);
        if (chunk is null || !await chunkProfiles.IsQueryVisibleAsync(chunk.SourceId, cancellationToken)) return null;
        return chunk;
    }
}
