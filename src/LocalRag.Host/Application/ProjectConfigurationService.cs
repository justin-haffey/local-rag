using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalRag.Domain;
using LocalRag.Infrastructure.Sqlite;

namespace LocalRag.Application;

public sealed record ProjectSolution(string Name, string Description);
public sealed record KnowledgeBaseReference(string SourceId);
public sealed record ProjectConfiguration(int SchemaVersion, ProjectSolution Solution, string InstanceId, IReadOnlyList<KnowledgeBaseReference> KnowledgeBases);
public sealed record ProjectConfigurationSnapshot(string? Content, string? Revision);
public sealed record ProjectSelection(ProjectConfiguration Configuration, IReadOnlyList<SourceRecord> Sources);

/// <summary>Explicit-file usage convention; never discovers files or broadens source membership.</summary>
public sealed class ProjectConfigurationService(SqliteDatabase database, ISourceRegistry sources, IRagSearchService search)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly object IdentityGate = new();

    public async Task<string> InstanceIdAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(database.DataDirectory, "catalog-instance.id");
        Directory.CreateDirectory(database.DataDirectory);
        lock (IdentityGate)
        {
            if (!File.Exists(path))
            {
                // CreateNew prevents a second client from changing this public catalog identity.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D")));
                stream.Flush(true);
            }
        }
        var id = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Catalog instance identifier is invalid; restore its saved value.");
        return id;
    }

    public static ProjectConfiguration Parse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Project configuration must be a JSON object.");
            RejectDuplicateProperties(document.RootElement);
            var config = JsonSerializer.Deserialize<ProjectConfiguration>(content, Json) ?? throw new ArgumentException("Project configuration is empty.");
            if (config.SchemaVersion != 1) throw new ArgumentException("Unsupported schemaVersion. Repair the project file before searching.");
            if (config.Solution is null || string.IsNullOrWhiteSpace(config.Solution.Name) || config.Solution.Name.Length > 120 || config.Solution.Description is null || config.Solution.Description.Length > 2000)
                throw new ArgumentException("Solution requires a name of 1–120 characters and a description string of at most 2,000 characters.");
            if (!Guid.TryParseExact(config.InstanceId, "D", out _)) throw new ArgumentException("Project instanceId must be a catalog identifier.");
            if (config.KnowledgeBases is null || config.KnowledgeBases.Any(kb => kb is null || string.IsNullOrWhiteSpace(kb.SourceId)) ||
                config.KnowledgeBases.Select(kb => kb.SourceId).Distinct(StringComparer.Ordinal).Count() != config.KnowledgeBases.Count)
                throw new ArgumentException("knowledgeBases must list distinct nonblank source IDs.");
            // Required properties are checked separately: default-valued deserialization cannot distinguish absence.
            RequireProperties(document.RootElement, "schemaVersion", "solution", "instanceId", "knowledgeBases");
            RequireProperties(document.RootElement.GetProperty("solution"), "name", "description");
            return config with { Solution = config.Solution with { Name = config.Solution.Name.Trim() } };
        }
        catch (JsonException exception) { throw new ArgumentException("Malformed or unsupported project configuration. Repair the JSON before editing or searching.", exception); }
    }

    public async Task<ProjectSelection> ValidateAsync(ProjectConfiguration config, CancellationToken cancellationToken)
    {
        config = Parse(JsonSerializer.Serialize(config, Json));
        if (!string.Equals(config.InstanceId, await InstanceIdAsync(cancellationToken), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Catalog instance mismatch. Explicitly rebind this project using IDs from the configured installation.");
        var catalog = (await sources.ListAsync(cancellationToken)).ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var missing = config.KnowledgeBases.Where(kb => !catalog.ContainsKey(kb.SourceId)).Select(kb => kb.SourceId).ToArray();
        if (missing.Length > 0) throw new ArgumentException($"Unknown or deleted source IDs: {string.Join(", ", missing)}. Edit the project references explicitly.");
        return new(config, config.KnowledgeBases.Select(kb => catalog[kb.SourceId]).ToArray());
    }

    public async Task<SearchResponse> SearchAsync(ProjectConfiguration config, SearchRequest request, CancellationToken cancellationToken)
    {
        var selection = await ValidateAsync(config, cancellationToken);
        if (selection.Sources.Count == 0) throw new ArgumentException("No knowledge bases selected. Add a source reference before searching.");
        var unavailable = selection.Sources.Where(source => source.Status == SourceStatus.Paused || source.LifecycleState != SourceLifecycleState.Active).Select(source => source.SourceId).ToArray();
        if (unavailable.Length > 0) throw new ArgumentException($"Selected sources cannot contribute: {string.Join(", ", unavailable)}. Resume or repair their status before searching.");
        return await search.SearchAsync(request with { SourceIds = selection.Sources.Select(source => source.SourceId).ToArray() }, cancellationToken);
    }

    public async Task<ProjectConfigurationSnapshot> ReadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var path = ConfigPath(projectRoot);
        if (!File.Exists(path)) return new(null, null);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public async Task<ProjectConfigurationSnapshot> SaveAsync(string projectRoot, string content, string? expectedRevision, CancellationToken cancellationToken)
    {
        var path = ConfigPath(projectRoot);
        await ValidateAsync(Parse(content), cancellationToken);
        // A sidecar serializes cooperating host/editor windows. Existing files are never repaired silently.
        await using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var previous = await ReadAsync(projectRoot, cancellationToken);
        if (previous.Revision != expectedRevision) throw new CatalogConflictException("Project configuration changed. Reload and review the proposed edit.");
        if (previous.Content is not null) Parse(previous.Content);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            // Recheck immediately before the atomic replacement to detect external editors.
            if ((await ReadAsync(projectRoot, cancellationToken)).Revision != expectedRevision)
                throw new CatalogConflictException("Project configuration changed while saving. Reload before retrying.");
            File.Move(temporary, path, overwrite: expectedRevision is not null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return await ReadAsync(projectRoot, cancellationToken);
    }

    private static string ConfigPath(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathFullyQualified(projectRoot) || !Directory.Exists(projectRoot))
            throw new ArgumentException("Choose an existing absolute project root explicitly.");
        var path = Path.Combine(Path.GetFullPath(projectRoot), ".localrag.json");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Project configuration cannot be a symbolic link.");
        return path;
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        foreach (var name in names) if (!element.TryGetProperty(name, out _)) throw new ArgumentException($"Required project property '{name}' is missing.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate project configuration properties are not supported.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
