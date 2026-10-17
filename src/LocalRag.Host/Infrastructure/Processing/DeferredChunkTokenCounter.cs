namespace LocalRag.Infrastructure.Processing;

/// <summary>Resolve model assets on actual chunking so missing assets leave manager diagnostics available.</summary>
internal sealed class DeferredChunkTokenCounter(Func<IChunkTokenCounter> create) : IChunkTokenCounter
{
    private readonly Lazy<IChunkTokenCounter> _inner = new(create);
    public int CountTokens(string content) => _inner.Value.CountTokens(content);
}
