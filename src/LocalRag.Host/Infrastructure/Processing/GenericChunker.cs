using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Processing;

/// <summary>Deterministic line-preserving fallback used when structural parsing is unavailable or unsafe.</summary>
public sealed class GenericChunker : IChunker
{
    private readonly LocalRagOptions _options;
    private readonly IChunkProfileProvider _profile;
    private readonly IChunkTokenCounter _tokenCounter;

    public GenericChunker(IOptions<LocalRagOptions> options)
        : this(options, new ChunkProfileProvider(options), new CharacterUpperBoundTokenCounter())
    {
    }

    public GenericChunker(IOptions<LocalRagOptions> options, IChunkProfileProvider profile)
        : this(options, profile, new CharacterUpperBoundTokenCounter())
    {
    }

    internal GenericChunker(
        IOptions<LocalRagOptions> options,
        IChunkProfileProvider profile,
        IChunkTokenCounter tokenCounter)
    {
        _options = options.Value;
        _profile = profile;
        _tokenCounter = tokenCounter;
    }

    public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent)
    {
        var lines = ChunkingText.Lines(normalizedContent);
        var chunks = new List<ChunkRecord>();
        var hardLimit = Math.Min(_options.Chunking.MaximumTokens, _options.Embedding.MaximumTokens);
        if (hardLimit < 3) throw new InvalidOperationException("Chunk and embedding token limits must both be at least three.");
        var target = Math.Clamp(_options.Chunking.TargetTokens, 3, hardLimit);
        var start = 0;
        var ordinal = 0;

        while (start < lines.Length)
        {
            var end = start;
            string content;
            do
            {
                end++;
                content = string.Join('\n', lines[start..end]);
            }
            while (end < lines.Length && CountPassageTokens(content + "\n" + lines[end]) <= hardLimit &&
                   (CountPassageTokens(content) < target || !string.IsNullOrWhiteSpace(lines[end - 1])));

            if (CountPassageTokens(content) > hardLimit)
            {
                foreach (var segment in SplitOversizedLine(lines[start], hardLimit))
                {
                    if (string.IsNullOrWhiteSpace(segment)) continue;
                    var locator = $"lines:{start + 1}-{start + 1}:segment:{chunks.Count + 1}";
                    chunks.Add(ChunkingText.CreateRecord(source, file, segment, start + 1, start + 1, ordinal++, "text",
                        null, null, locator, "generic", "1", _profile.Fingerprint, CountPassageTokens(segment)));
                }
            }
            else if (!string.IsNullOrWhiteSpace(content))
            {
                var locator = $"lines:{start + 1}-{end}";
                chunks.Add(ChunkingText.CreateRecord(source, file, content, start + 1, end, ordinal++, "text",
                    null, null, locator, "generic", "1", _profile.Fingerprint, CountPassageTokens(content)));
            }

            if (end >= lines.Length) break;
            var overlapLines = Math.Max(0, _options.Chunking.OverlapTokens / 8);
            start = Math.Max(end - overlapLines, start + 1);
        }

        return chunks;
    }

    private IEnumerable<string> SplitOversizedLine(string line, int hardLimit)
    {
        for (var offset = 0; offset < line.Length;)
        {
            var low = 1;
            var high = line.Length - offset;
            var acceptedLength = 0;
            while (low <= high)
            {
                var candidateLength = low + ((high - low) / 2);
                if (CountPassageTokens(line.Substring(offset, candidateLength)) <= hardLimit)
                {
                    acceptedLength = candidateLength;
                    low = candidateLength + 1;
                }
                else
                {
                    high = candidateLength - 1;
                }
            }
            if (acceptedLength == 0)
            {
                throw new InvalidOperationException("The configured passage prefix leaves no token capacity for chunk content.");
            }
            yield return line.Substring(offset, acceptedLength);
            offset += acceptedLength;
        }
    }

    private int CountPassageTokens(string content) =>
        _tokenCounter.CountTokens(_options.Embedding.PassagePrefix + content);
}
