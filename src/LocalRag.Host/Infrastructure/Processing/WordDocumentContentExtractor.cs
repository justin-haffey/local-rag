using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LocalRag.Application;
using LocalRag.Configuration;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Processing;

/// <summary>Extracts searchable paragraph text from Open XML Word documents without executing macros or external relationships.</summary>
public sealed class WordDocumentContentExtractor(IOptions<LocalRagOptions> options) : IContentExtractor
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly long _maxExpandedBytes = options.Value.Indexing.MaxExpandedDocumentBytes;

    public bool Supports(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.GetEntry("word/document.xml") is null)
        {
            throw new InvalidDataException("The DOCX package does not contain word/document.xml.");
        }

        var parts = archive.Entries
            .Where(entry => IsSearchablePart(entry.FullName))
            .OrderBy(entry => PartOrder(entry.FullName))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expandedBytes = parts.Aggregate(0L, (total, entry) => checked(total + entry.Length));
        if (_maxExpandedBytes <= 0 || expandedBytes > _maxExpandedBytes)
        {
            throw new InvalidDataException($"The DOCX searchable XML expands to {expandedBytes} bytes, exceeding the configured limit of {_maxExpandedBytes} bytes.");
        }

        var sections = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ExtractPartAsync(part, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text)) sections.Add(text);
        }
        return string.Join("\n\n", sections);
    }

    private static bool IsSearchablePart(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        if (!normalized.StartsWith("word/", StringComparison.OrdinalIgnoreCase) || !normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = normalized["word/".Length..];
        return fileName.Equals("document.xml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("footnotes.xml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("endnotes.xml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("comments.xml", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("header", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("footer", StringComparison.OrdinalIgnoreCase);
    }

    private static int PartOrder(string fullName)
    {
        var fileName = fullName.Replace('\\', '/')["word/".Length..];
        if (fileName.Equals("document.xml", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.StartsWith("header", StringComparison.OrdinalIgnoreCase)) return 1;
        if (fileName.StartsWith("footer", StringComparison.OrdinalIgnoreCase)) return 2;
        if (fileName.Equals("footnotes.xml", StringComparison.OrdinalIgnoreCase)) return 3;
        if (fileName.Equals("endnotes.xml", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static async Task<string> ExtractPartAsync(ZipArchiveEntry part, CancellationToken cancellationToken)
    {
        await using var partStream = part.Open();
        using var reader = XmlReader.Create(partStream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var paragraphs = new List<string>();
        foreach (var paragraph in document.Descendants(Word + "p"))
        {
            var text = ExtractParagraph(paragraph);
            if (!string.IsNullOrWhiteSpace(text)) paragraphs.Add(text);
        }
        return string.Join('\n', paragraphs);
    }

    private static string ExtractParagraph(XElement paragraph)
    {
        var output = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            if (element.Ancestors(Word + "del").Any()) continue;
            if (element.Name == Word + "t") output.Append(element.Value);
            else if (element.Name == Word + "tab") output.Append('\t');
            else if (element.Name == Word + "br" || element.Name == Word + "cr") output.Append('\n');
            else if (element.Name == Word + "noBreakHyphen") output.Append('\u2011');
            else if (element.Name == Word + "softHyphen") output.Append('\u00AD');
        }
        return output.ToString().Trim();
    }
}
