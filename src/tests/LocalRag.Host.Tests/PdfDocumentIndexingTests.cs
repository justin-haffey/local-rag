using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Diagnostics;
using LocalRag.Infrastructure.Indexing;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Options;
using PDFtoImage;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace LocalRag.Host.Tests;

public sealed class PdfDocumentIndexingTests(ITestOutputHelper output)
{
    [Fact]
    public void ExhibitARequiresOcr()
    {
        using var document = PdfDocument.Open(FixturePath);
        var pages = document.GetPages().ToArray();

        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            output.WriteLine($"Page {page.Number}: letters={page.Letters.Count}, images={page.GetImages().Count()}");
        }
        Assert.Contains(pages, page => page.GetImages().Any());
        Assert.All(pages, page => Assert.Empty(page.Letters));
    }

    [Fact]
    public void ExhibitAFirstPageRendersForOcrInspection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inspectionDirectory = Path.Combine(Path.GetTempPath(), "local-rag-pdf-inspection");
        Directory.CreateDirectory(inspectionDirectory);
        var imagePath = Path.Combine(inspectionDirectory, "exhibit-a-page-1.png");

        Conversion.SavePng(imagePath, File.ReadAllBytes(FixturePath), 0, options: new RenderOptions(Dpi: 150, Grayscale: true));

        Assert.True(new FileInfo(imagePath).Length > 10_000);
        output.WriteLine(imagePath);
    }

    [Fact]
    public async Task ExhibitAExtractionProducesSearchableText()
    {
        var content = await CreateExtractionService(DefaultOptions()).ExtractAsync(FixturePath, CancellationToken.None);

        Assert.True(content.Length > 100);
        Assert.DoesNotContain('\0', content);
        Assert.Contains("SYNTHETIC OCR EXHIBIT A", content, StringComparison.OrdinalIgnoreCase);
        output.WriteLine(content[..Math.Min(content.Length, 4_000)]);
    }

    [Fact]
    public async Task NativePdfExtractionUsesEmbeddedTextWithoutOcr()
    {
        var options = DefaultOptions();
        var extractor = new PdfContentExtractor(options, new ThrowingPdfOcrService());

        var content = await extractor.ExtractAsync(NativeFixturePath, CancellationToken.None);

        Assert.True(content.Length > 1_000);
        Assert.Contains("decree", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', content);
        output.WriteLine(content[..Math.Min(content.Length, 4_000)]);
    }

    [Fact]
    public void FilePolicyAllowsPdf()
    {
        var options = DefaultOptions();
        var policy = new FilePolicy(options, CreateExtractionService(options));

        Assert.True(policy.IsEligible(Path.GetDirectoryName(FixturePath)!, FixturePath, new FileInfo(FixturePath)));
    }

    [Fact]
    public async Task FileIndexingServicePersistsAndEmbedsExhibitAPdfChunks()
    {
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
        var source = new SourceRecord("source", Path.GetDirectoryName(FixturePath)!, "fixture", SourceStatus.Ready, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "test-profile");

        await service.IndexAsync(source, FixturePath, "Exhibit A.pdf", new FileInfo(FixturePath), CancellationToken.None);

        Assert.NotNull(state.SavedFile);
        Assert.Equal("Exhibit A.pdf", state.SavedFile.RelativePath);
        Assert.NotEmpty(state.SavedChunks);
        Assert.All(state.SavedChunks, chunk => Assert.Equal("pdf", chunk.Language));
        Assert.Equal(state.SavedChunks.Count, vectors.Upserted.Count);
    }

    [Fact]
    public async Task PdfTextLimitIsEnforced()
    {
        var options = Options.Create(new LocalRagOptions
        {
            Indexing = new IndexingOptions { MaxPdfTextCharacters = 16 }
        });
        var extractor = new PdfContentExtractor(options, new ThrowingPdfOcrService());

        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(NativeFixturePath, CancellationToken.None));
    }

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Exhibit A.pdf");
    private static string NativeFixturePath => Path.Combine(AppContext.BaseDirectory, "Final Decree [615749].pdf");

    private static IOptions<LocalRagOptions> DefaultOptions() => Options.Create(new LocalRagOptions
    {
        Indexing = new IndexingOptions { StabilityIntervalMilliseconds = 0 },
        Chunking = new ChunkingOptions { TargetTokens = 64, MaximumTokens = 128, OverlapTokens = 8 }
    });

    private static ContentExtractionService CreateExtractionService(IOptions<LocalRagOptions> options) =>
        new([new PlainTextContentExtractor(), new WordDocumentContentExtractor(options), new PdfContentExtractor(options, new TesseractPdfOcrService(options))]);

    private sealed class ThrowingPdfOcrService : IPdfOcrService
    {
        public Task<IReadOnlyDictionary<int, string>> ExtractPagesAsync(string path, IReadOnlyList<int> zeroBasedPageIndexes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OCR should not be used for a native-text PDF.");
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
