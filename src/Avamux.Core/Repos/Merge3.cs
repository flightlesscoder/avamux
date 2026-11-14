namespace Avamux.Core.Repos;

/// <summary>What a stretch of a three-way merge is: the same everywhere, changed on one side, changed alike on both, or changed differently on both.</summary>
public enum MergeChunkKind { Stable, Ours, Theirs, Same, Conflict }

/// <summary>What was decided about one side's change: nothing yet, put into the result, or left out.</summary>
public enum SideState { Pending, Applied, Rejected }

/// <summary>Where a line of the result came from: the common ancestor, one of the two sides, or typed by hand.</summary>
public enum MergeSource { Base, Ours, Theirs, Custom }

public readonly record struct MergeLine(MergeSource Source, int Index, string Text);

/// <summary>One stretch of a three-way merge, with the lines each version has there and what the result takes.</summary>
public sealed class MergeChunk
{
    public MergeChunkKind Kind { get; init; }
    public IReadOnlyList<string> Base { get; init; } = [];
    public IReadOnlyList<string> Ours { get; init; } = [];
    public IReadOnlyList<string> Theirs { get; init; } = [];
    /// <summary>Zero-based index of the chunk's first line in each version.</summary>
    public int BaseStart { get; init; }
    public int OursStart { get; init; }
    public int TheirsStart { get; init; }

    public SideState OursState { get; internal set; }
    public SideState TheirsState { get; internal set; }
    /// <summary>With both sides applied to a conflict: whether theirs comes before ours.</summary>
    public bool TheirsFirst { get; internal set; }
    /// <summary>Lines typed by hand for this chunk, which win over both sides.</summary>
    public IReadOnlyList<string>? Custom { get; internal set; }

    /// <summary>True for a change of either side, and for lines all versions share that were then edited by hand.</summary>
    public bool IsChange => Kind != MergeChunkKind.Stable || Custom is not null;
    public bool IsConflict => Kind == MergeChunkKind.Conflict;
    /// <summary>A conflict is settled once each side was either taken or left out, or the result was typed by hand.</summary>
    public bool Resolved => Kind != MergeChunkKind.Conflict || Custom is not null || (OursState != SideState.Pending && TheirsState != SideState.Pending);

    /// <summary>Whether a side has a change here that can be taken or left out.</summary>
    public bool Has(bool theirs) => Kind switch
    {
        MergeChunkKind.Ours => !theirs, MergeChunkKind.Theirs => theirs, MergeChunkKind.Same or MergeChunkKind.Conflict => true, _ => false,
    };

    public SideState State(bool theirs) => theirs ? TheirsState : OursState;

    /// <summary>The lines this chunk gives the result. An unsettled conflict gives the common ancestor's lines.</summary>
    public IEnumerable<MergeLine> Result()
    {
        if (Custom is not null) { for (var i = 0; i < Custom.Count; i++) yield return new(MergeSource.Custom, i, Custom[i]); yield break; }
        bool ours = OursState == SideState.Applied, theirs = TheirsState == SideState.Applied;
        if (Kind == MergeChunkKind.Stable || (!ours && !theirs))
        {
            for (var i = 0; i < Base.Count; i++) yield return new(MergeSource.Base, BaseStart + i, Base[i]);
            yield break;
        }
        // Both sides made the same change: it goes in once.
        if (Kind == MergeChunkKind.Same) theirs = !ours;
        for (var pass = 0; pass < 2; pass++)
        {
            var takeTheirs = (pass == 0) == TheirsFirst;
            if (takeTheirs && theirs) for (var i = 0; i < Theirs.Count; i++) yield return new(MergeSource.Theirs, TheirsStart + i, Theirs[i]);
            if (!takeTheirs && ours) for (var i = 0; i < Ours.Count; i++) yield return new(MergeSource.Ours, OursStart + i, Ours[i]);
        }
    }
}

/// <summary>
/// A three-way merge of one file, chunk by chunk. Changes only one side made start out applied; changes both sides
/// made differently are conflicts, which wait for a decision. Nothing here touches git or the disk.
/// </summary>
public sealed class MergeDoc
{
    private const int MaxEdits = 2500;   // beyond this many differing lines, a stretch is treated as wholly replaced

    public IReadOnlyList<MergeChunk> Chunks { get; private set; } = [];
    public IReadOnlyList<string> BaseLines { get; private init; } = [];
    public IReadOnlyList<string> OursLines { get; private init; } = [];
    public IReadOnlyList<string> TheirsLines { get; private init; } = [];
    public string Eol { get; private init; } = "\n";
    public bool FinalNewline { get; private init; } = true;
    /// <summary>False when git had no common ancestor for the file (both sides added it): one was worked out from what the sides share.</summary>
    public bool HasBase { get; private init; } = true;

