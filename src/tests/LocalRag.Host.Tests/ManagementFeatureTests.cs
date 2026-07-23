using LocalRag.Api;
using LocalRag.Application;
using LocalRag.Authentication;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using System.Text.Encodings.Web;
using System.Net;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ManagementFeatureTests
{
    [Fact]
    public void ManagementToolsAreSeparateAndResultsDoNotExposeSensitiveTargets()
    {
        var standard = ToolNames<RagMcpTools>();
        var management = ToolNames<RagManagementMcpTools>();

        Assert.True(standard.SetEquals(["rag_search", "rag_get_chunk", "rag_list_sources", "rag_get_source_status"]));
        Assert.True(management.SetEquals(["rag_index", "rag_remove_index", "rag_reset"]));
        Assert.Empty(standard.Intersect(management));

        var resultProperties = typeof(ManagementResult).GetProperties().Select(property => property.Name).ToHashSet();
        Assert.DoesNotContain("RootPath", resultProperties);
        Assert.DoesNotContain("CanonicalRootPath", resultProperties);
        Assert.DoesNotContain("Collection", resultProperties);
        Assert.DoesNotContain("RawError", resultProperties);
    }

    [Theory]
    [InlineData(true, "management-token", true)]
    [InlineData(true, "standard-token", false)]
    [InlineData(false, "management-token", false)]
    public async Task ManagementAuthenticationRejectsStandardTokenAndDisabledConfiguration(
        bool enabled,
        string suppliedToken,
        bool expectedSuccess)
    {
        var localOptions = Microsoft.Extensions.Options.Options.Create(new LocalRagOptions
        {
            Authentication = new LocalRag.Configuration.AuthenticationOptions { Token = "standard-token" },
            Management = new ManagementOptions { Enabled = enabled, Token = "management-token" }
        });
        var handler = new ManagementTokenAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            localOptions);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + suppliedToken;
        await handler.InitializeAsync(
            new AuthenticationScheme(
                ManagementTokenAuthenticationHandler.SchemeName,
                null,
                typeof(ManagementTokenAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        Assert.Equal(expectedSuccess, result.Succeeded);
    }

    [Fact]
    public void ConfirmationIsBoundToActionTargetAndPrincipalAndIsOneUse()
    {
        using var fixture = new ManagementFixture();
        var store = new ManagementConfirmationStore(fixture.Options);
        var challenge = store.Create("remove", "target-a", "principal-a");

        Assert.False(store.Consume(challenge.Token, "remove", "target-b", "principal-a"));
        Assert.False(store.Consume(challenge.Token, "remove", "target-a", "principal-a"));

        challenge = store.Create("reset", "installation", "principal-a");
        Assert.False(store.Consume(challenge.Token, "reset", "installation", "principal-b"));

        challenge = store.Create("reset", "installation", "principal-a");
        Assert.True(store.Consume(challenge.Token, "reset", "installation", "principal-a"));
        Assert.False(store.Consume(challenge.Token, "reset", "installation", "principal-a"));
    }

    [Fact]
    public async Task MaintenanceCancelsAndDrainsOperationsAndBlocksConcurrentLeases()
    {
        var maintenance = new HostMaintenanceCoordinator();
        var operation = maintenance.TryAcquireOperational(CancellationToken.None);
        Assert.NotNull(operation);

        var acquire = maintenance.TryAcquireMaintenanceAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(TimeSpan.FromSeconds(10), operation!.CancellationToken));
        Assert.Null(maintenance.TryAcquireOperational(CancellationToken.None));
        Assert.False(acquire.IsCompleted);

        await operation.DisposeAsync();
        var exclusive = await acquire;
        Assert.NotNull(exclusive);
        Assert.False(maintenance.IsReady);
        exclusive!.Complete();
        await exclusive.DisposeAsync();
        Assert.True(maintenance.IsReady);
    }

    [Fact]
    public async Task IncompleteMaintenanceFailsReadinessAndBlocksOperations()
    {
        var maintenance = new HostMaintenanceCoordinator();
        var exclusive = await maintenance.TryAcquireMaintenanceAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(exclusive);
        await exclusive!.DisposeAsync();

        Assert.False(maintenance.IsReady);
        Assert.Null(maintenance.TryAcquireOperational(CancellationToken.None));
    }

    [Fact]
    public async Task SqliteResetDeletesOnlyDatabaseAndSidecars()
    {
        using var fixture = new ManagementFixture();
        await fixture.InitializeAsync();
        SqliteConnection.ClearAllPools();
        var unrelated = Path.Combine(Path.GetDirectoryName(fixture.Database.DatabasePath)!, "preserve.txt");
        await File.WriteAllTextAsync(unrelated, "preserve");
        await File.WriteAllTextAsync(fixture.Database.DatabasePath + "-wal", "wal");
        await File.WriteAllTextAsync(fixture.Database.DatabasePath + "-shm", "shm");

        await fixture.Database.ResetAsync(CancellationToken.None);

        Assert.False(File.Exists(fixture.Database.DatabasePath));
        Assert.False(File.Exists(fixture.Database.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.Database.DatabasePath + "-shm"));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task IndexAndConfirmedRemoveDelegateAndPreserveSourceFiles()
    {
        using var fixture = new ManagementFixture();
        await fixture.InitializeAsync();
        var file = Path.Combine(fixture.SourceDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(file, "preserve me");

        var indexed = await fixture.Service.IndexAsync(
            fixture.SourceDirectory,
            "fixture",
            "principal",
            CancellationToken.None);
        Assert.Equal(ManagementOperationState.Accepted, indexed.State);
        Assert.Equal(1, fixture.Coordinator.InitialQueueCount);

        var duplicate = await fixture.Service.IndexAsync(
            Path.Combine(fixture.SourceDirectory, "."),
            null,
            "principal",
            CancellationToken.None);
        Assert.Equal("AlreadyRegistered", duplicate.ErrorCode);
        Assert.Single(await fixture.Sources.ListAsync(CancellationToken.None));

        var prepared = await fixture.Service.RemoveAsync(
            fixture.SourceDirectory,
            null,
            "principal",
            CancellationToken.None);
        Assert.Equal(ManagementOperationState.ConfirmationRequired, prepared.State);
        Assert.True(File.Exists(file));

        var removed = await fixture.Service.RemoveAsync(
            fixture.SourceDirectory,
            prepared.ConfirmationToken,
            "principal",
            CancellationToken.None);
        Assert.Equal(ManagementOperationState.Completed, removed.State);
        Assert.Equal(1, fixture.Coordinator.RemoveCount);
        Assert.Empty(await fixture.Sources.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(file));

        var replay = await fixture.Service.RemoveAsync(
            fixture.SourceDirectory,
            prepared.ConfirmationToken,
            "principal",
            CancellationToken.None);
        Assert.Equal("SourceNotFound", replay.ErrorCode);
    }

    [Fact]
    public async Task ConfirmedResetRecreatesEmptyStateAndPreservesSourceFiles()
    {
        using var fixture = new ManagementFixture();
        await fixture.InitializeAsync();
        var file = Path.Combine(fixture.SourceDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(file, "preserve me");
        await fixture.Sources.RegisterAsync(fixture.SourceDirectory, "fixture", CancellationToken.None);

        var prepared = await fixture.Service.ResetAsync(null, "principal", CancellationToken.None);
        var reset = await fixture.Service.ResetAsync(prepared.ConfirmationToken, "principal", CancellationToken.None);

        Assert.Equal(ManagementOperationState.Completed, reset.State);
        Assert.Equal(1, fixture.Vectors.ResetCount);
        Assert.Empty(await fixture.Sources.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(fixture.Database.DatabasePath));
        Assert.False(fixture.ResetState.HasIncompleteReset);
        Assert.True(fixture.Maintenance.IsReady);
    }

    [Fact]
    public async Task OwnershipRefusalOccursBeforeSqliteReset()
    {
        using var fixture = new ManagementFixture();
        await fixture.InitializeAsync();
        await fixture.Sources.RegisterAsync(fixture.SourceDirectory, "fixture", CancellationToken.None);
        fixture.Vectors.Owned = false;

        var prepared = await fixture.Service.ResetAsync(null, "principal", CancellationToken.None);
        var reset = await fixture.Service.ResetAsync(prepared.ConfirmationToken, "principal", CancellationToken.None);

        Assert.Equal("OwnershipNotVerified", reset.ErrorCode);
        Assert.Single(await fixture.Sources.ListAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Vectors.ResetCount);
    }

    [Fact]
    public async Task PartialResetStaysUnreadyUntilExplicitConfirmedRetrySucceeds()
    {
        using var fixture = new ManagementFixture();
        await fixture.InitializeAsync();
        fixture.Vectors.FailNextReset = true;

        var prepared = await fixture.Service.ResetAsync(null, "principal", CancellationToken.None);
        var failed = await fixture.Service.ResetAsync(prepared.ConfirmationToken, "principal", CancellationToken.None);

        Assert.Equal("ResetFailed", failed.ErrorCode);
        Assert.False(fixture.Vectors.CollectionExists);
        Assert.True(fixture.ResetState.HasIncompleteReset);
        Assert.False(fixture.Maintenance.IsReady);

        prepared = await fixture.Service.ResetAsync(null, "principal", CancellationToken.None);
        var recovered = await fixture.Service.ResetAsync(prepared.ConfirmationToken, "principal", CancellationToken.None);
        Assert.Equal(ManagementOperationState.Completed, recovered.State);
        Assert.Equal(1, fixture.Vectors.RecoveryCount);
        Assert.False(fixture.ResetState.HasIncompleteReset);
        Assert.True(fixture.Maintenance.IsReady);
    }

    [Fact]
    public async Task WatcherCannotScheduleWhileMaintenanceIsExclusive()
    {
        using var fixture = new ManagementFixture();
        await fixture.InitializeAsync();
        var source = await fixture.Sources.RegisterAsync(fixture.SourceDirectory, "fixture", CancellationToken.None);
        var scheduler = new ReconciliationScheduler(
            fixture.Reconciliations,
            new IndexWorkChannel(),
            new OperationalMetrics(),
            maintenance: fixture.Maintenance);
        using var watcher = new SourceWatcherRegistry(
            scheduler,
            fixture.Options,
            NullLogger<SourceWatcherRegistry>.Instance,
            fixture.Maintenance);
        var exclusive = await fixture.Maintenance.TryAcquireMaintenanceAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.NotNull(exclusive);

        await watcher.NotifyErrorAsync(source.SourceId, new InternalBufferOverflowException(), CancellationToken.None);

        Assert.Null(await fixture.Reconciliations.GetAsync(source.SourceId, CancellationToken.None));
        exclusive!.Complete();
        await exclusive.DisposeAsync();
    }

    [Fact]
    public void LocalHostingPolicyRejectsRemoteBindingsAndConnections()
    {
        var remoteBinding = new ConfigurationManager();
        remoteBinding["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5000";
        Assert.Throws<InvalidOperationException>(() => LocalHostingPolicy.ValidateConfiguredBindings(remoteBinding));

        var loopbackBinding = new ConfigurationManager();
        loopbackBinding["Kestrel:Endpoints:Http:Url"] = "http://localhost:5000";
        LocalHostingPolicy.ValidateConfiguredBindings(loopbackBinding);

        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 5000);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.False(LocalHostingPolicy.IsLoopbackRequest(context));
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(LocalHostingPolicy.IsLoopbackRequest(context));
    }

    private static HashSet<string> ToolNames<T>() => typeof(T).GetMethods()
        .Select(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), false)
            .OfType<McpServerToolAttribute>().SingleOrDefault()?.Name)
        .Where(name => name is not null)
        .Select(name => name!)
        .ToHashSet(StringComparer.Ordinal);

    private sealed class ManagementFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "localrag-management-tests", Guid.NewGuid().ToString("N"));

        public ManagementFixture()
        {
            SourceDirectory = Path.Combine(_root, "source");
            Directory.CreateDirectory(SourceDirectory);
            Options = Microsoft.Extensions.Options.Options.Create(new LocalRagOptions
            {
                DataDirectory = Path.Combine(_root, "data"),
                Authentication = new LocalRag.Configuration.AuthenticationOptions { Token = "standard-token" },
                Management = new ManagementOptions
                {
                    Enabled = true,
                    Token = "management-token",
                    ConfirmationLifetimeSeconds = 120
                },
                Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
            });
            Database = new SqliteDatabase(Options);
            Sources = new SqliteSourceRegistry(Database, Options);
            var indexState = new SqliteIndexStateStore(Database, Options);
            var chunkProfiles = new SqliteChunkProfileStateStore(Database);
            var jobs = new IndexJobStore(Database);
            var reconciliations = new SqliteReconciliationStore(Database);
            Reconciliations = reconciliations;
            Coordinator = new FakeCoordinator(Sources);
            Vectors = new FakeManagementVectorStore();
            Maintenance = new HostMaintenanceCoordinator();
            ResetState = new ResetStateStore(Options);
            Initializers = [Sources, indexState, chunkProfiles, jobs, reconciliations];
            Service = new LocalRagManagementService(
                Sources,
                Coordinator,
                new ManagementConfirmationStore(Options),
                new InstallationOwnershipStore(Options),
                ResetState,
                Maintenance,
                Vectors,
                Database,
                indexState,
                chunkProfiles,
                jobs,
                reconciliations,
                Options);
        }

        public IOptions<LocalRagOptions> Options { get; }
        public string SourceDirectory { get; }
        public SqliteDatabase Database { get; }
        public SqliteSourceRegistry Sources { get; }
        public FakeCoordinator Coordinator { get; }
        public FakeManagementVectorStore Vectors { get; }
        public HostMaintenanceCoordinator Maintenance { get; }
        public SqliteReconciliationStore Reconciliations { get; }
        public ResetStateStore ResetState { get; }
        public LocalRagManagementService Service { get; }
        private object[] Initializers { get; }

        public async Task InitializeAsync()
        {
            await ((ISourceRegistry)Initializers[0]).InitializeAsync(CancellationToken.None);
            await ((IIndexStateStore)Initializers[1]).InitializeAsync(CancellationToken.None);
            await ((IChunkProfileStateStore)Initializers[2]).InitializeAsync(CancellationToken.None);
            await ((IndexJobStore)Initializers[3]).InitializeAsync(CancellationToken.None);
            await ((IReconciliationStore)Initializers[4]).InitializeAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeCoordinator(ISourceRegistry sources) : IIndexCoordinator
    {
        public int InitialQueueCount { get; private set; }
        public int RemoveCount { get; private set; }

        public Task QueueInitialIndexAsync(string sourceId, CancellationToken cancellationToken)
        {
            InitialQueueCount++;
            return Task.CompletedTask;
        }

        public Task ReindexAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken)
        {
            RemoveCount++;
            await sources.RemoveAsync(sourceId, cancellationToken);
        }
    }

    private sealed class FakeManagementVectorStore : IManagementVectorStore
    {
        public bool Owned { get; set; } = true;
        public bool FailNextReset { get; set; }
        public bool CollectionExists { get; private set; } = true;
        public int ResetCount { get; private set; }
        public int RecoveryCount { get; private set; }

        public Task<bool> VerifyOwnershipAsync(string ownershipId, CancellationToken cancellationToken) =>
            Task.FromResult(Owned && CollectionExists);

        public Task ResetOwnedCollectionAsync(string ownershipId, CancellationToken cancellationToken)
        {
            if (FailNextReset)
            {
                FailNextReset = false;
                CollectionExists = false;
                throw new InvalidOperationException("raw fake dependency failure");
            }
            ResetCount++;
            return Task.CompletedTask;
        }

        public Task RecoverOwnedCollectionAsync(string ownershipId, CancellationToken cancellationToken)
        {
            RecoveryCount++;
            CollectionExists = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
