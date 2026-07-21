using System.Diagnostics;
using System.Diagnostics.Metrics;
using LocalRag.Application;
using LocalRag.Configuration;
using LocalRag.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalRag.Infrastructure.Processing;

/// <summary>Selects one bounded structural adapter and falls back atomically to generic chunking.</summary>
public sealed partial class CompositeChunker : IChunker
{
    internal const string MeterName = "LocalRag.Chunking";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> FileCounter = Meter.CreateCounter<long>("localrag.chunking.files");
    private static readonly Counter<long> ChunkCounter = Meter.CreateCounter<long>("localrag.chunking.chunks");
    private static readonly Counter<long> FallbackCounter = Meter.CreateCounter<long>("localrag.chunking.fallbacks");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("localrag.chunking.duration", "ms");
    private readonly IReadOnlyList<IStructuralChunker> _adapters;
    private readonly HashSet<string> _enabledAdapters;
    private readonly GenericChunker _fallback;
    private readonly IChunkProfileProvider _profile;
    private readonly LocalRagOptions _options;
    private readonly IChunkTokenCounter _tokenCounter;
    private readonly ILogger<CompositeChunker> _logger;

    internal CompositeChunker(
        IEnumerable<IStructuralChunker> adapters,
        GenericChunker fallback,
        IChunkProfileProvider profile,
        IOptions<LocalRagOptions> options,
        IChunkTokenCounter tokenCounter,
        ILogger<CompositeChunker> logger)
    {
        _adapters = adapters.OrderBy(adapter => adapter.ChunkerId, StringComparer.Ordinal).ToArray();
        _enabledAdapters = options.Value.Chunking.EnabledAdapters.ToHashSet(StringComparer.Ordinal);
        _fallback = fallback;
        _profile = profile;
        _options = options.Value;
        _tokenCounter = tokenCounter;
        _logger = logger;
    }

    public IReadOnlyList<ChunkRecord> Chunk(SourceRecord source, IndexedFile file, string normalizedContent)
    {
        var started = Stopwatch.GetTimestamp();
        var adapter = _adapters.FirstOrDefault(candidate =>
            _enabledAdapters.Contains(candidate.ChunkerId) && candidate.Supports(file.RelativePath));
        if (adapter is null)
        {
            return Record(_fallback.Chunk(source, file, normalizedContent), "generic", "1", "unsupported", started, fallback: true);
        }

        try
        {
            if (!adapter.TryChunk(file.RelativePath, normalizedContent, out var units) || !AreValid(units, normalizedContent))
            {
                LogFallback(adapter.ChunkerId, "invalid-or-malformed");
                return Record(_fallback.Chunk(source, file, normalizedContent), adapter.ChunkerId, adapter.ChunkerVersion,
                    "invalid-or-malformed", started, fallback: true);
            }

            return Record(BuildChunks(source, file, normalizedContent, adapter, units), adapter.ChunkerId,
                adapter.ChunkerVersion, "structural", started, fallback: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            LogFallback(adapter.ChunkerId, "adapter-exception");
            return Record(_fallback.Chunk(source, file, normalizedContent), adapter.ChunkerId, adapter.ChunkerVersion,
                "adapter-exception", started, fallback: true);
        }
    }

    private static IReadOnlyList<ChunkRecord> Record(
        IReadOnlyList<ChunkRecord> chunks,
        string chunkerId,
        string chunkerVersion,
        string outcome,
        long started,
        bool fallback)
    {
        var tags = new TagList
        {
            { "chunker.id", chunkerId },
            { "chunker.version", chunkerVersion },
            { "chunking.outcome", outcome }
        };
        FileCounter.Add(1, tags);
        ChunkCounter.Add(chunks.Count, tags);
        if (fallback) FallbackCounter.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        return chunks;
    }

    private List<ChunkRecord> BuildChunks(
        SourceRecord source,
        IndexedFile file,
        string content,
        IStructuralChunker adapter,
        IReadOnlyList<StructuralUnit> units)
    {
        var lines = ChunkingText.Lines(content);
        var hardLimit = Math.Min(_options.Chunking.MaximumTokens, _options.Embedding.MaximumTokens);
        if (hardLimit < 3) throw new InvalidOperationException("Chunk and embedding token limits must both be at least three.");
        var chunks = new List<ChunkRecord>();
        foreach (var unit in units.OrderBy(unit => unit.StartLine).ThenBy(unit => unit.EndLine).ThenBy(unit => unit.Locator, StringComparer.Ordinal))
        {
            var unitContent = ChunkingText.Slice(lines, unit.StartLine, unit.EndLine);
            var segments = Split(unitContent, unit.StartLine, hardLimit).ToArray();
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (string.IsNullOrWhiteSpace(segment.Content)) continue;
                var continuation = segments.Length > 1;
                var locator = continuation ? $"{unit.Locator}:segment:{index + 1}" : unit.Locator;
                var kind = index == 0 ? unit.Kind : $"{unit.Kind}-continuation";
                chunks.Add(ChunkingText.CreateRecord(source, file, segment.Content, segment.StartLine, segment.EndLine,
                    chunks.Count, kind, unit.SymbolName, unit.QualifiedSymbolName, locator, adapter.ChunkerId,
                    adapter.ChunkerVersion, _profile.Fingerprint, CountPassageTokens(segment.Content)));
            }
        }
        return chunks;
    }

    private static bool AreValid(IReadOnlyList<StructuralUnit>? units, string content)
    {
        if (units is null) return false;
        var lineCount = ChunkingText.Lines(content).Length;
        var locators = new HashSet<string>(StringComparer.Ordinal);
        return units.All(unit => unit.StartLine >= 1 && unit.EndLine >= unit.StartLine && unit.EndLine <= lineCount &&
            !string.IsNullOrWhiteSpace(unit.Kind) && !string.IsNullOrWhiteSpace(unit.Locator) && locators.Add(unit.Locator));
    }

    private IEnumerable<(string Content, int StartLine, int EndLine)> Split(string content, int firstLine, int hardLimit)
    {
        if (CountPassageTokens(content) <= hardLimit)
        {
            yield return (content, firstLine, firstLine + ChunkingText.Lines(content).Length - 1);
            yield break;
        }

        var lines = ChunkingText.Lines(content);
        var start = 0;
        while (start < lines.Length)
        {
            if (CountPassageTokens(lines[start]) > hardLimit)
            {
                for (var offset = 0; offset < lines[start].Length;)
                {
                    var low = 1;
                    var high = lines[start].Length - offset;
                    var acceptedLength = 0;
                    while (low <= high)
                    {
                        var candidateLength = low + ((high - low) / 2);
                        if (CountPassageTokens(lines[start].Substring(offset, candidateLength)) <= hardLimit)
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
                    yield return (lines[start].Substring(offset, acceptedLength), firstLine + start, firstLine + start);
                    offset += acceptedLength;
                }
                start++;
                continue;
            }

            var end = start + 1;
            var segment = lines[start];
            while (end < lines.Length && CountPassageTokens(segment + "\n" + lines[end]) <= hardLimit)
            {
                segment += "\n" + lines[end];
                end++;
            }
            yield return (segment, firstLine + start, firstLine + end - 1);
            start = end;
        }
    }

    private int CountPassageTokens(string content) =>
        _tokenCounter.CountTokens(_options.Embedding.PassagePrefix + content);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning,
        Message = "Structural chunker {ChunkerId} used generic fallback; category={Category}")]
    private partial void LogFallback(string chunkerId, string category);
}
