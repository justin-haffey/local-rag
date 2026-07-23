using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class ChunkProfileTransitionTests
{
    [Fact]
    public async Task TransitionRemainsInvisibleAcrossRestartFailureRetryAndRollback()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var initial = await fixture.Profiles.GetOrCreateAsync(
                fixture.Source.SourceId, "candidate-a", hasIndexedChunks: false, CancellationToken.None);
            Assert.Equal(ChunkProfileStatus.Ready, initial.Status);
            Assert.True(await fixture.Profiles.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));

            await fixture.Profiles.BeginTransitionAsync(fixture.Source.SourceId, "candidate-b", CancellationToken.None);
            Assert.False(await fixture.Profiles.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));

            var restarted = new SqliteChunkProfileStateStore(fixture.Database, fixture.Gate);
            await restarted.InitializeAsync(CancellationToken.None);
            var recovered = await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None);
            Assert.Equal(ChunkProfileStatus.Reindexing, recovered?.Status);
            Assert.Equal("candidate-a", recovered?.ActiveFingerprint);
            Assert.Equal("candidate-b", recovered?.PendingFingerprint);
            Assert.False(await restarted.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));

            await restarted.FailTransitionAsync(
                fixture.Source.SourceId, "candidate-b", "synthetic failure", CancellationToken.None);
            var failed = await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None);
            Assert.Equal(ChunkProfileStatus.Failed, failed?.Status);
            Assert.Equal("synthetic failure", failed?.LastError);
            Assert.False(await restarted.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));

            await restarted.BeginTransitionAsync(fixture.Source.SourceId, "candidate-b", CancellationToken.None);
            await restarted.CompleteTransitionAsync(fixture.Source.SourceId, "candidate-b", CancellationToken.None);
            Assert.True(await restarted.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));
            Assert.Equal("candidate-b", (await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None))?.ActiveFingerprint);

            await restarted.BeginTransitionAsync(fixture.Source.SourceId, "legacy-generic-1", CancellationToken.None);
            Assert.False(await restarted.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));
            await restarted.CompleteTransitionAsync(fixture.Source.SourceId, "legacy-generic-1", CancellationToken.None);
            var rolledBack = await restarted.GetAsync(fixture.Source.SourceId, CancellationToken.None);
            Assert.Equal(ChunkProfileStatus.Ready, rolledBack?.Status);
            Assert.Equal("legacy-generic-1", rolledBack?.ActiveFingerprint);
            Assert.Null(rolledBack?.PendingFingerprint);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ExistingUnversionedSourceStartsOnLegacyProfile()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var state = await fixture.Profiles.GetOrCreateAsync(
                fixture.Source.SourceId, "candidate", hasIndexedChunks: true, CancellationToken.None);

            Assert.Equal("legacy-generic-1", state.ActiveFingerprint);
            Assert.Equal(ChunkProfileStatus.Ready, state.Status);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ForcedProfileJobSurvivesProcessingRecovery()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var jobs = new IndexJobStore(fixture.Database);
            await jobs.InitializeAsync(CancellationToken.None);
            await jobs.QueueAsync(fixture.Source.SourceId, "candidate", forceContentProcessing: true, CancellationToken.None);

            var firstLease = await jobs.LeaseAsync(fixture.Source.SourceId, CancellationToken.None);
            Assert.NotNull(firstLease);
            Assert.Equal("candidate", firstLease.TargetChunkProfileFingerprint);
            Assert.True(firstLease.ForceContentProcessing);

            var restarted = new IndexJobStore(fixture.Database);
            await restarted.InitializeAsync(CancellationToken.None);
            Assert.Contains(fixture.Source.SourceId, await restarted.RecoverAsync(CancellationToken.None));
            var recovered = await restarted.LeaseAsync(fixture.Source.SourceId, CancellationToken.None);

            Assert.NotNull(recovered);
            Assert.Equal(firstLease.JobId, recovered.JobId);
            Assert.Equal("candidate", recovered.TargetChunkProfileFingerprint);
            Assert.True(recovered.ForceContentProcessing);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task TransitionWaitsForAnInFlightSourceQueryLease()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await fixture.Profiles.GetOrCreateAsync(
                fixture.Source.SourceId, "candidate-a", hasIndexedChunks: false, CancellationToken.None);
            var queryLease = await fixture.Gate.AcquireAsync([fixture.Source.SourceId], CancellationToken.None);

            var transition = fixture.Profiles.BeginTransitionAsync(
                fixture.Source.SourceId, "candidate-b", CancellationToken.None);
            await Task.Delay(50);
            Assert.False(transition.IsCompleted);

            await queryLease.DisposeAsync();
            await transition;
            Assert.False(await fixture.Profiles.IsQueryVisibleAsync(fixture.Source.SourceId, CancellationToken.None));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ForcedTransitionQueuedDuringProcessingPersistsAsSequentialSuccessor()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var jobs = new IndexJobStore(fixture.Database);
            await jobs.InitializeAsync(CancellationToken.None);
            await jobs.QueueAsync(fixture.Source.SourceId, CancellationToken.None);
            var active = Assert.IsType<IndexJob>(await jobs.LeaseAsync(fixture.Source.SourceId, CancellationToken.None));

            await jobs.QueueAsync(
                fixture.Source.SourceId, "candidate-next", forceContentProcessing: true, CancellationToken.None);

            Assert.Null(await jobs.LeaseAsync(fixture.Source.SourceId, CancellationToken.None));
            await jobs.CompleteAsync(active, CancellationToken.None);
            var successor = Assert.IsType<IndexJob>(await jobs.LeaseAsync(fixture.Source.SourceId, CancellationToken.None));
            Assert.NotEqual(active.JobId, successor.JobId);
            Assert.Equal("candidate-next", successor.TargetChunkProfileFingerprint);
            Assert.True(successor.ForceContentProcessing);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"localrag-profile-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var options = Options.Create(new LocalRagOptions { DataDirectory = Path.Combine(root, "data") });
        var database = new SqliteDatabase(options);
        var registry = new SqliteSourceRegistry(database, options);
        await registry.InitializeAsync(CancellationToken.None);
        var source = await registry.RegisterAsync(sourceRoot, "profile fixture", CancellationToken.None);
        await new SqliteIndexStateStore(database, options).InitializeAsync(CancellationToken.None);
        var gate = new ChunkProfileOperationGate();
        var profiles = new SqliteChunkProfileStateStore(database, gate);
        await profiles.InitializeAsync(CancellationToken.None);
        return new Fixture(root, database, profiles, gate, source);
    }

    private sealed record Fixture(
        string Root,
        SqliteDatabase Database,
        SqliteChunkProfileStateStore Profiles,
        ChunkProfileOperationGate Gate,
        SourceRecord Source) : IDisposable
    {
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
