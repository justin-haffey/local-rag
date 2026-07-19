using LocalRag.Configuration;
using LocalRag.Domain;
using LocalRag.Infrastructure.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalRag.Host.Tests;

public sealed class GenericChunkerTests
{
    [Fact]
    public void ChunkProducesStableLineBoundariesAndIds()
    {
        var chunker = new GenericChunker(Options.Create(new LocalRagOptions
        {
            Chunking = new ChunkingOptions { TargetTokens = 2, MaximumTokens = 4, OverlapTokens = 1 }
        }));
        var source = new SourceRecord("source", "C:\\fixture", "fixture", SourceStatus.Ready, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "profile");
        var file = new IndexedFile("file", "source", "src/Test.cs", "filehash", 12, DateTimeOffset.UtcNow);

        var first = chunker.Chunk(source, file, "one\ntwo\nthree\nfour");
        var second = chunker.Chunk(source, file, "one\ntwo\nthree\nfour");

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(chunk => chunk.ChunkId), second.Select(chunk => chunk.ChunkId));
        Assert.All(first, chunk => Assert.True(chunk.StartLine <= chunk.EndLine));
        Assert.All(first, chunk => Assert.Equal("csharp", chunk.Language));
    }
}
