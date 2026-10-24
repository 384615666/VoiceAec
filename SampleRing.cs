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
        lock (_lock)
        {
            var dropped = Math.Max(0, _count + count - _buffer.Length);
            if (dropped > 0)
            {
                _readPos = (_readPos + dropped) % _buffer.Length;
                _count -= dropped;
            }
            var first = Math.Min(count, _buffer.Length - _writePos);
            Array.Copy(data, 0, _buffer, _writePos, first);
            if (count > first)
                Array.Copy(data, first, _buffer, 0, count - first);
            _writePos = (_writePos + count) % _buffer.Length;
            _count += count;
            return dropped;
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
