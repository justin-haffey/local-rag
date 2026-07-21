using System.IO.Compression;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using LocalRag.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class SourceWatcherIndexingTests
{
    [Theory]
    [InlineData(".docx", "Watcher DOCX sentinel")]
    [InlineData(".pdf", "decree")]
    public async Task WatcherIndexesCreatedDocumentAndRemovesDeletedDocumentFromStateAndVectors(string extension, string sentinel)
    {
        var root = CreateTemporaryDirectory("source");
        var data = CreateTemporaryDirectory("data");
        var options = Options.Create(new LocalRagOptions
        {
            DataDirectory = data,
            Indexing = new IndexingOptions
            {
                DebounceMilliseconds = 50,
                StabilityIntervalMilliseconds = 0
            },
            Chunking = new ChunkingOptions
            {
                TargetTokens = 64,
                MaximumTokens = 128,
                OverlapTokens = 8
            },
            Embedding = new EmbeddingOptions { ProfileId = "test-profile" }
        });
        var database = new SqliteDatabase(options);
        var sources = new SqliteSourceRegistry(database, options);
        var indexState = new SqliteIndexStateStore(database, options);
        var jobs = new IndexJobStore(database);
        var chunkProfiles = new SqliteChunkProfileStateStore(database);
        var queue = new IndexWorkChannel();
        using var watchers = new SourceWatcherRegistry(queue, jobs, sources, options, NullLogger<SourceWatcherRegistry>.Instance);
        var vectors = new RecordingVectorStore();
        var extraction = new ContentExtractionService([
            new PlainTextContentExtractor(),
            new WordDocumentContentExtractor(options),
            new PdfContentExtractor(options, new TesseractPdfOcrService(options))
        ]);
        var fileIndexing = new FileIndexingService(
            indexState,
            new FakeEmbeddingService(),
            new GenericChunker(options),
            vectors,
            extraction,
            options,
            new OperationalMetrics());
        var coordinator = new IndexCoordinator(
            sources,
            indexState,
            vectors,
            fileIndexing,
            new FilePolicy(options, extraction),
            watchers,
            queue,
            jobs,
            new FixedChunkProfileProvider(),
            chunkProfiles,
            NullLogger<IndexCoordinator>.Instance);

        try
        {
            await sources.InitializeAsync(CancellationToken.None);
            await indexState.InitializeAsync(CancellationToken.None);
            await chunkProfiles.InitializeAsync(CancellationToken.None);
            await jobs.InitializeAsync(CancellationToken.None);
            var source = await sources.RegisterAsync(root, "watcher fixture", CancellationToken.None);
            watchers.Track(source);

            var relativePath = "watched" + extension;
            var path = Path.Combine(root, relativePath);
            if (extension == ".docx") CreateDocx(path);
            else File.Copy(Path.Combine(AppContext.BaseDirectory, "Final Decree [615749].pdf"), path);
            var createJob = await WaitForJobAsync(queue, jobs);
            Assert.Equal(source.SourceId, createJob.SourceId);
            Assert.True(await coordinator.ProcessAsync(source.SourceId, CancellationToken.None));
            await jobs.CompleteAsync(createJob, CancellationToken.None);

            var indexedFile = await indexState.GetFileAsync(source.SourceId, relativePath, CancellationToken.None);
            Assert.NotNull(indexedFile);
            var indexedChunks = await indexState.GetChunksForFileAsync(indexedFile.FileId, CancellationToken.None);
            Assert.NotEmpty(indexedChunks);
            Assert.Contains(indexedChunks, chunk => chunk.Content.Contains(sentinel, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(vectors.Upserted);

            await Task.Delay(100);
            File.Delete(path);
            var deleteJob = await WaitForJobAsync(queue, jobs);
            Assert.Equal(source.SourceId, deleteJob.SourceId);
            Assert.True(await coordinator.ProcessAsync(source.SourceId, CancellationToken.None));
            await jobs.CompleteAsync(deleteJob, CancellationToken.None);

            Assert.Null(await indexState.GetFileAsync(source.SourceId, relativePath, CancellationToken.None));
            Assert.DoesNotContain(await indexState.GetChunksForSourceAsync(source.SourceId, CancellationToken.None), chunk => chunk.RelativePath == relativePath);
            Assert.Equal(indexedChunks.Select(chunk => chunk.ChunkId).Order(), vectors.Deleted.Order());
        }
        finally
        {
            watchers.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteDirectory(root);
            DeleteDirectory(data);
        }
    }

    private sealed class FixedChunkProfileProvider : IChunkProfileProvider
    {
        public string ChunkerIdentity => "test/1";
        public string Fingerprint => "test-structural-profile";
    }

    private static async Task<IndexJob> WaitForJobAsync(IndexWorkChannel queue, IndexJobStore jobs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        var job = await jobs.LeaseAsync(reader.Current, timeout.Token);
        return Assert.IsType<IndexJob>(job);
    }

    private static string CreateTemporaryDirectory(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-rag-watcher-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateDocx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        WriteEntry(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>Watcher DOCX sentinel</w:t></w:r></w:p></w:body>
            </w:document>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public string ProfileId => "test-profile";
        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([0.5f, 0.5f]);
        public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([0.5f, 0.5f]);
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public List<VectorDocument> Upserted { get; } = [];
        public List<string> Deleted { get; } = [];
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
        {
            Upserted.AddRange(documents);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
        {
            Deleted.AddRange(chunkIds);
            return Task.CompletedTask;
        }
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }
}
