using LocalRag.Infrastructure.Processing;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class BertWordPieceTokenizerTests
{
    [Fact]
    public void CountTokensUsesTheSameWordPieceRulesAsEmbeddingEncoding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"localrag-vocab-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path, ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "hello", "world", "##s", "!"]);
            var tokenizer = new BertWordPieceTokenizer(path);

            var count = tokenizer.CountTokens("Hello worlds!");
            var encoded = tokenizer.Encode("Hello worlds!", 8);

            Assert.Equal(6, count);
            Assert.Equal(count, encoded.Count(id => id != 0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
