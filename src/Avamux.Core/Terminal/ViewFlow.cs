namespace Avamux.Core.Terminal;

/// <summary>
/// Flow control between a terminal's program and one view of it. The view acknowledges output once xterm.js has
/// parsed it; while it is more than <see cref="High"/> bytes behind, the pty's read thread waits in
/// <see cref="WaitForView"/> (until it is back under <see cref="Low"/>), so the kernel's pty buffer fills and the
/// program blocks on its writes, as it would in any terminal. Without this a fast program (find, cat of a big file)
/// is never held back: tens of megabytes queue up for the view, and Ctrl+C, although it stops the program at once,
/// leaves the screen scrolling for seconds.
/// <para>
/// A view that cannot catch up within <see cref="MaxWait"/> (a page the web view throttles while it is hidden, a
/// view that never acknowledges) is not waited for again until it has caught up: a program never runs at the pace
/// of a view nobody is looking at, and a stuck view can never hang a terminal.
/// </para>
/// </summary>
internal sealed class ViewFlow
{
    public const long High = 256 * 1024, Low = 64 * 1024;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly long _high, _low;
    private readonly TimeSpan _maxWait;
    private long _outstanding;
    private bool _free, _hidden, _closed;

    public ViewFlow() : this(High, Low, MaxWait) { }

    internal ViewFlow(long high, long low, TimeSpan maxWait) { _high = high; _low = low; _maxWait = maxWait; }

    /// <summary>Output handed to the view and not yet acknowledged as parsed.</summary>
    public long Outstanding { get { lock (_gate) return _outstanding; } }

    /// <summary>The view was too slow to wait for, and has not caught up since.</summary>
    public bool Free { get { lock (_gate) return _free; } }

    public void Queued(long bytes) { lock (_gate) _outstanding += bytes; }

    public void Acked(long bytes)
    {
        lock (_gate)
        {
            _outstanding = Math.Max(0, _outstanding - bytes);
            if (_outstanding <= _low) _free = false;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The page says it is not on screen: nobody is waiting to see this output.</summary>
    public void SetHidden(bool hidden) { lock (_gate) { _hidden = hidden; Monitor.PulseAll(_gate); } }

    /// <summary>The view has gone: never wait for it again.</summary>
    public void Close() { lock (_gate) { _closed = true; Monitor.PulseAll(_gate); } }

    /// <summary>Called on the pty's read thread after each chunk: waits while the view is too far behind.</summary>
    public void WaitForView()
    {
        lock (_gate)
        {
            if (_free || _hidden || _closed || _outstanding <= _high) return;
            var until = Environment.TickCount64 + (long)_maxWait.TotalMilliseconds;
            while (!_hidden && !_closed && _outstanding > _low)
            {
                var left = until - Environment.TickCount64;
                if (left <= 0) { _free = true; return; } // too slow to wait for: the program goes on
                Monitor.Wait(_gate, (int)left);
            }
        }
    }
}
