using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LocalRag.Application;
using LocalRag.Authentication;
using LocalRag.Configuration;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LocalRag.Api;

public static class ManagerEndpoints
{
    public const string CookieScheme = "ManagerSession";
    public const string RequestScheme = "LocalRequest";

    public static void MapManagerEndpoints(this WebApplication app, RouteGroupBuilder api, RouteGroupBuilder managementApi)
    {
        app.MapPost("/manage/tickets", (BrowserLaunchRequest request, BrowserLaunchTicketStore tickets, IOptions<LocalRagOptions> options) =>
            Results.Ok(new { ticket = tickets.Issue(options.Value.Management.Enabled && EqualSecret(options.Value.Management.Token, request.ManagementToken)) }))
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(LocalTokenAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
        app.MapPost("/manage/session-ticket", async (BrowserTicket request, HttpContext context, BrowserLaunchTicketStore tickets, IAntiforgery antiforgery) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(403);
            try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.StatusCode(403); }
            if (!tickets.Consume(request.Ticket, out var management)) return Results.Unauthorized();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "local-browser") };
            if (management) claims.Add(new("LocalRagManagement", "true"));
            await context.SignInAsync(CookieScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme)), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30), AllowRefresh = false });
            return Results.Ok(new { expiresInMinutes = 30 });
        }).AllowAnonymous();
        app.MapPost("/manage/session", async (BrowserCredentials request, HttpContext context, IOptions<LocalRagOptions> options, IAntiforgery antiforgery) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(403);
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.StatusCode(403); }
            if (!EqualSecret(options.Value.Authentication.Token, request.Token)) return Results.Unauthorized();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "local-browser") };
            if (options.Value.Management.Enabled && EqualSecret(options.Value.Management.Token, request.ManagementToken))
                claims.Add(new("LocalRagManagement", "true"));
            await context.SignInAsync(CookieScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme)),
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30), AllowRefresh = false });
            return Results.Ok(new { expiresInMinutes = 30 });
        }).AllowAnonymous();
        app.MapPost("/manage/logout", async (HttpContext context) => { await context.SignOutAsync(CookieScheme); return Results.NoContent(); }).RequireAuthorization();
        api.MapGet("/lifecycle", async (ISourceRegistry sources, SourceIndexingStateStore states, CancellationToken ct) =>
        {
            var result = new List<SourceIndexingState>();
            foreach (var source in await sources.ListAsync(ct))
            {
                var state = await states.GetAsync(source.SourceId, ct);
                result.Add(source.LifecycleState == LocalRag.Domain.SourceLifecycleState.Removing ? state with { State = "Removing" } : state);
            }
            return Results.Ok(result);
        });
        foreach (var action in new[] { "pause", "resume" })
        {
            var paused = action == "pause";
            api.MapPost("/sources/{sourceId}/" + action, async (string sourceId, SourceLifecycleService lifecycle, CancellationToken ct) =>
            {
                try { return Results.Ok(await lifecycle.SetPausedAsync(sourceId, paused, ct)); }
                catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
                catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            });
        }
        api.MapGet("/folders", (string? path) =>
        {
            try { return Results.Ok(HostFolderPicker.List(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { return Results.BadRequest(new { error = "Folder is missing, disconnected, denied, or a reparse point. Paste an accessible absolute path." }); }
        });
        api.MapGet("/system", async (HealthCheckService health, OperationalMetrics metrics, IOptions<LocalRagOptions> options, CancellationToken ct) =>
        {
            var report = await health.CheckHealthAsync(ct);
            return Results.Ok(new { provider = options.Value.VectorStore.Provider.ToString(), status = report.Status.ToString(), checks = report.Entries.ToDictionary(x => x.Key, x => new { status = x.Value.Status.ToString(), x.Value.Description }), metrics = metrics.Snapshot() });
        });
        managementApi.MapPost("/sources/{sourceId}/remove", async (string sourceId, BrowserConfirmation request, ISourceRegistry sources, ILocalRagManagementService management, CancellationToken ct) =>
            await sources.GetAsync(sourceId, ct) is { } source
                ? Results.Ok(await management.RemoveAsync(source.CanonicalRootPath, request.ConfirmationToken, "local-management", ct))
                : Results.NotFound());
    }

    public static bool SameOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) &&
        string.Equals(origin.GetLeftPart(UriPartial.Authority), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);

    private static bool EqualSecret(string? expected, string? actual) => !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(actual) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}

public sealed record BrowserCredentials(string Token, string? ManagementToken);
public sealed record BrowserConfirmation(string? ConfirmationToken);
public sealed record FolderListing(string? Current, string? Parent, string[] Directories, string[] Drives);

public static class HostFolderPicker
{
    public static FolderListing List(string? path)
    {
        var drives = DriveInfo.GetDrives().Where(x => x.IsReady).Select(x => x.RootDirectory.FullName).ToArray();
        if (string.IsNullOrWhiteSpace(path)) return new(null, null, [], drives);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute host path is required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        EnsureNoReparsePoints(root);
        var children = Directory.EnumerateDirectories(root).Where(x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0).Take(500).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(root, Directory.GetParent(root)?.FullName, children, drives);
    }

    public static void EnsureNoReparsePoints(string path)
    {
        if (!Directory.Exists(path)) throw new ArgumentException("Selected directory is unavailable.");
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse-point source roots are unsupported.");
        // Access is evaluated now, without recursively visiting source content.
        using var readable = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        _ = readable.MoveNext();
    }
}

public sealed record BrowserLaunchRequest(string? ManagementToken);
public sealed record BrowserTicket(string Ticket);
