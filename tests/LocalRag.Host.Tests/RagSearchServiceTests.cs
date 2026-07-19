using LocalRag.Application;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class RagSearchServiceTests
{
    [Fact]
    public async Task SearchRejectsUnknownSourceIdBeforeQueryingStore()
    {
        var vectors = new FakeVectorStore();
        var service = new RagSearchService(new FakeEmbeddings(), vectors, new FakeIndexState(), new FakeSources(), new OperationalMetrics());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SearchAsync(new SearchRequest("retry", ["unknown"]), CancellationToken.None));
        Assert.False(vectors.WasSearched);
    }

    private sealed class FakeEmbeddings : IEmbeddingService
    {
        public string ProfileId => "test";
        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([0.5f, 0.5f]);
        public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([0.5f, 0.5f]);
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public bool WasSearched { get; private set; }
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken)
        {
            WasSearched = true;
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        }
    }

    private sealed class FakeSources : ISourceRegistry
    {
        private static readonly SourceRecord Source = new("source", "C:\\fixture", "fixture", SourceStatus.Ready, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "test");
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SourceRecord> RegisterAsync(string rootPath, string? displayName, CancellationToken cancellationToken) => Task.FromResult(Source);
        public Task<IReadOnlyList<SourceRecord>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceRecord>>([Source]);
        public Task<SourceRecord?> GetAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult<SourceRecord?>(sourceId == Source.SourceId ? Source : null);
        public Task RemoveAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetStatusAsync(string sourceId, SourceStatus status, string? failureMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeIndexState : IIndexStateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.FromResult<IndexedFile?>(null);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ChunkRecord>>([]);
        public Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) => Task.FromResult<ChunkRecord?>(null);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ChunkRecord>>([]);
    }
}
