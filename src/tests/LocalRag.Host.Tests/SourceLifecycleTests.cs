using LocalRag.Api;
using LocalRag.Application;
using LocalRag.Authentication;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Management;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class SourceLifecycleTests
{
    [Fact]
    public async Task PauseFencesAndDrainsActiveWorkWhilePreservingSourceIdentityAndMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var active = await fixture.Gate.AcquireAsync(fixture.Source.SourceId, CancellationToken.None);
        var pausing = fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None);
        await WaitUntilAsync(async () => (await fixture.States.GetAsync(fixture.Source.SourceId, CancellationToken.None)).State == "Pausing");
        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.False(pausing.IsCompleted);
        var fence = await fixture.States.GetAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.False(fence.AllowsWork);
        Assert.False(fence.QueryVisible);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scheduler.RequestAsync(fixture.Source.SourceId, ReconciliationCause.FileHint, CancellationToken.None));
        await active.DisposeAsync();
        Assert.Equal("Paused", (await pausing).State);
        var preserved = await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.Equal(fixture.Source.CanonicalRootPath, preserved!.CanonicalRootPath);
        Assert.Equal(fixture.Source.DisplayName, preserved.DisplayName);
        Assert.Equal(SourceStatus.Paused, preserved.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Source.CanonicalRootPath, "keep.txt")));
        Assert.Equal("Paused", (await fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData("Pausing")]
    [InlineData("Paused")]
    public async Task InterruptedPauseSurvivesNewStoreAndRejectsScheduling(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.States.SetAsync(fixture.Source.SourceId, "Paused", state, CancellationToken.None);
        var restarted = new SourceIndexingStateStore(fixture.Database);
        Assert.False((await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None)).AllowsWork);
        Assert.False((await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None)).QueryVisible);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scheduler.RequestAsync(fixture.Source.SourceId, ReconciliationCause.Startup, CancellationToken.None));
        Assert.Equal("Paused", (await fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ResumeIsIdempotentAndHiddenAcrossRestartUntilConsistentCompletionAndPauseWinsStaleCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None);
        File.Delete(Path.Combine(fixture.Source.CanonicalRootPath, "keep.txt"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Source.CanonicalRootPath, "new.txt"), "changes accumulated while paused");
        Assert.Equal("Resuming", (await fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None)).State);
        Assert.Equal(1, fixture.Coordinator.Reindexes);
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None);
        Assert.Equal(1, fixture.Coordinator.Reindexes);
        var restarted = new SourceIndexingStateStore(fixture.Database);
        Assert.False((await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None)).QueryVisible);
        await restarted.CompleteResumeAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.True((await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None)).QueryVisible);
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None);
        await restarted.CompleteResumeAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.Equal("Paused", (await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None)).State);
    }

    [Fact]
    public async Task MaintenanceAndRemovalTakePrecedenceAndFailedResumeCanBeRetried()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None);
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None);
        await fixture.Sources.SetStatusAsync(fixture.Source.SourceId, SourceStatus.Degraded, "A dependency is unavailable.", CancellationToken.None);
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None);
        Assert.Equal(2, fixture.Coordinator.Reindexes);
        await fixture.Reconciliations.TombstoneAsync(fixture.Source.SourceId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None));
        fixture.Maintenance.MarkFailed();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None));
    }

    [Fact]
    public async Task ResumeQueueFailureRemainsHiddenWithRecoverableErrorAndExplicitRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.SetPausedAsync(fixture.Source.SourceId, true, CancellationToken.None);
        fixture.Coordinator.FailQueue = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None));
        Assert.False((await fixture.States.GetAsync(fixture.Source.SourceId, CancellationToken.None)).QueryVisible);
        var source = await fixture.Sources.GetAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.Equal(SourceStatus.Degraded, source!.Status);
        Assert.Contains("retry Resume", source.LastError!, StringComparison.Ordinal);
        fixture.Coordinator.FailQueue = false;
        Assert.Equal("Resuming", (await fixture.Service.SetPausedAsync(fixture.Source.SourceId, false, CancellationToken.None)).State);
        Assert.Equal(1, fixture.Coordinator.Reindexes);
    }

    [Fact]
    public async Task ResetAndRemovalDiscardPauseStateWithoutBreakingSubsequentRequests()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.States.SetAsync(fixture.Source.SourceId, "Paused", "Paused", CancellationToken.None);
        await fixture.States.RemoveAsync(fixture.Source.SourceId, CancellationToken.None);
        Assert.Equal("Running", (await fixture.States.GetAsync(fixture.Source.SourceId, CancellationToken.None)).State);
        await fixture.States.SetAsync(fixture.Source.SourceId, "Paused", "Paused", CancellationToken.None);
        await fixture.Database.ResetAsync(CancellationToken.None);
        Assert.Equal("Running", (await fixture.States.GetAsync(fixture.Source.SourceId, CancellationToken.None)).State);
    }

    [Fact]
    public void BrowserTicketsAreUnpredictableOneUseAndSameOriginChecksRejectCrossOriginAndMissingOrigin()
    {
        var clock = new ManualTimeProvider();
        var tickets = new BrowserLaunchTicketStore(clock);
        var first = tickets.Issue(true); var second = tickets.Issue(false);
        Assert.NotEqual(first, second); Assert.Equal(64, first.Length);
        Assert.True(tickets.Consume(first, out var management)); Assert.True(management);
        Assert.False(tickets.Consume(first, out _)); Assert.False(tickets.Consume("invented", out _));
        clock.Advance(TimeSpan.FromSeconds(61)); Assert.False(tickets.Consume(second, out _));
        var context = new DefaultHttpContext(); context.Request.Scheme = "http"; context.Request.Host = new("127.0.0.1:5198");
        Assert.False(ManagerEndpoints.SameOrigin(context.Request));
        context.Request.Headers.Origin = "http://evil.example"; Assert.False(ManagerEndpoints.SameOrigin(context.Request));
        context.Request.Headers.Origin = "http://127.0.0.1:5198"; Assert.True(ManagerEndpoints.SameOrigin(context.Request));
        context.Request.Headers.Origin = "http://127.0.0.1:5199"; Assert.False(ManagerEndpoints.SameOrigin(context.Request));
    }

    [Fact]
    public void HostFolderPickerSupportsAbsoluteUnicodeSpacesTrailingSeparatorAndLazyDirectChildren()
    {
        var root = ManagerHostIntegrationTests.NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Unicode 日本語", "nested"));
            var listing = HostFolderPicker.List(root + Path.DirectorySeparatorChar);
            Assert.Equal(root, listing.Current);
            Assert.Single(listing.Directories);
            Assert.DoesNotContain(listing.Directories, x => x.EndsWith("nested", StringComparison.Ordinal));
            Assert.Throws<ArgumentException>(() => HostFolderPicker.List("relative/path"));
            Assert.Throws<DirectoryNotFoundException>(() => HostFolderPicker.List(Path.Combine(root, "missing")));
            Assert.Null(HostFolderPicker.List(null).Current);
        }
        finally { ManagerHostIntegrationTests.RecordManifest(root); }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class FakeCoordinator : IIndexCoordinator
    {
        public int Reindexes { get; private set; }
        public bool FailQueue { get; set; }
        public Task QueueInitialIndexAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task ReindexAsync(string id, CancellationToken ct)
        {
            if (FailQueue) throw new IOException("Queue is unavailable.");
            Reindexes++; return Task.CompletedTask;
        }
        public Task RemoveSourceAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = ManagerHostIntegrationTests.NewRoot();
        public SqliteDatabase Database { get; private set; } = null!;
        public SqliteSourceRegistry Sources { get; private set; } = null!;
        public SourceRecord Source { get; private set; } = null!;
        public SourceIndexingStateStore States { get; private set; } = null!;
        public SourceOperationGate Gate { get; } = new();
        public HostMaintenanceCoordinator Maintenance { get; } = new();
        public FakeCoordinator Coordinator { get; } = new();
        public SourceWatcherRegistry Watchers { get; private set; } = null!;
        public SourceLifecycleService Service { get; private set; } = null!;
        public ReconciliationScheduler Scheduler { get; private set; } = null!;
        public SqliteReconciliationStore Reconciliations { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var options = Options.Create(new LocalRagOptions { DataDirectory = Path.Combine(fixture.Root, "data") });
            fixture.Database = new(options); fixture.Sources = new(fixture.Database, options); fixture.States = new(fixture.Database);
            await fixture.Sources.InitializeAsync(CancellationToken.None);
            var source = Path.Combine(fixture.Root, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "original data");
            fixture.Source = await fixture.Sources.RegisterAsync(source, "Keep identity", CancellationToken.None);
            fixture.Reconciliations = new(fixture.Database); await fixture.Reconciliations.InitializeAsync(CancellationToken.None);
            fixture.Scheduler = new(fixture.Reconciliations, new(), new OperationalMetrics(), indexingStates: fixture.States);
            fixture.Watchers = new(fixture.Scheduler, options, NullLogger<SourceWatcherRegistry>.Instance);
            fixture.Service = new(fixture.Sources, fixture.States, fixture.Gate, fixture.Watchers, fixture.Coordinator, fixture.Maintenance);
            return fixture;
        }
        public ValueTask DisposeAsync()
        {
            Watchers.Dispose(); ManagerHostIntegrationTests.RecordManifest(Root);
            return ValueTask.CompletedTask;
        }
    }
}
