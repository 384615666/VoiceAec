namespace VoiceAec;

internal sealed class SampleRing
{
    private readonly object _lock = new();
    private readonly byte[] _buffer;
    private int _readPos;
    private int _writePos;
    private int _count;

    public SampleRing(int capacityBytes)
    {
        _buffer = new byte[Math.Max(1024, capacityBytes)];
    }

    public int CapacityBytes => _buffer.Length;

    public int BufferedBytes
    {
        get { lock (_lock) return _count; }
    }

    public int Write(byte[] data, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_lock)
        {
            var offset = Math.Max(0, count - _buffer.Length);
            var incoming = count - offset;
            var dropped = Math.Max(0, _count + incoming - _buffer.Length);
            if (dropped > 0)
            {
                _readPos = (_readPos + dropped) % _buffer.Length;
                _count -= dropped;
            }
            var first = Math.Min(incoming, _buffer.Length - _writePos);
            Array.Copy(data, offset, _buffer, _writePos, first);
            if (incoming > first)
                Array.Copy(data, offset + first, _buffer, 0, incoming - first);
            _writePos = (_writePos + incoming) % _buffer.Length;
            _count += incoming;
            return dropped + offset;
        }
    }

    public int Read(byte[] destination, int offset, int count)
    {
        lock (_lock)
        {
            var actual = Math.Min(count, _count);
            var first = Math.Min(actual, _buffer.Length - _readPos);
            Array.Copy(_buffer, _readPos, destination, offset, first);
            if (actual > first)
                Array.Copy(_buffer, 0, destination, offset + first, actual - first);
            _readPos = (_readPos + actual) % _buffer.Length;
            _count -= actual;
            return actual;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
        }
    }
}