    public int Conflicts => Chunks.Count(c => c.IsConflict);
    public int Unresolved => Chunks.Count(c => !c.Resolved);
    public bool Done => Unresolved == 0;

    public static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public static MergeDoc Create(string? baseText, string oursText, string theirsText)
    {
        string[] ours = Lines(oursText), theirs = Lines(theirsText);
        string[] bas;
        if (baseText is not null) bas = Lines(baseText);
        else
        {
            // No ancestor: what both sides have in common stands in for it, so only the differences are in question.
            var common = Match(ours, theirs);
            bas = ours.Where((_, i) => common[i] >= 0).ToArray();
        }
        int[] toOurs = Match(bas, ours), toTheirs = Match(bas, theirs);
        var chunks = new List<MergeChunk>();
        int b = 0, o = 0, t = 0;
        while (b < bas.Length || o < ours.Length || t < theirs.Length)
        {
            // A run of lines that all three versions share.
            var run = 0;
            while (b + run < bas.Length && toOurs[b + run] == o + run && toTheirs[b + run] == t + run) run++;
            if (run > 0)
            {
                chunks.Add(new MergeChunk { Kind = MergeChunkKind.Stable, Base = Slice(bas, b, run), Ours = Slice(ours, o, run), Theirs = Slice(theirs, t, run), BaseStart = b, OursStart = o, TheirsStart = t });
                b += run; o += run; t += run;
                continue;
            }
            // Otherwise everything up to the next line that both sides kept is one change.
            var next = b;
            while (next < bas.Length && (toOurs[next] < 0 || toTheirs[next] < 0)) next++;
            int oEnd = next < bas.Length ? toOurs[next] : ours.Length, tEnd = next < bas.Length ? toTheirs[next] : theirs.Length;
            var (cb, co, ct) = (Slice(bas, b, next - b), Slice(ours, o, oEnd - o), Slice(theirs, t, tEnd - t));
            var kind = Same(co, cb) ? MergeChunkKind.Theirs : Same(ct, cb) ? MergeChunkKind.Ours : Same(co, ct) ? MergeChunkKind.Same : MergeChunkKind.Conflict;
            chunks.Add(new MergeChunk
            {
                Kind = kind, Base = cb, Ours = co, Theirs = ct, BaseStart = b, OursStart = o, TheirsStart = t,
                OursState = kind switch { MergeChunkKind.Conflict => SideState.Pending, MergeChunkKind.Theirs => SideState.Rejected, _ => SideState.Applied },
                TheirsState = kind switch { MergeChunkKind.Conflict => SideState.Pending, MergeChunkKind.Ours => SideState.Rejected, _ => SideState.Applied },
            });
            b = next; o = oEnd; t = tEnd;
        }
        return new MergeDoc
        {
            Chunks = chunks, BaseLines = bas, OursLines = ours, TheirsLines = theirs, HasBase = baseText is not null,
            Eol = oursText.Contains("\r\n") || (oursText.Length == 0 && theirsText.Contains("\r\n")) ? "\r\n" : "\n",
            FinalNewline = oursText.EndsWith('\n') || theirsText.EndsWith('\n') || (oursText.Length == 0 && theirsText.Length == 0),
        };
    }

    // ---- decisions ----

    /// <summary>Puts one side's change into the result. In a conflict, taking the second side as well appends it after the first.</summary>
    public void Apply(MergeChunk c, bool theirs)
    {
        if (!c.Has(theirs)) return;
        c.Custom = null;
        if (c.Kind == MergeChunkKind.Same) { c.OursState = c.TheirsState = SideState.Applied; return; }
        var other = c.State(!theirs);
        if (c.State(theirs) != SideState.Applied) c.TheirsFirst = other == SideState.Applied ? !theirs : theirs;
        Set(c, theirs, SideState.Applied);
        if (c.IsConflict && other == SideState.Pending) Set(c, !theirs, SideState.Rejected);
    }

    /// <summary>Leaves one side's change out of the result.</summary>
    public void Reject(MergeChunk c, bool theirs)
    {
        if (!c.Has(theirs)) return;
        c.Custom = null;
        if (c.Kind == MergeChunkKind.Same) { c.OursState = c.TheirsState = SideState.Rejected; return; }
        Set(c, theirs, SideState.Rejected);
    }

