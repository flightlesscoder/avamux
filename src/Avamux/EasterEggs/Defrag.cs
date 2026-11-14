using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avamux.EasterEggs;

/// <summary>DEFRAG.EXE: a text-mode disk optimiser that shuffles blocks of drive A: into order.</summary>
public sealed class Defrag : DosProgram
{
    private enum B : byte { Free, Used, Reading, Writing, Bad, Fixed }
    private const int MapX = 1, MapY = 2, MapW = 78, MapH = 14, Count = MapW * MapH;
    private readonly B[] _map = new B[Count];
    private readonly TextGrid _grid = new();
    private double _t, _phaseT;
    private int _phase; // 0 analysing, 1 picking, 2 reading, 3 writing, 4 done
    private List<int> _from = [], _to = [];
    private int _moved, _toMove;
    private int _cluster;

    public override void Start()
    {
        var at = 0;
        _map[at++] = B.Fixed; _map[at++] = B.Fixed; _map[at++] = B.Fixed;
        while (at < Count)
        {
            // Runs of used and free blocks, more used near the start, like a disk that has been used for years.
            var used = Rnd.NextDouble() < 0.62 - 0.25 * at / Count;
            var run = Rnd.Next(1, used ? 9 : 7);
            for (var i = 0; i < run && at < Count; i++) _map[at++] = used ? B.Used : B.Free;
        }
        for (var i = 0; i < 6; i++) _map[Rnd.Next(40, Count)] = B.Bad;
        for (var i = 0; i < 3; i++) _map[Rnd.Next(200, Count)] = B.Fixed;
        _toMove = CountMisplaced();
        _grid.CursorVisible = false;
    }

    private int CountMisplaced()
    {
        var used = _map.Count(b => b == B.Used);
        var n = 0;
        for (int i = 0, seen = 0; i < Count && seen < used; i++)
        {
            if (_map[i] is B.Used) seen++;
            else if (_map[i] == B.Free) n++;
        }
        return Math.Max(1, n);
    }

    public override void Update(double dt)
    {
        _t += dt;
        _phaseT += dt;
        switch (_phase)
        {
            case 0 when _phaseT > 1.8: Next(1); break;
            case 1: Pick(); break;
            case 2 when _phaseT > 0.07:
                foreach (var i in _from) _map[i] = B.Free;
                foreach (var i in _to) _map[i] = B.Writing;
                Next(3);
                break;
            case 3 when _phaseT > 0.07:
                foreach (var i in _to) _map[i] = B.Used;
                _moved += _to.Count;
                Next(1);
                break;
        }
    }

    private void Next(int phase) { _phase = phase; _phaseT = 0; }

    /// <summary>Takes the last few used blocks and moves them into the first gap.</summary>
    private void Pick()
    {
        var gap = Array.FindIndex(_map, b => b == B.Free);
        var last = Array.FindLastIndex(_map, b => b == B.Used);
        if (gap < 0 || last < gap) { Next(4); DosScreen.Beep(); return; }
        _to = [];
        for (var i = gap; i < Count && _to.Count < 6 && _map[i] == B.Free; i++) _to.Add(i);
        _from = [];
        for (var i = last; i > gap && _from.Count < _to.Count && _map[i] == B.Used; i--) _from.Add(i);
        if (_from.Count < _to.Count) _to = _to.Take(_from.Count).ToList();
        foreach (var i in _from) _map[i] = B.Reading;
        _cluster = _from[0] * 2 + 33;
        Next(2);
    }

    public override void KeyDown(Key key, KeyModifiers mods)
    {
        if (_phase == 4 && key is Key.Enter or Key.Space) Finish();
    }

    public override void TextInput(string text)
    {
        if (_phase == 4) Finish();
    }

    private void Finish()
    {
        ExitMessage = "\nDrive A: is now 0% fragmented.\n";
        Done = true;
    }

    public override void Draw(DrawingContext g)
    {
        var grid = _grid;
        grid.Fill(0, 0, 80, 25, ' ', 15, 1);
        // Menu bar and title.
        grid.Fill(0, 0, 80, 1, ' ', 0, 7);
        grid.Text(1, 0, "Optimize  Analyze  File  Help", 0, 7);
        grid.Text(1, 0, "O", 4, 7); grid.Text(11, 0, "A", 4, 7); grid.Text(20, 0, "F", 4, 7); grid.Text(26, 0, "H", 4, 7);
        grid.Text(58, 0, "AVA-DOS Defragmenter", 0, 7);
        grid.Text(MapX, 1, "Drive A:  FUN PACK VOL. 1", 11, 1);

        for (var i = 0; i < Count; i++)
        {
            var (c, fg, bg) = _map[i] switch
            {
                B.Free => ('░', 7, 1), B.Used => ('■', 15, 1), B.Reading => ('r', 0, 10), B.Writing => ('W', 15, 4),
                B.Bad => ('B', 12, 1), _ => ('X', 14, 1),
            };
            grid.Put(MapX + i % MapW, MapY + i / MapW, c, fg, bg);
        }

        // Status box.
        grid.Box(1, 17, 46, 7, 0, 7, "Status");
        var pct = _phase == 4 ? 100 : Math.Min(99, _moved * 100 / _toMove);
        grid.Text(3, 18, _phase == 0 ? "Reading drive information..." : $"Cluster {_cluster:N0}", 0, 7);
        grid.Text(37, 18, $"{pct,3}%", 0, 7);
        var bar = 40 * pct / 100;
        grid.Fill(3, 19, 40, 1, '░', 8, 7);
        grid.Fill(3, 19, bar, 1, '█', 1, 7);
        var elapsed = TimeSpan.FromSeconds(_t);
        grid.Text(3, 20, $"Elapsed Time: {elapsed:hh\\:mm\\:ss}", 0, 7);
        grid.Text(3, 21, "Full Optimization", 0, 7);
        grid.Text(26, 21, _phase == 4 ? "complete" : "in progress", 0, 7);

        // Legend box.
        grid.Box(48, 17, 31, 7, 0, 7, "Legend");
        grid.Put(50, 18, '■', 15, 1); grid.Text(52, 18, "- Used", 0, 7);
        grid.Put(64, 18, '░', 7, 1); grid.Text(66, 18, "- Unused", 0, 7);
        grid.Put(50, 19, 'r', 0, 10); grid.Text(52, 19, "- Reading", 0, 7);
        grid.Put(64, 19, 'W', 15, 4); grid.Text(66, 19, "- Writing", 0, 7);
        grid.Put(50, 20, 'B', 12, 1); grid.Text(52, 20, "- Bad", 0, 7);
        grid.Put(64, 20, 'X', 14, 1); grid.Text(66, 20, "- Unmovable", 0, 7);
        grid.Text(50, 22, "1 block = 2 clusters", 0, 7);

        // Bottom line.
        grid.Fill(0, 24, 80, 1, ' ', 0, 7);
        grid.Text(1, 24, _phase switch { 0 => "Analyzing...", 2 => "Reading...", 3 => "Writing...", 4 => "Finished.", _ => "Optimizing..." }, 0, 7);
        grid.Text(52, 24, "│ Esc=Stop Defrag", 0, 7);

        if (_phase == 4)
        {
            grid.Box(22, 7, 36, 8, 0, 7, "Finished Condensing");
            grid.Text(25, 9, "Optimization of drive A: is", 0, 7);
            grid.Text(25, 10, "complete. Every block is home.", 0, 7);
            var blink = _t % 1 < 0.6;
            grid.Text(35, 12, "<  OK  >", blink ? 15 : 0, blink ? 1 : 7);
        }
        grid.Draw(g, false);
    }
}
