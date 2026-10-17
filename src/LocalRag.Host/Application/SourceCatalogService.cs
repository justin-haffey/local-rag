using LocalRag.Domain;
using LocalRag.Infrastructure.Sqlite;

namespace LocalRag.Application;

public sealed class CatalogConflictException(string message) : Exception(message);
public sealed record SourceMetadataUpdate(string Name, string Description, string Scope, string? OwningProjectRoot, long ExpectedRevision);
public sealed record CatalogRegistration(string RootPath, string Name, string Description, string Scope, string? OwningProjectRoot);
public sealed record CatalogRegistrationResult(SourceRecord Source, bool AlreadyRegistered);

/// <summary>Provider-neutral metadata operations. Metadata edits never enter the indexing pipeline.</summary>
public sealed class SourceCatalogService(SqliteSourceRegistry sources, IIndexCoordinator coordinator) : IDisposable
{
    private readonly SemaphoreSlim _registrations = new(1, 1);
    public void Dispose() => _registrations.Dispose();

    public async Task<CatalogRegistrationResult> RegisterAsync(CatalogRegistration request, CancellationToken cancellationToken)
    {
        var metadata = Validate(new(request.Name, request.Description, request.Scope, request.OwningProjectRoot, 0));
        return await RegisterCoreAsync(request.RootPath, metadata, cancellationToken);
    }

    /// <summary>Preserves the previous two-field API while labeling its unclassified intent honestly.</summary>
    public Task<CatalogRegistrationResult> RegisterLegacyAsync(string rootPath, string? displayName, CancellationToken cancellationToken) =>
        RegisterCoreAsync(rootPath, new(string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath)) : displayName.Trim(), "", "unclassified", null, 0), cancellationToken);

    private async Task<CatalogRegistrationResult> RegisterCoreAsync(string rootPath, SourceMetadataUpdate metadata, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath)) throw new ArgumentException("Choose an absolute source root explicitly.");
        if (string.IsNullOrWhiteSpace(metadata.Name) || metadata.Name.Length > 120) throw new ArgumentException("Name must contain 1–120 characters.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        LocalRag.Api.HostFolderPicker.EnsureNoReparsePoints(canonical);
        await _registrations.WaitAsync(cancellationToken);
        try
        {
            var existing = (await sources.ListAsync(cancellationToken)).FirstOrDefault(source =>
                source.CanonicalRootPath.Equals(canonical, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return new(existing, true);
            var source = await sources.RegisterCatalogAsync(canonical, metadata.Name, metadata.Description, metadata.Scope, metadata.OwningProjectRoot, cancellationToken);
            await coordinator.QueueInitialIndexAsync(source.SourceId, cancellationToken);
            return new(source, false);
        }
        finally { _registrations.Release(); }
    }

    public Task<SourceRecord> UpdateAsync(string sourceId, SourceMetadataUpdate request, CancellationToken cancellationToken)
    {
        var value = Validate(request);
        return sources.UpdateMetadataAsync(sourceId, value.Name, value.Description, value.Scope, value.OwningProjectRoot, value.ExpectedRevision, cancellationToken);
    }

    public static SourceMetadataUpdate Validate(SourceMetadataUpdate request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120) throw new ArgumentException("Name must contain 1–120 characters.");
        if (request.Description is null || request.Description.Length > 2000) throw new ArgumentException("Description must be text of at most 2,000 characters.");
        if (request.Scope is not ("local" or "global")) throw new ArgumentException("Choose local or global scope explicitly.");
        string? project = null;
        if (request.Scope == "local")
        {
            if (string.IsNullOrWhiteSpace(request.OwningProjectRoot) || !Path.IsPathFullyQualified(request.OwningProjectRoot))
                throw new ArgumentException("Choose an absolute owning project root for a local knowledge base.");
            project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OwningProjectRoot));
        }
        if (request.ExpectedRevision < 0) throw new ArgumentException("Metadata revision cannot be negative.");
        return request with { Name = name, OwningProjectRoot = project };
    }
}
