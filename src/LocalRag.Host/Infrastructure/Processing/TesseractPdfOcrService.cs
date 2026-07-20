using LocalRag.Configuration;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;

namespace LocalRag.Infrastructure.Processing;

/// <summary>Runs local Tesseract OCR against selected rasterized PDF pages.</summary>
public interface IPdfOcrService
{
    /// <summary>Extracts text for the requested pages, keyed by their zero-based PDF indexes.</summary>
    /// <param name="path">Path to the PDF that supplies the pages.</param>
    /// <param name="zeroBasedPageIndexes">Zero-based page indexes that have no embedded text.</param>
    /// <param name="cancellationToken">Token checked before rendering and OCR of each page.</param>
    /// <returns>OCR text keyed by page index; empty when OCR is disabled or no pages are requested.</returns>
    Task<IReadOnlyDictionary<int, string>> ExtractPagesAsync(
        string path,
        IReadOnlyList<int> zeroBasedPageIndexes,
        CancellationToken cancellationToken);
}

/// <summary>Provides Windows-only OCR using the bundled English Tesseract trained data.</summary>
public sealed class TesseractPdfOcrService(IOptions<LocalRagOptions> options) : IPdfOcrService
{
    private readonly IndexingOptions _options = options.Value.Indexing;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> ExtractPagesAsync(
        string path,
        IReadOnlyList<int> zeroBasedPageIndexes,
        CancellationToken cancellationToken)
    {
        if (!_options.EnablePdfOcr || zeroBasedPageIndexes.Count == 0)
        {
            return new Dictionary<int, string>();
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Bundled PDF OCR is supported by the standalone Windows host.");
        }
        ValidateOptions(zeroBasedPageIndexes.Count);

        var pdfBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        var trainedDataPath = Path.Combine(dataPath, "eng.traineddata");
        if (!File.Exists(trainedDataPath))
        {
            throw new InvalidDataException($"PDF OCR language data was not found at '{trainedDataPath}'.");
        }

        using var engine = new Engine(dataPath, Language.English, EngineMode.LstmOnly);
        var results = new Dictionary<int, string>(zeroBasedPageIndexes.Count);
        var renderOptions = new RenderOptions(Dpi: _options.PdfOcrDpi, Grayscale: true);
        foreach (var pageIndex in zeroBasedPageIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRenderedPageSize(pdfBytes, pageIndex);
            using var bitmap = Conversion.ToImage(pdfBytes, pageIndex, options: renderOptions);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var image = TesseractOCR.Pix.Image.LoadFromMemory(encoded.ToArray());
            using var page = engine.Process(image, PageSegMode.Auto);
            results[pageIndex] = page.Text;
        }
        return results;
    }

    private void ValidateOptions(int pageCount)
    {
        if (_options.PdfOcrDpi is < 72 or > 600)
        {
            throw new InvalidDataException("The configured PDF OCR DPI must be between 72 and 600.");
        }
        if (_options.MaxPdfOcrPages <= 0 || pageCount > _options.MaxPdfOcrPages)
        {
            throw new InvalidDataException($"The PDF requires OCR for {pageCount} pages, exceeding the configured limit of {_options.MaxPdfOcrPages} pages.");
        }
        if (_options.MaxPdfOcrPixelsPerPage <= 0)
        {
            throw new InvalidDataException("The configured PDF OCR pixel limit must be greater than zero.");
        }
    }

    private void ValidateRenderedPageSize(byte[] pdfBytes, int pageIndex)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var pageSize = Conversion.GetPageSize(pdfBytes, pageIndex);
        var scale = _options.PdfOcrDpi / 72d;
        var pixelCount = Math.Ceiling(pageSize.Width * scale) * Math.Ceiling(pageSize.Height * scale);
        if (pixelCount > _options.MaxPdfOcrPixelsPerPage)
        {
            throw new InvalidDataException($"PDF page {pageIndex + 1} would render to {pixelCount:N0} pixels, exceeding the configured OCR limit of {_options.MaxPdfOcrPixelsPerPage:N0} pixels.");
        }
    }
}