    /// <summary>Takes both sides of a chunk, in the order given.</summary>
    public void ApplyBoth(MergeChunk c, bool theirsFirst)
    {
        if (!c.IsConflict) return;
        c.Custom = null; c.OursState = c.TheirsState = SideState.Applied; c.TheirsFirst = theirsFirst;
    }

    /// <summary>Replaces what the chunk gives the result with lines typed by hand.</summary>
    public void Edit(MergeChunk c, string text) { if (c.IsChange) c.Custom = Lines(text); }

    // ---- free editing ----

    private const string OursMarker = "<<<<<<< ", MiddleMarker = "=======", TheirsMarker = ">>>>>>> ";

    /// <summary>What a chunk contributes to the text that is edited freely: its result, or both sides between markers while it is open.</summary>
    private static List<string> Editable(MergeChunk c, string oursLabel, string theirsLabel)
    {
        if (c.Resolved) return c.Result().Select(l => l.Text).ToList();
        return [OursMarker + oursLabel, .. c.Ours, MiddleMarker, .. c.Theirs, TheirsMarker + theirsLabel];
    }

    /// <summary>The result as text to edit freely, in the tool or in an editor: conflicts still open are between git's markers.</summary>
    public string EditableText(string oursLabel, string theirsLabel) => ResultText(true, oursLabel, theirsLabel);

    /// <summary>
    /// Takes an edited copy of <see cref="EditableText"/> back in. Every chunk the edit did not touch keeps its
    /// decision. A change or conflict that was edited takes the typed lines as its result (a conflict only once
    /// its markers are gone); lines all versions share that were edited become a hand-made change of their own,
    /// which can be undone like any other. Returns false when nothing differed.
    /// </summary>
    public bool ApplyEdited(string text, string oursLabel, string theirsLabel)
    {
        var parts = Chunks.Select(c => Editable(c, oursLabel, theirsLabel)).ToList();
        var old = parts.SelectMany(p => p).ToArray();
        var edited = Lines(text);
        if (Same(old, edited)) return false;
        var map = Match(old, edited);

        // Where each chunk starts in the edited text. Lines typed between two chunks go to the earlier one, unless
        // it ends on a line that survived the edit: then they belong to what follows.
        var starts = new int[Chunks.Count + 1];
        starts[Chunks.Count] = edited.Length;
        var at = 0;
        for (var i = 0; i < Chunks.Count; i++)
        {
            if (i > 0)
            {
                var before = -1;
                for (var k = at - 1; k >= 0 && before < 0; k--) if (map[k] >= 0) before = map[k];
                starts[i] = at < old.Length && map[at] >= 0 ? map[at] : before + 1;
            }
            at += parts[i].Count;
        }

        var chunks = new List<MergeChunk>();
        for (var i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            var now = new ArraySegment<string>(edited, starts[i], Math.Max(0, starts[i + 1] - starts[i]));
            if (Same(parts[i], now)) { chunks.Add(c); continue; }
            if (c.Kind == MergeChunkKind.Stable)
            {
                // Only the lines that were actually edited leave the stable run.
                int head = 0, tail = 0;
                while (head < c.Base.Count && head < now.Count && c.Base[head] == now[head]) head++;
                while (tail < c.Base.Count - head && tail < now.Count - head && c.Base[^(tail + 1)] == now[^(tail + 1)]) tail++;
                MergeChunk Part(int from, int count, IReadOnlyList<string>? custom) => new()
                {
                    Kind = MergeChunkKind.Stable, Custom = custom, BaseStart = c.BaseStart + from, OursStart = c.OursStart + from, TheirsStart = c.TheirsStart + from,
                    Base = c.Base.Skip(from).Take(count).ToArray(), Ours = c.Ours.Skip(from).Take(count).ToArray(), Theirs = c.Theirs.Skip(from).Take(count).ToArray(),
                };
                var middle = c.Base.Count - head - tail;
                if (head > 0) chunks.Add(Part(0, head, null));
                chunks.Add(Part(head, middle, now.Skip(head).Take(now.Count - head - tail).ToArray()));
                if (tail > 0) chunks.Add(Part(head + middle, tail, null));
                continue;
            }
            // A conflict that still has a marker in it is not settled yet: it stays open as it was.
            if (!c.Resolved && now.Any(l => l.StartsWith(OursMarker, StringComparison.Ordinal) || l == MiddleMarker || l.StartsWith(TheirsMarker, StringComparison.Ordinal))) { chunks.Add(c); continue; }
            c.Custom = now.ToArray();
            chunks.Add(c);
        }
        Chunks = Tidy(chunks);
        return true;
    }

