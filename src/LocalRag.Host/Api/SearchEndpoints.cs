using LocalRag.Application;
using LocalRag.Domain;

namespace LocalRag.Api;

public static class SearchEndpoints
{
    /// <summary>Availability is a recoverable search conflict, distinct from transport authentication failure.</summary>
    public static async Task<IResult> ExecuteAsync(SearchApiRequest request, IRagSearchService search, CancellationToken ct)
    {
        try { return Results.Ok(await search.SearchAsync(new SearchRequest(request.Query, request.SourceIds, request.Limit ?? 12, request.Alpha ?? 0.65, request.Mode ?? SearchMode.Hybrid, request.Language, request.PathPrefix), ct)); }
        catch (UnauthorizedAccessException) { return Results.Conflict(new { error = "A selected knowledge base is paused, resuming, rebuilding, removed, or unavailable. Resume it or wait for Ready, then retry with explicit available sources." }); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
    }
}
