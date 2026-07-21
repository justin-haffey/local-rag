using System.Net;
using System.Text.Json;
using LocalRag.Api;
using LocalRag.Application;
using LocalRag.Authentication;
using LocalRag.Configuration;
using LocalRag.Health;
using LocalRag.Infrastructure.Embeddings;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using LocalRag.Infrastructure.Weaviate;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
ConfigureLocalOverridePrecedence(builder.Configuration);
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<LocalRagOptions>, LocalRagOptionsValidator>();
builder.Services.AddOptions<LocalRagOptions>()
    .Bind(builder.Configuration.GetSection(LocalRagOptions.SectionName))
    .ValidateOnStart();
ValidateLocalHosting(builder.Configuration);
var configuredOptions = builder.Configuration.GetSection(LocalRagOptions.SectionName).Get<LocalRagOptions>() ?? new LocalRagOptions();
if (string.IsNullOrWhiteSpace(configuredOptions.Authentication.Token))
{
    throw new InvalidOperationException("LocalRag:Authentication:Token must be supplied by the extension or local host configuration.");
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);

builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<SqliteSourceRegistry>();
builder.Services.AddSingleton<ISourceRegistry>(services => services.GetRequiredService<SqliteSourceRegistry>());
builder.Services.AddSingleton<SqliteIndexStateStore>();
builder.Services.AddSingleton<IIndexStateStore>(services => services.GetRequiredService<SqliteIndexStateStore>());
builder.Services.AddSingleton<ChunkProfileOperationGate>();
builder.Services.AddSingleton<IChunkProfileOperationGate>(services => services.GetRequiredService<ChunkProfileOperationGate>());
builder.Services.AddSingleton<SqliteChunkProfileStateStore>(services => new SqliteChunkProfileStateStore(
    services.GetRequiredService<SqliteDatabase>(),
    services.GetRequiredService<IChunkProfileOperationGate>()));
builder.Services.AddSingleton<IChunkProfileStateStore>(services => services.GetRequiredService<SqliteChunkProfileStateStore>());
builder.Services.AddSingleton<IContentExtractor, PlainTextContentExtractor>();
builder.Services.AddSingleton<IContentExtractor, WordDocumentContentExtractor>();
builder.Services.AddSingleton<IPdfOcrService, TesseractPdfOcrService>();
builder.Services.AddSingleton<IContentExtractor, PdfContentExtractor>();
builder.Services.AddSingleton<ContentExtractionService>();
builder.Services.AddSingleton<FilePolicy>();
builder.Services.AddSingleton<IStructuralChunker, CSharpStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, TypeScriptJavaScriptStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, PythonStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, MarkdownStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, JsonStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, YamlStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, TomlStructuralChunker>();
builder.Services.AddSingleton<IStructuralChunker, XmlStructuralChunker>();
builder.Services.AddSingleton<ChunkProfileProvider>(services => new ChunkProfileProvider(
    services.GetServices<IStructuralChunker>(),
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalRagOptions>>()));
builder.Services.AddSingleton<IChunkProfileProvider>(services => services.GetRequiredService<ChunkProfileProvider>());
builder.Services.AddSingleton<BertWordPieceTokenizer>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalRagOptions>>().Value;
    var modelDirectory = Environment.ExpandEnvironmentVariables(options.Embedding.ModelDirectory);
    return new BertWordPieceTokenizer(Path.Combine(modelDirectory, "vocab.txt"));
});
builder.Services.AddSingleton<IChunkTokenCounter>(services => services.GetRequiredService<BertWordPieceTokenizer>());
builder.Services.AddSingleton<GenericChunker>(services => new GenericChunker(
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalRagOptions>>(),
    services.GetRequiredService<IChunkProfileProvider>(),
    services.GetRequiredService<IChunkTokenCounter>()));
builder.Services.AddSingleton<IChunker>(services => new CompositeChunker(
    services.GetServices<IStructuralChunker>(),
    services.GetRequiredService<GenericChunker>(),
    services.GetRequiredService<IChunkProfileProvider>(),
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalRagOptions>>(),
    services.GetRequiredService<IChunkTokenCounter>(),
    services.GetRequiredService<ILogger<CompositeChunker>>()));
builder.Services.AddSingleton<FileIndexingService>();
builder.Services.AddSingleton<BgeOnnxEmbeddingService>(services => new BgeOnnxEmbeddingService(
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalRagOptions>>(),
    services.GetRequiredService<BertWordPieceTokenizer>()));
