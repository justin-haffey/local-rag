using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Management;

namespace LocalRag.Api;

public static class CatalogEndpoints
{
    public static IServiceCollection AddCatalogServices(this IServiceCollection services) => services
        .AddSingleton<SourceCatalogService>().AddSingleton<ProjectConfigurationService>();

    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/catalog", async (ProjectConfigurationService projects, ISourceRegistry sources, CancellationToken ct) =>
            Results.Ok(new { instanceId = await projects.InstanceIdAsync(ct), sources = await sources.ListAsync(ct) }));
        api.MapGet("/catalog/migration", async (ISourceRegistry sources, CancellationToken ct) =>
            Results.Ok((await sources.ListAsync(ct)).Where(source => source.Scope == "unclassified")));
        api.MapPost("/catalog/sources", async (CatalogRegistration request, SourceCatalogService catalog, HostMaintenanceCoordinator maintenance, CancellationToken ct) =>
        {
            var operational = maintenance.TryAcquireOperational(ct);
            if (operational is null) return Results.StatusCode(503);
            await using var lease = operational;
            return await ExecuteAsync(async () => Results.Ok(await catalog.RegisterAsync(request, lease.CancellationToken)));
        });
        api.MapPost("/catalog/sources/{sourceId}/metadata", async (string sourceId, SourceMetadataUpdate request, SourceCatalogService catalog, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await catalog.UpdateAsync(sourceId, request, ct))));
        api.MapPost("/project-config/validate", async (ProjectConfigContent request, ProjectConfigurationService projects, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await projects.ValidateAsync(ProjectConfigurationService.Parse(request.Content), ct))));
        api.MapPost("/project-config/read", async (ProjectConfigLocation request, ProjectConfigurationService projects, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await projects.ReadAsync(request.ProjectRoot, ct))));
        api.MapPost("/project-config/save", async (ProjectConfigSave request, ProjectConfigurationService projects, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await projects.SaveAsync(request.ProjectRoot, request.Content, request.ExpectedRevision, ct))));
        api.MapPost("/project-config/search", async (ProjectConfigSearch request, ProjectConfigurationService projects, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await projects.SearchAsync(ProjectConfigurationService.Parse(request.Content), request.Search, ct))));
        return api;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CatalogConflictException error) { return Results.Conflict(new { error = error.Message }); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (UnauthorizedAccessException) { return Results.BadRequest(new { error = "Selected source is unavailable, paused or rebuilding. Check its status before retrying." }); }
        catch (DirectoryNotFoundException) { return Results.BadRequest(new { error = "Selected directory is unavailable." }); }
        catch (IOException) { return Results.Conflict(new { error = "File is busy or changed. Reload before retrying." }); }
        catch (InvalidOperationException) { return Results.BadRequest(new { error = "Operation could not complete. Check overlapping roots and host status." }); }
    }
}

public sealed record ProjectConfigContent(string Content);
public sealed record ProjectConfigLocation(string ProjectRoot);
public sealed record ProjectConfigSave(string ProjectRoot, string Content, string? ExpectedRevision);
public sealed record ProjectConfigSearch(string Content, SearchRequest Search);
