using System.IO.Compression;
using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class WordDocumentIndexingTests
{
    [Fact]
    public async Task DocxExtractionIncludesDocumentPartsAndExcludesDeletedText()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "evidence.docx");
            CreateDocx(path);
            var extraction = CreateExtractionService(DefaultOptions());

            var content = await extraction.ExtractAsync(path, CancellationToken.None);

            Assert.Contains("Word indexing sentinel", content, StringComparison.Ordinal);
            Assert.Contains("Table cell evidence", content, StringComparison.Ordinal);
            Assert.Contains("Inserted revision", content, StringComparison.Ordinal);
            Assert.Contains("Header evidence", content, StringComparison.Ordinal);
            Assert.Contains("Footer evidence", content, StringComparison.Ordinal);
            Assert.Contains("Footnote evidence", content, StringComparison.Ordinal);
            Assert.Contains("Reviewer comment", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Deleted revision", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PlainTextExtractionPreservesExistingBehaviorAndNormalizesLineEndings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "sample.cs");
            await File.WriteAllTextAsync(path, "one\r\ntwo\rthree");
            var extraction = CreateExtractionService(DefaultOptions());

            var content = await extraction.ExtractAsync(path, CancellationToken.None);

            Assert.Equal("one\ntwo\nthree", content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FilePolicyAllowsDocxAndRejectsLegacyDoc()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var docxPath = Path.Combine(root, "supported.docx");
            var docPath = Path.Combine(root, "legacy.doc");
            File.WriteAllBytes(docxPath, [1]);
            File.WriteAllBytes(docPath, [1]);
            var options = DefaultOptions();
            var policy = new FilePolicy(options, CreateExtractionService(options));

            Assert.True(policy.IsEligible(root, docxPath, new FileInfo(docxPath)));
            Assert.False(policy.IsEligible(root, docPath, new FileInfo(docPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileIndexingServicePersistsAndEmbedsExtractedDocxChunks()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "indexed.docx");
            CreateDocx(path);
            var options = DefaultOptions();
            var state = new RecordingIndexState();
            var vectors = new RecordingVectorStore();
            var service = new FileIndexingService(
                state,
                new FakeEmbeddingService(),
                new GenericChunker(options),
                vectors,
                CreateExtractionService(options),
                options,
                new OperationalMetrics());
            var source = new SourceRecord("source", root, "fixture", SourceStatus.Ready, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "test-profile");

            await service.IndexAsync(source, path, "indexed.docx", new FileInfo(path), CancellationToken.None);

            Assert.NotNull(state.SavedFile);
            Assert.Equal("indexed.docx", state.SavedFile.RelativePath);
            Assert.NotEmpty(state.SavedChunks);
            Assert.All(state.SavedChunks, chunk => Assert.Equal("word", chunk.Language));
            Assert.Contains(state.SavedChunks, chunk => chunk.Content.Contains("Word indexing sentinel", StringComparison.Ordinal));
            Assert.Equal(state.SavedChunks.Count, vectors.Upserted.Count);
            Assert.All(vectors.Upserted, document => Assert.Equal("test-profile", document.Chunk.EmbeddingProfileId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DocxExpandedContentLimitIsEnforced()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "oversized.docx");
            CreateDocx(path);
            var options = Options.Create(new LocalRagOptions
            {
                Indexing = new IndexingOptions { MaxExpandedDocumentBytes = 16 }
            });
            var extractor = new WordDocumentContentExtractor(options);

            await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IOptions<LocalRagOptions> DefaultOptions() => Options.Create(new LocalRagOptions
    {
        Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
        Chunking = new ChunkingOptions { TargetTokens = 64, MaximumTokens = 128, OverlapTokens = 8 }
    });

    private static ContentExtractionService CreateExtractionService(IOptions<LocalRagOptions> options) =>
        new([new PlainTextContentExtractor(), new WordDocumentContentExtractor(options)]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-rag-docx-{Guid.NewGuid():N}");
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
              <w:body>
                <w:p><w:r><w:t>Word indexing sentinel</w:t></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Table cell evidence</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                <w:p>
                  <w:del><w:r><w:t>Deleted revision</w:t></w:r></w:del>
                  <w:ins><w:r><w:t>Inserted revision</w:t></w:r></w:ins>
                </w:p>
              </w:body>
            </w:document>
            """);
        WriteEntry(archive, "word/header1.xml", WordPart("hdr", "Header evidence"));
        WriteEntry(archive, "word/footer1.xml", WordPart("ftr", "Footer evidence"));
        WriteEntry(archive, "word/footnotes.xml", WordPart("footnotes", "Footnote evidence"));
        WriteEntry(archive, "word/comments.xml", WordPart("comments", "Reviewer comment"));
    }

    private static string WordPart(string rootName, string text) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <w:{rootName} xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:p><w:r><w:t>{text}</w:t></w:r></w:p>
        </w:{rootName}>
        """;

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public string ProfileId => "test-profile";
        public Task<IReadOnlyList<float>> EmbedQueryAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([0.5f, 0.5f]);
        public Task<IReadOnlyList<float>> EmbedPassageAsync(string input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<float>>([0.5f, 0.5f]);
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingIndexState : IIndexStateStore
    {
        public IndexedFile? SavedFile { get; private set; }
        public IReadOnlyList<ChunkRecord> SavedChunks { get; private set; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IndexedFile?> GetFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.FromResult<IndexedFile?>(null);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForFileAsync(string fileId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ChunkRecord>>([]);
        public Task SaveFileAndChunksAsync(IndexedFile file, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
        {
            SavedFile = file;
            SavedChunks = chunks;
            return Task.CompletedTask;
        }
        public Task DeleteFileAsync(string sourceId, string relativePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ChunkRecord?> GetChunkAsync(string chunkId, CancellationToken cancellationToken) => Task.FromResult<ChunkRecord?>(null);
        public Task<IReadOnlyList<ChunkRecord>> GetChunksForSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult(SavedChunks);
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public IReadOnlyList<VectorDocument> Upserted { get; private set; } = [];
        public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorDocument> documents, CancellationToken cancellationToken)
        {
            Upserted = documents;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IReadOnlyList<float> queryVector, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchResult>>([]);
    }
}
