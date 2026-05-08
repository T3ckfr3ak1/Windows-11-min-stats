namespace TaskbarResourceMonitor;

internal sealed class RingBuffer
{
    private readonly double[] _buf;
    private int _idx;
    private int _count;

    public RingBuffer(int capacity) => _buf = new double[capacity];

    public void Add(double v)
    {
        _buf[_idx] = v;
        _idx = (_idx + 1) % _buf.Length;
        _count = Math.Min(_count + 1, _buf.Length);
    }

    public double Latest => _count == 0 ? 0 : _buf[(_idx - 1 + _buf.Length) % _buf.Length];

    public double[] Snapshot()
    {
        var n = _count;
        var outArr = new double[n];
        for (var i = 0; i < n; i++)
        {
            var src = (_idx - n + i + _buf.Length) % _buf.Length;
            outArr[i] = _buf[src];
        }
        return outArr;
    }
}