    /// <summary>Joins neighbouring runs of untouched common lines, and drops hand edits that put the original lines back.</summary>
    private static List<MergeChunk> Tidy(List<MergeChunk> chunks)
    {
        var result = new List<MergeChunk>();
        foreach (var c in chunks)
        {
            if (c.Kind == MergeChunkKind.Stable && c.Custom is not null && Same(c.Custom, c.Base)) c.Custom = null;
            if (c.Kind == MergeChunkKind.Stable && c.Custom is null && c.Base.Count == 0) continue;
            if (c.Kind == MergeChunkKind.Stable && c.Custom is null && result.Count > 0 && result[^1] is { Kind: MergeChunkKind.Stable, Custom: null } last)
            {
                result[^1] = new MergeChunk
                {
                    Kind = MergeChunkKind.Stable, BaseStart = last.BaseStart, OursStart = last.OursStart, TheirsStart = last.TheirsStart,
                    Base = [.. last.Base, .. c.Base], Ours = [.. last.Ours, .. c.Ours], Theirs = [.. last.Theirs, .. c.Theirs],
                };
                continue;
            }
            result.Add(c);
        }
        return result;
    }

    /// <summary>Puts a chunk back the way the merge started.</summary>
    public void Reset(MergeChunk c)
    {
        c.Custom = null; c.TheirsFirst = false;
        c.OursState = c.Kind switch { MergeChunkKind.Conflict => SideState.Pending, MergeChunkKind.Theirs => SideState.Rejected, _ => SideState.Applied };
        c.TheirsState = c.Kind switch { MergeChunkKind.Conflict => SideState.Pending, MergeChunkKind.Ours => SideState.Rejected, _ => SideState.Applied };
    }

    public void ResetAll() { foreach (var c in Chunks) if (c.IsChange) Reset(c); Chunks = Tidy(Chunks.ToList()); }

    // ---- differences inside a line ----

    /// <summary>A line as words, runs of blanks and single punctuation marks.</summary>
    private static List<string> Words(string line)
    {
        var words = new List<string>();
        for (var i = 0; i < line.Length;)
        {
            var j = i + 1;
            if (char.IsLetterOrDigit(line[i]) || line[i] == '_') while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
            else if (char.IsWhiteSpace(line[i])) while (j < line.Length && char.IsWhiteSpace(line[j])) j++;
            words.Add(line[i..j]);
            i = j;
        }
        return words;
    }

    /// <summary>
    /// The stretches of <paramref name="to"/> that <paramref name="from"/> does not have, as (start, end) character
    /// positions. Empty when the lines are equal, and when they have so little in common that marking words would
    /// only repeat that the whole line changed.
    /// </summary>
    public static List<(int Start, int End)> WordChanges(string from, string to) => Compare(from, to, 3).Ranges;

    /// <summary>The marked stretches of <paramref name="to"/>, and how much of it (0 to 1) <paramref name="from"/> also has.</summary>
    private static (List<(int Start, int End)> Ranges, double Alike) Compare(string from, string to, int least)
    {
        var ranges = new List<(int Start, int End)>();
        if (from == to) return (ranges, 1);
        if (from.Length == 0 || to.Length == 0) return (ranges, 0);
        string[] a = [.. Words(from)], b = [.. Words(to)];
        var map = Match(b, a);
        int pos = 0, kept = 0, solid = 0;
        for (var i = 0; i < b.Length; i++)
        {
            var blank = string.IsNullOrWhiteSpace(b[i]);
            if (!blank) solid += b[i].Length;
            if (map[i] >= 0) { if (!blank) kept += b[i].Length; }
            else if (ranges.Count > 0 && ranges[^1].End == pos) ranges[^1] = (ranges[^1].Start, pos + b[i].Length);
            else ranges.Add((pos, pos + b[i].Length));
            pos += b[i].Length;
        }
        // Words of the other line that are gone leave no stretch to mark here; a line that only lost words has none.
        return solid == 0 || kept * least < solid ? ([], 0) : (ranges, (double)kept / solid);
    }

