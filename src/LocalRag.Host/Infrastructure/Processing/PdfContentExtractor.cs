using System.Text;
using LocalRag.Application;
using LocalRag.Configuration;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalRag.Infrastructure.Processing;

/// <summary>
/// Extracts searchable text from PDF pages in visual reading order and applies OCR to image-only pages.
/// </summary>
public sealed class PdfContentExtractor(IOptions<LocalRagOptions> options, IPdfOcrService ocrService) : IContentExtractor
{
    private readonly int _maxPages = options.Value.Indexing.MaxPdfPages;
    private readonly int _maxTextCharacters = options.Value.Indexing.MaxPdfTextCharacters;

    /// <summary>Returns whether <paramref name="path"/> has the PDF extension.</summary>
    public bool Supports(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads and concatenates page text, enforcing the configured page and character limits.
    /// </summary>
    /// <param name="path">Path to the PDF file to read.</param>
    /// <param name="cancellationToken">Token checked before opening and between pages.</param>
    /// <returns>Extracted page text separated by blank lines; empty when no text is present.</returns>
    /// <exception cref="InvalidDataException">Thrown when a configured PDF or OCR limit is exceeded, or required OCR data is missing.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when OCR is needed on a non-Windows host.</exception>
    public async Task<string> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var document = PdfDocument.Open(stream);
        if (_maxPages <= 0 || document.NumberOfPages > _maxPages)
        {
            throw new InvalidDataException($"The PDF contains {document.NumberOfPages} pages, exceeding the configured limit of {_maxPages} pages.");
        }
        if (_maxTextCharacters <= 0)
        {
            throw new InvalidDataException("The configured PDF text character limit must be greater than zero.");
        }

        var pageTexts = new string[document.NumberOfPages];
        var pagesNeedingOcr = new List<int>();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page).Trim();
            pageTexts[page.Number - 1] = pageText;
            if (string.IsNullOrWhiteSpace(pageText)) pagesNeedingOcr.Add(page.Number - 1);
        }

        if (pagesNeedingOcr.Count > 0)
        {
            var ocrText = await ocrService.ExtractPagesAsync(path, pagesNeedingOcr, cancellationToken);
            foreach (var page in ocrText) pageTexts[page.Key] = page.Value.Trim();
        }

        var content = new StringBuilder();
        foreach (var pageText in pageTexts)
        {
            if (string.IsNullOrWhiteSpace(pageText)) continue;
            var separatorLength = content.Length == 0 ? 0 : 2;
            if ((long)content.Length + separatorLength + pageText.Length > _maxTextCharacters)
            {
                throw new InvalidDataException($"The PDF text exceeds the configured limit of {_maxTextCharacters} characters.");
            }
            if (separatorLength > 0) content.Append("\n\n");
            content.Append(pageText);
        }
        return content.ToString();
    }
}
