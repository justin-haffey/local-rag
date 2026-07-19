using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LocalRag.Infrastructure.Indexing;

public sealed class IndexWorkChannel
{
    private readonly ConcurrentDictionary<string, byte> _scheduled = new(StringComparer.Ordinal);
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });

    public async ValueTask EnqueueAsync(string sourceId, CancellationToken cancellationToken)
    {
        if (!_scheduled.TryAdd(sourceId, 0)) return;
        try { await _channel.Writer.WriteAsync(sourceId, cancellationToken); }
        catch { _scheduled.TryRemove(sourceId, out _); throw; }
    }

    public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sourceId in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _scheduled.TryRemove(sourceId, out _);
            yield return sourceId;
        }
    }
}
