using System.ComponentModel;
using LocalRag.Application;
using LocalRag.Domain;
using ModelContextProtocol.Server;

namespace LocalRag.Api;

[McpServerToolType]
public sealed class RagMcpTools(IRagSearchService search, ISourceRegistry sources)
{
    [McpServerTool(Name = "rag_search"), Description("Search visible indexed developer folders with hybrid lexical and semantic retrieval.")]
    public Task<SearchResponse> RagSearch(
        [Description("Natural-language or exact-symbol search query.")] string query,
        [Description("Optional source IDs to search.")] string[]? sourceIds = null,
        [Description("Maximum results, from 1 to 50.")] int limit = 12,
        CancellationToken cancellationToken = default) =>
        search.SearchAsync(new SearchRequest(query, sourceIds, limit), cancellationToken);

    [McpServerTool(Name = "rag_get_chunk"), Description("Retrieve a previously indexed chunk by its stable chunk ID.")]
    public Task<ChunkRecord?> RagGetChunk(
        [Description("Stable chunk ID returned by rag_search.")] string chunkId,
        CancellationToken cancellationToken = default) => search.GetChunkAsync(chunkId, cancellationToken);

    [McpServerTool(Name = "rag_list_sources"), Description("List RAG sources visible to this local client.")]
    public async Task<IReadOnlyList<SourceResponse>> RagListSources(CancellationToken cancellationToken = default) =>
        (await sources.ListAsync(cancellationToken)).Select(source => source.ToResponse()).ToArray();

    [McpServerTool(Name = "rag_get_source_status"), Description("Get status and last successful indexing time for one RAG source.")]
    public async Task<SourceResponse?> RagGetSourceStatus(
        [Description("Source ID returned by rag_list_sources.")] string sourceId,
        CancellationToken cancellationToken = default) => (await sources.GetAsync(sourceId, cancellationToken))?.ToResponse();
}
