using System.Threading.Channels;

/// <summary>A bounded cross-thread inbox that applies backpressure instead of dropping queued state.</summary>
internal sealed class BoundedInbox<T>
{
    private readonly Channel<T> _channel;
    private int _count;
    private int _highWatermark;
    public int ApproximateCount => Volatile.Read(ref _count);
    public int HighWatermark => Volatile.Read(ref _highWatermark);

    public BoundedInbox(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    public async ValueTask EnqueueAsync(T value, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _count);
        var watermark = Volatile.Read(ref _highWatermark);
        while (count > watermark)
        {
            var previous = Interlocked.CompareExchange(ref _highWatermark, count, watermark);
            if (previous == watermark) break;
            watermark = previous;
        }
        try { await _channel.Writer.WriteAsync(value, cancellationToken).ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref _count); throw; }
    }

    public bool TryDequeue(out T value)
    {
        if (!_channel.Reader.TryRead(out value!)) return false;
        Interlocked.Decrement(ref _count);
        return true;
    }
}
