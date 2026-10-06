using System.Threading.Channels;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Analytics;

/// <summary>
/// Bounded in-memory buffer between the redirect path and the database writer.
/// Under extreme load the oldest events are dropped rather than slowing redirects or growing memory without bound;
/// the drop count is exposed so operators can see analytics loss.
/// </summary>
public sealed class ClickQueue
{
    private readonly Channel<ClickEvent> _channel;
    private long _dropped;

    public ClickQueue(int capacity = 10_000)
    {
        _channel = Channel.CreateBounded<ClickEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(ClickEvent click)
    {
        if (_channel.Writer.TryWrite(click)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}
