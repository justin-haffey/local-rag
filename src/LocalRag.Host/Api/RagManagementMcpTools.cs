using System.ComponentModel;
using LocalRag.Application;
using ModelContextProtocol.Server;

namespace LocalRag.Api;

[McpServerToolType]
public sealed class RagManagementMcpTools(ILocalRagManagementService management)
{
    private const string PrincipalId = "local-management";

    [McpServerTool(Name = "rag_index"), Description("Register and queue indexing for one accessible local folder.")]
    public Task<ManagementResult> RagIndex(
        [Description("Local folder path to index.")] string folderPath,
        [Description("Optional display name.")] string? displayName = null,
        CancellationToken cancellationToken = default) =>
        management.IndexAsync(folderPath, displayName, PrincipalId, cancellationToken);

    [McpServerTool(Name = "rag_remove_index"), Description("Prepare or confirm removal of one exact registered folder index without deleting source files.")]
    public Task<ManagementResult> RagRemoveIndex(
        [Description("Exact registered folder path.")] string folderPath,
        [Description("One-use confirmation token returned by the prepare call.")] string? confirmationToken = null,
        CancellationToken cancellationToken = default) =>
        management.RemoveAsync(folderPath, confirmationToken, PrincipalId, cancellationToken);

    [McpServerTool(Name = "rag_reset"), Description("Prepare or confirm an irreversible reset of Local RAG state and its owned vector collection.")]
    public Task<ManagementResult> RagReset(
        [Description("One-use confirmation token returned by the prepare call.")] string? confirmationToken = null,
        CancellationToken cancellationToken = default) =>
        management.ResetAsync(confirmationToken, PrincipalId, cancellationToken);
}
