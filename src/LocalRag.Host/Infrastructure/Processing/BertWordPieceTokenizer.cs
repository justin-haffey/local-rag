using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace LocalRag.Infrastructure.Processing;

internal sealed partial class BertWordPieceTokenizer : IChunkTokenCounter
{
    private readonly FrozenDictionary<string, int> _vocabulary;
    private readonly int _unknownId;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;

    public BertWordPieceTokenizer(string vocabularyPath)
    {
        if (!File.Exists(vocabularyPath)) throw new FileNotFoundException("ONNX model vocabulary was not found.", vocabularyPath);
        _vocabulary = File.ReadLines(vocabularyPath)
            .Select((token, index) => (token, index))
            .ToFrozenDictionary(pair => pair.token, pair => pair.index, StringComparer.Ordinal);
        _unknownId = GetRequiredId("[UNK]");
        _clsId = GetRequiredId("[CLS]");
        _sepId = GetRequiredId("[SEP]");
        _padId = GetRequiredId("[PAD]");
    }

    public long[] Encode(string content, int maximumTokens)
    {
        var tokenCount = CountTokens(content);
        if (maximumTokens < 3 || tokenCount > maximumTokens)
        {
            throw new InvalidOperationException(
                $"WordPiece input requires {tokenCount} tokens but the configured hard limit is {maximumTokens}.");
        }
        var ids = new List<int> { _clsId };
        foreach (var word in WordRegex().Matches(content.ToLowerInvariant()).Select(match => match.Value))
        {
            foreach (var id in EncodeWord(word))
            {
                if (ids.Count >= maximumTokens - 1) break;
                ids.Add(id);
            }
            if (ids.Count >= maximumTokens - 1) break;
        }
        ids.Add(_sepId);
        while (ids.Count < maximumTokens) ids.Add(_padId);
        return ids.Select(id => (long)id).ToArray();
    }

    public int CountTokens(string content)
    {
        var count = 2;
        foreach (var word in WordRegex().Matches(content.ToLowerInvariant()).Select(match => match.Value))
        {
            count += EncodeWord(word).Count;
        }
        return count;
    }

    private List<int> EncodeWord(string word)
    {
        if (word.Length == 1 && _vocabulary.TryGetValue(word, out var punctuationId)) return [punctuationId];
        var output = new List<int>();
        var offset = 0;
        while (offset < word.Length)
        {
            var found = false;
            for (var end = word.Length; end > offset; end--)
            {
                var candidate = (offset == 0 ? string.Empty : "##") + word[offset..end];
                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    output.Add(id);
                    offset = end;
                    found = true;
                    break;
                }
            }
            if (!found) return [_unknownId];
        }
        return output;
    }

    private int GetRequiredId(string token) => _vocabulary.TryGetValue(token, out var id)
        ? id : throw new InvalidOperationException($"BERT vocabulary is missing required token {token}.");

    [GeneratedRegex("[a-z0-9_]+|[^\\s]", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