    /// <summary>
    /// For each line of a changed side, the stretches that differ from the line it replaced. Each line is compared
    /// with the line it most resembles among those it could have replaced, keeping their order; a line that
    /// resembles none of them is wholly new and gets nothing.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)>?[] WordChanges(IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        var result = new IReadOnlyList<(int Start, int End)>?[to.Count];
        var next = 0;
        for (var i = 0; i < to.Count && next < from.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(to[i])) continue;
            var best = -1;
            (List<(int Start, int End)> Ranges, double Alike) found = ([], 0);
            // Looking a few lines ahead is enough to step over lines one side added or removed.
            for (var j = next; j < Math.Min(from.Count, next + 8); j++)
            {
                var c = Compare(from[j], to[i], 2);
                if (c.Alike > found.Alike) { found = c; best = j; }
                if (c.Alike >= 1) break;
            }
            if (best < 0) continue;
            next = best + 1;
            if (found.Ranges.Count > 0) result[i] = found.Ranges;
        }
        return result;
    }

    /// <summary>Settles conflicts with one side: only the ones still open, or all of them. Changes that did not conflict stay as they are.</summary>
    public void AcceptConflicts(bool theirs, bool onlyUnresolved)
    {
        foreach (var c in Chunks.Where(c => c.IsConflict && (!onlyUnresolved || !c.Resolved)))
        {
            c.Custom = null; c.TheirsFirst = theirs;
            Set(c, theirs, SideState.Applied); Set(c, !theirs, SideState.Rejected);
        }
    }

    /// <summary>Makes the result one side's version of the file exactly, dropping every change of the other side.</summary>
    public void AcceptFile(bool theirs)
    {
        foreach (var c in Chunks.Where(c => c.IsChange))
        {
            c.Custom = null; c.TheirsFirst = theirs;
            if (c.Kind == MergeChunkKind.Same) { c.OursState = c.TheirsState = SideState.Applied; continue; }
            Set(c, theirs, c.Has(theirs) ? SideState.Applied : SideState.Rejected);
            Set(c, !theirs, SideState.Rejected);
        }
    }

    private static void Set(MergeChunk c, bool theirs, SideState s) { if (theirs) c.TheirsState = s; else c.OursState = s; }

    // ---- result ----

    public IEnumerable<string> ResultLines() => Chunks.SelectMany(c => c.Result().Select(l => l.Text));

    /// <summary>The merged file. With <paramref name="markers"/>, conflicts still open are written the way git writes them.</summary>
    public string ResultText(bool markers = false, string oursLabel = "ours", string theirsLabel = "theirs")
    {
        var lines = new List<string>();
        foreach (var c in Chunks)
        {
            if (markers && !c.Resolved)
            {
                lines.Add("<<<<<<< " + oursLabel); lines.AddRange(c.Ours);
                lines.Add("======="); lines.AddRange(c.Theirs);
                lines.Add(">>>>>>> " + theirsLabel);
            }
            else lines.AddRange(c.Result().Select(l => l.Text));
        }
        return lines.Count == 0 ? "" : string.Join(Eol, lines) + (FinalNewline ? Eol : "");
    }

    // ---- line matching ----

    private static IReadOnlyList<string> Slice(string[] a, int start, int count) => count == 0 ? [] : new ArraySegment<string>(a, start, count);

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// For every line of <paramref name="a"/>, the index of the line of <paramref name="b"/> it pairs with in a
    /// longest common subsequence, or -1. Myers' algorithm, after trimming what the two share at either end.
    /// </summary>
    public static int[] Match(string[] a, string[] b)
    {
        var map = new int[a.Length];
        Array.Fill(map, -1);
        var pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) { map[pre] = pre; pre++; }
        int ea = a.Length, eb = b.Length;
        while (ea > pre && eb > pre && a[ea - 1] == b[eb - 1]) { ea--; eb--; map[ea] = eb; }
        int n = ea - pre, m = eb - pre;
        if (n == 0 || m == 0) return map;

        // Lines as numbers, so the inner loop compares integers.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s) { if (!ids.TryGetValue(s, out var id)) ids[s] = id = ids.Count; return id; }
        var x1 = new int[n]; var y1 = new int[m];
        for (var i = 0; i < n; i++) x1[i] = Id(a[pre + i]);
        for (var i = 0; i < m; i++) y1[i] = Id(b[pre + i]);

        int max = Math.Min(n + m, MaxEdits), off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            // Remember the furthest points reached with d-1 edits: the way back needs them.
            var snap = new int[2 * d + 3];
            Array.Copy(v, off - d - 1, snap, 0, snap.Length);
            trace.Add(snap);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && x1[x] == y1[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return map;   // too different: nothing in the middle is paired

        int cx = n, cy = m;
        for (var d = found; d >= 0; d--)
        {
            var snap = trace[d];
            int At(int k) => snap[k + d + 1];
            var k = cx - cy;
            var pk = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            int px = At(pk), py = px - pk;
            while (cx > px && cy > py) { cx--; cy--; map[pre + cx] = pre + cy; }
            if (d > 0) { cx = px; cy = py; }
        }
        return map;
    }
}
