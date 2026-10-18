namespace Aria2Tray.Model;

/// <summary>最近 60 秒的全局下载、上传速度，每秒一个点。面板收起时 2 秒取一次，同一个值记两个点。</summary>
internal sealed class SpeedHistory
{
    public const int Length = 60;

    private readonly long[] _down = new long[Length];
    private readonly long[] _up = new long[Length];
    private int _next;
    private long _lastTick;

    /// <summary>按经过的整秒数补点（最少一个）。</summary>
    public void Add(long down, long up)
    {
        var now = Environment.TickCount64;
        var seconds = _lastTick == 0 ? 1 : (int)Math.Clamp((now - _lastTick + 500) / 1000, 1, Length);
        _lastTick = now;
        for (var i = 0; i < seconds; i++)
        {
            _down[_next] = down;
            _up[_next] = up;
            _next = (_next + 1) % Length;
        }
    }

    public void Clear()
    {
        Array.Clear(_down);
        Array.Clear(_up);
        _lastTick = 0;
    }

    /// <summary>从旧到新。</summary>
    public IReadOnlyList<long> Down => Ordered(_down);

    public IReadOnlyList<long> Up => Ordered(_up);

    public long Max => Math.Max(_down.Max(), _up.Max());

    private long[] Ordered(long[] a) => [.. a[_next..], .. a[.._next]];
}