builder.Services.AddSingleton<IEmbeddingService>(services => services.GetRequiredService<BgeOnnxEmbeddingService>());
builder.Services.AddHttpClient<IVectorStore, WeaviateVectorStore>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalRagOptions>>().Value.Weaviate;
    client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    if (!string.IsNullOrWhiteSpace(options.ApiKey)) client.DefaultRequestHeaders.Authorization = new("Bearer", options.ApiKey);
});
builder.Services.AddSingleton<IndexWorkChannel>();
builder.Services.AddSingleton<OperationalMetrics>();
builder.Services.AddSingleton<IndexJobStore>();
builder.Services.AddSingleton<SourceWatcherRegistry>();
builder.Services.AddSingleton<MissingSourcePolicy>();
builder.Services.AddSingleton<IndexCoordinator>();
builder.Services.AddSingleton<IIndexCoordinator>(services => services.GetRequiredService<IndexCoordinator>());
builder.Services.AddSingleton<IRagSearchService, RagSearchService>();
builder.Services.AddHostedService<IndexWorker>();
builder.Services.AddHostedService<StartupInitializationService>();
builder.Services.AddHostedService<ReconciliationService>();
builder.Services.AddHealthChecks()
    .AddCheck<SqliteHealthCheck>("sqlite", failureStatus: HealthStatus.Unhealthy)
    .AddCheck<WeaviateHealthCheck>("weaviate", failureStatus: HealthStatus.Degraded)
    .AddCheck<EmbeddingAssetsHealthCheck>("embedding-assets", failureStatus: HealthStatus.Degraded);
builder.Services.AddAuthentication(LocalTokenAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(LocalTokenAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<RagMcpTools>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if (!string.IsNullOrEmpty(host) && (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address)))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Only loopback Host headers are accepted.");
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Name is "sqlite" or "weaviate" or "embedding-assets",
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description })
        }));
    }
});

var api = app.MapGroup("/api/v1").RequireAuthorization();
app.MapGet("/metrics", (OperationalMetrics metrics) => Results.Ok(metrics.Snapshot())).RequireAuthorization();
api.MapPost("/sources", async (RegisterSourceRequest request, ISourceRegistry sources, IIndexCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var source = await sources.RegisterAsync(request.RootPath, request.DisplayName, cancellationToken);
    await coordinator.QueueInitialIndexAsync(source.SourceId, cancellationToken);
    return Results.Created($"/api/v1/sources/{source.SourceId}", source.ToResponse());
});
api.MapGet("/sources", async (ISourceRegistry sources, CancellationToken cancellationToken) => (await sources.ListAsync(cancellationToken)).Select(source => source.ToResponse()));
api.MapGet("/sources/{sourceId}", async (string sourceId, ISourceRegistry sources, CancellationToken cancellationToken) =>
    await sources.GetAsync(sourceId, cancellationToken) is { } source ? Results.Ok(source.ToResponse()) : Results.NotFound());
api.MapGet("/sources/{sourceId}/status", async (string sourceId, ISourceRegistry sources, CancellationToken cancellationToken) =>
    await sources.GetAsync(sourceId, cancellationToken) is { } source ? Results.Ok(source.ToResponse()) : Results.NotFound());
api.MapDelete("/sources/{sourceId}", async (string sourceId, ISourceRegistry sources, IIndexCoordinator coordinator, CancellationToken cancellationToken) =>
{
    if (await sources.GetAsync(sourceId, cancellationToken) is null) return Results.NotFound();
    await coordinator.RemoveSourceAsync(sourceId, cancellationToken);
    return Results.NoContent();
});
api.MapPost("/sources/{sourceId}/reindex", async (string sourceId, ISourceRegistry sources, IIndexCoordinator coordinator, CancellationToken cancellationToken) =>
{
    if (await sources.GetAsync(sourceId, cancellationToken) is null) return Results.NotFound();
    await coordinator.ReindexAsync(sourceId, cancellationToken);
    return Results.Accepted($"/api/v1/sources/{sourceId}/status");
});
api.MapPost("/search", async (SearchApiRequest request, IRagSearchService search, CancellationToken cancellationToken) =>
    Results.Ok(await search.SearchAsync(new LocalRag.Domain.SearchRequest(request.Query, request.SourceIds, request.Limit ?? 12, request.Alpha ?? 0.65), cancellationToken)));
api.MapGet("/chunks/{chunkId}", async (string chunkId, IRagSearchService search, CancellationToken cancellationToken) =>
    await search.GetChunkAsync(chunkId, cancellationToken) is { } chunk ? Results.Ok(chunk) : Results.NotFound());

app.MapMcp("/mcp").RequireAuthorization();
await app.RunAsync();

static void ConfigureLocalOverridePrecedence(ConfigurationManager configuration)
{
    var lateSources = configuration.Sources.Where(source => source is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource || source is Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationSource).ToArray();
    foreach (var source in lateSources) configuration.Sources.Remove(source);
    configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
    foreach (var source in lateSources) configuration.Sources.Add(source);
}

static void ValidateLocalHosting(IConfiguration configuration)
{
    var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
    if (string.IsNullOrWhiteSpace(urls)) return;
    foreach (var rawUrl in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || !IPAddress.TryParse(url.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Local RAG only permits loopback server bindings.");
        }
    }
}
