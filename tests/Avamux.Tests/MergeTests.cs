using Avamux.Core;
using Avamux.Core.Config;
using Avamux.Core.Repos;

namespace Avamux.Tests;

public class MergeTests
{
    private static string T(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void Changes_of_one_side_apply_themselves_and_only_real_clashes_conflict()
    {
        var doc = MergeDoc.Create(T("a", "b", "c", "d", "e", "f", "g"), T("a", "B1", "c", "d", "e", "f", "G1"), T("a", "b", "c", "D2", "e", "f", "G2"));
        Assert.Equal([MergeChunkKind.Stable, MergeChunkKind.Ours, MergeChunkKind.Stable, MergeChunkKind.Theirs, MergeChunkKind.Stable, MergeChunkKind.Conflict], doc.Chunks.Select(c => c.Kind));
        Assert.Equal(1, doc.Conflicts);
        Assert.Equal(1, doc.Unresolved);
        Assert.False(doc.Done);

        var conflict = doc.Chunks[^1];
        doc.Apply(conflict, theirs: false);
        Assert.True(doc.Done);
        Assert.Equal(T("a", "B1", "c", "D2", "e", "f", "G1"), doc.ResultText());

        // Taking the other side as well appends it; leaving a side out takes it away again.
        doc.Apply(conflict, theirs: true);
        Assert.Equal(T("a", "B1", "c", "D2", "e", "f", "G1", "G2"), doc.ResultText());
        doc.Reject(conflict, theirs: false);
        Assert.Equal(T("a", "B1", "c", "D2", "e", "f", "G2"), doc.ResultText());
        doc.Reject(conflict, theirs: true);
        Assert.True(doc.Done);
        Assert.Equal(T("a", "B1", "c", "D2", "e", "f", "g"), doc.ResultText());
    }

    [Fact]
    public void A_change_that_did_not_conflict_can_be_left_out_and_put_back()
    {
        var doc = MergeDoc.Create(T("a", "b", "c"), T("a", "b", "c"), T("a", "x", "y", "c"));
        var change = doc.Chunks.Single(c => c.IsChange);
        Assert.Equal(MergeChunkKind.Theirs, change.Kind);
        Assert.True(doc.Done);
        Assert.Equal(T("a", "x", "y", "c"), doc.ResultText());
        doc.Reject(change, theirs: true);
        Assert.Equal(T("a", "b", "c"), doc.ResultText());
        doc.Apply(change, theirs: true);
        Assert.Equal(T("a", "x", "y", "c"), doc.ResultText());
        // The side that changed nothing here has nothing to take.
        doc.Apply(change, theirs: false);
        Assert.Equal(T("a", "x", "y", "c"), doc.ResultText());
    }

    [Fact]
    public void The_same_change_on_both_sides_goes_in_once()
    {
        var doc = MergeDoc.Create(T("a", "b"), T("a", "b", "new"), T("a", "b", "new"));
        Assert.Equal(MergeChunkKind.Same, doc.Chunks[^1].Kind);
        Assert.Equal(T("a", "b", "new"), doc.ResultText());
        doc.Reject(doc.Chunks[^1], theirs: true);
        Assert.Equal(T("a", "b"), doc.ResultText());
    }

    [Fact]
    public void Accepting_a_side_settles_conflicts_or_takes_the_whole_file()
    {
        string bas = T("1", "2", "3", "4", "5"), ours = T("1", "two", "3", "4", "FIVE"), theirs = T("1", "2", "3", "four", "cinq");
        var doc = MergeDoc.Create(bas, ours, theirs);
        doc.AcceptConflicts(theirs: true, onlyUnresolved: true);
        Assert.True(doc.Done);
        Assert.Equal(T("1", "two", "3", "four", "cinq"), doc.ResultText());

        doc.AcceptFile(theirs: false);
        Assert.Equal(ours, doc.ResultText());
        doc.AcceptFile(theirs: true);
        Assert.Equal(theirs, doc.ResultText());

        doc.ResetAll();
        Assert.Equal(1, doc.Unresolved);
        doc.ApplyBoth(doc.Chunks.Single(c => c.IsConflict), theirsFirst: true);
        Assert.Equal(T("1", "two", "3", "four", "cinq", "4", "FIVE"), doc.ResultText());
        doc.Edit(doc.Chunks.Single(c => c.IsConflict), "by hand\n");
        Assert.True(doc.Done);
        Assert.Equal(T("1", "two", "3", "by hand"), doc.ResultText());
    }

    [Fact]
    public void Open_conflicts_can_be_written_with_markers_and_line_endings_are_kept()
    {
        var doc = MergeDoc.Create("a\r\nb\r\n", "a\r\nours\r\n", "a\r\ntheirs\r\n");
        Assert.Equal("a\r\n<<<<<<< main\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> topic\r\n", doc.ResultText(true, "main", "topic"));
        doc.Apply(doc.Chunks[^1], theirs: true);
        Assert.Equal("a\r\ntheirs\r\n", doc.ResultText());
    }

    [Fact]
    public void Without_an_ancestor_only_what_differs_is_in_question()
    {
        var doc = MergeDoc.Create(null, T("head", "mine", "tail"), T("head", "yours", "tail"));
        Assert.False(doc.HasBase);
        Assert.Equal([MergeChunkKind.Stable, MergeChunkKind.Conflict, MergeChunkKind.Stable], doc.Chunks.Select(c => c.Kind));
    }

    [Fact]
    public void An_edited_result_comes_back_in_without_losing_decisions()
    {
        var doc = MergeDoc.Create(T("a", "b", "c", "d", "e", "f", "g", "h"), T("a", "B1", "c", "d", "e", "f", "G1", "h"), T("a", "b", "c", "D2", "e", "f", "G2", "h"));
        var text = doc.EditableText("main", "topic");
        Assert.Equal(T("a", "B1", "c", "D2", "e", "f", "<<<<<<< main", "G1", "=======", "G2", ">>>>>>> topic", "h"), text);
        Assert.False(doc.ApplyEdited(text, "main", "topic"));

        // A line everyone shares is edited, and a line is added at the end; the conflict is left alone.
        Assert.True(doc.ApplyEdited(T("a", "B1", "c", "D2", "E!", "f", "<<<<<<< main", "G1", "=======", "G2", ">>>>>>> topic", "h", "tail"), "main", "topic"));
        Assert.Equal(1, doc.Unresolved);
        Assert.Equal(SideState.Applied, doc.Chunks.Single(c => c.Kind == MergeChunkKind.Ours).OursState);
        var edits = doc.Chunks.Where(c => c.Kind == MergeChunkKind.Stable && c.Custom is not null).ToList();
        Assert.Equal(2, edits.Count);
        Assert.Equal(["e"], edits[0].Base);
        Assert.Equal(["E!"], edits[0].Custom);
        Assert.Empty(edits[1].Base);
        Assert.Equal(["tail"], edits[1].Custom);

        // The earlier decisions still work, and the text is still what was typed.
        doc.Reject(doc.Chunks.Single(c => c.Kind == MergeChunkKind.Theirs), theirs: true);
        Assert.Equal(T("a", "B1", "c", "d", "E!", "f", "<<<<<<< main", "G1", "=======", "G2", ">>>>>>> topic", "h", "tail"), doc.EditableText("main", "topic"));

        // Removing the markers settles the conflict with what is left between them.
        Assert.True(doc.ApplyEdited(T("a", "B1", "c", "d", "E!", "f", "G1 and G2", "h", "tail"), "main", "topic"));
        Assert.True(doc.Done);
        Assert.Equal(["G1 and G2"], doc.Chunks.Single(c => c.IsConflict).Custom);
        Assert.Equal(T("a", "B1", "c", "d", "E!", "f", "G1 and G2", "h", "tail"), doc.ResultText());

        // Undoing a hand edit puts the shared line back; starting over forgets all of them.
        doc.Reset(edits[0]);
        Assert.Equal(T("a", "B1", "c", "d", "e", "f", "G1 and G2", "h", "tail"), doc.ResultText());
        doc.ResetAll();
        Assert.Equal(1, doc.Unresolved);
        Assert.DoesNotContain(doc.Chunks, c => c.Custom is not null);
        Assert.Equal(T("a", "B1", "c", "D2", "e", "f", "<<<<<<< main", "G1", "=======", "G2", ">>>>>>> topic", "h"), doc.EditableText("main", "topic"));
    }

    [Fact]
    public void Whatever_is_typed_the_result_is_exactly_that_text()
    {
        var rnd = new Random(23);
        for (var round = 0; round < 200; round++)
        {
            var bas = Enumerable.Range(0, 20).Select(i => "line " + i).ToList();
            List<string> Mutate(List<string> from, string tag)
            {
                var l = from.ToList();
                for (var k = 0; k < 4; k++)
                {
                    var at = rnd.Next(0, l.Count + 1);
                    switch (rnd.Next(3))
                    {
                        case 0: l.Insert(at, tag + rnd.Next(100)); break;
                        case 1: if (at < l.Count) l.RemoveAt(at); break;
                        default: if (at < l.Count) l[at] = tag + rnd.Next(100); break;
                    }
                }
                return l;
            }
            var doc = MergeDoc.Create(T(bas.ToArray()), T(Mutate(bas, "ours ").ToArray()), T(Mutate(bas, "theirs ").ToArray()));
            doc.AcceptConflicts(theirs: rnd.Next(2) == 0, onlyUnresolved: true);
            // Two rounds of edits, the second on top of the first.
            for (var pass = 0; pass < 2; pass++)
            {
                var typed = T(Mutate(MergeDoc.Lines(doc.ResultText()).ToList(), "typed ").ToArray());
                doc.ApplyEdited(typed, "o", "t");
                Assert.True(doc.Done);
                Assert.Equal(typed, doc.ResultText());
            }
        }
    }

    [Fact]
    public void Changed_words_inside_a_line_are_found()
    {
        string from = "        var total = x + 3;", to = "        var total = Math.Abs(x) + 3;";
        var marks = MergeDoc.WordChanges(from, to);
        Assert.Equal(["Math.Abs(", ")"], marks.Select(m => to[m.Start..m.End]));
        Assert.Empty(MergeDoc.WordChanges(from, from));
        // Lines with next to nothing in common are not marked word by word.
        Assert.Empty(MergeDoc.WordChanges("return total * 2;", "Console.WriteLine(\"hello\");"));
        var lines = MergeDoc.WordChanges(["a = 1", "b = 2"], ["a = 10", "b = 2", "c = 3"]);
        Assert.Equal(3, lines.Length);
        Assert.Equal([(4, 6)], lines[0]);
        Assert.Null(lines[1]);
        Assert.Null(lines[2]);
    }

    [Fact]
    public void Line_matching_finds_a_longest_common_subsequence()
    {
        var rnd = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => rnd.Next(0, 6).ToString()).ToArray();
            var b = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => rnd.Next(0, 6).ToString()).ToArray();
            var map = MergeDoc.Match(a, b);
            var pairs = map.Select((to, from) => (from, to)).Where(p => p.to >= 0).ToList();
            Assert.All(pairs, p => Assert.Equal(a[p.from], b[p.to]));
            for (var i = 1; i < pairs.Count; i++) Assert.True(pairs[i].to > pairs[i - 1].to);
            // The classic table gives the length to beat.
            var t = new int[a.Length + 1, b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
            Assert.Equal(t[a.Length, b.Length], pairs.Count);
        }
    }

    [Fact]
    public void A_merge_of_random_edits_with_one_side_accepted_gives_that_sides_file()
    {
        var rnd = new Random(11);
        for (var round = 0; round < 100; round++)
        {
            var bas = Enumerable.Range(0, 25).Select(i => "line " + i).ToList();
            List<string> Mutate()
            {
                var l = bas.ToList();
                for (var k = 0; k < 4; k++)
                {
                    var at = rnd.Next(0, l.Count + 1);
                    switch (rnd.Next(3))
                    {
                        case 0: l.Insert(at, "new " + rnd.Next(100)); break;
                        case 1: if (at < l.Count) l.RemoveAt(at); break;
                        default: if (at < l.Count) l[at] = "changed " + rnd.Next(100); break;
                    }
                }
                return l;
            }
            string ours = T(Mutate().ToArray()), theirs = T(Mutate().ToArray());
            var doc = MergeDoc.Create(T(bas.ToArray()), ours, theirs);
            doc.AcceptFile(theirs: false);
            Assert.Equal(ours, doc.ResultText());
            doc.AcceptFile(theirs: true);
            Assert.Equal(theirs, doc.ResultText());
        }
    }

    [Fact]
    public async Task A_real_merge_conflict_is_found_resolved_and_finished()
    {
        using var box = new Sandbox();
        var dir = box.Work("repo");
        Directory.CreateDirectory(dir);
        async Task<string> Git(params string[] args) { var r = await Shell.RunAsync("git", args, dir); Assert.True(r.ExitCode == 0, r.Stderr + r.Stdout); return r.Stdout.Trim(); }
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.com");
        await Git("config", "user.name", "T");
        await Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), T("one", "two", "three", "four", "five", "six"));
        File.WriteAllText(Path.Combine(dir, "gone.txt"), "x\n");
        await Git("add", "-A"); await Git("commit", "-qm", "base");
        await Git("switch", "-qc", "topic");
        File.WriteAllText(Path.Combine(dir, "a.txt"), T("one", "TWO theirs", "three", "four", "five", "six", "seven"));
        File.WriteAllText(Path.Combine(dir, "gone.txt"), "changed\n");
        await Git("commit", "-qam", "topic");
        await Git("switch", "-q", "main");
        File.WriteAllText(Path.Combine(dir, "a.txt"), T("zero", "one", "TWO ours", "three", "four", "five", "six"));
        await Git("rm", "-q", "gone.txt");
        await Git("commit", "-qam", "main");

        using var config = new ConfigService(box.Paths);
        var repos = new RepoService(box.Paths, config);
        var repo = await repos.AddAsync(dir);
        Assert.False((await repos.MergeStateAsync(repo)).Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repos.MergeAsync(repo, "topic"));

        var state = await repos.MergeStateAsync(repo);
        Assert.Equal(MergeOp.Merge, state.Op);
        Assert.Equal("main", state.OursLabel);
        Assert.Equal("topic", state.TheirsLabel);
        Assert.Equal(["a.txt", "gone.txt"], state.Conflicts.Order());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repos.ContinueMergeAsync(repo));

        var file = await repos.ConflictAsync(repo, "a.txt");
        Assert.True(file.Mergeable);
        var doc = MergeDoc.Create(file.Base, file.Ours!, file.Theirs!);
        Assert.Equal(1, doc.Unresolved);
        doc.Apply(doc.Chunks.Single(c => c.IsConflict), theirs: true);
        await repos.ResolveAsync(repo, "a.txt", doc.ResultText());
        Assert.Equal(T("zero", "one", "TWO theirs", "three", "four", "five", "six", "seven"), File.ReadAllText(Path.Combine(dir, "a.txt")));

        // Deleted on our side, changed on theirs: one side has to win.
        var gone = await repos.ConflictAsync(repo, "gone.txt");
        Assert.False(gone.Mergeable);
        Assert.Null(gone.Ours);
        await repos.ResolveWithSideAsync(repo, "gone.txt", theirs: false);
        Assert.False(File.Exists(Path.Combine(dir, "gone.txt")));

        state = await repos.MergeStateAsync(repo);
        Assert.True(state.InProgress);
        Assert.Empty(state.Conflicts);
        Assert.False((await repos.ContinueMergeAsync(repo)).Active);
        Assert.Equal(3, (await Git("rev-list", "--parents", "-n", "1", "HEAD")).Split(' ').Length);   // a merge commit: itself and two parents
        Assert.Equal("", await Git("status", "--porcelain"));
    }

    [Fact]
    public async Task An_aborted_merge_puts_the_repository_back()
    {
        using var box = new Sandbox();
        var dir = box.Work("repo");
        Directory.CreateDirectory(dir);
        async Task<string> Git(params string[] args) { var r = await Shell.RunAsync("git", args, dir); Assert.True(r.ExitCode == 0, r.Stderr + r.Stdout); return r.Stdout.Trim(); }
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.com");
        await Git("config", "user.name", "T");
        await Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "base\n");
        await Git("add", "-A"); await Git("commit", "-qm", "base");
        await Git("switch", "-qc", "topic");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "theirs\n");
        await Git("commit", "-qam", "topic");
        await Git("switch", "-q", "main");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "ours\n");
        await Git("commit", "-qam", "main");

        using var config = new ConfigService(box.Paths);
        var repos = new RepoService(box.Paths, config);
        var repo = await repos.AddAsync(dir);
        // A rebase swaps the sides: what is being rebased is "theirs".
        await Git("switch", "-q", "topic");
        Assert.NotEqual(0, (await Shell.RunAsync("git", ["rebase", "main"], dir)).ExitCode);
        var state = await repos.MergeStateAsync(repo);
        Assert.Equal(MergeOp.Rebase, state.Op);
        Assert.Equal("topic", state.TheirsLabel);
        Assert.Single(state.Conflicts);
        Assert.Equal("ours\n", (await repos.ConflictAsync(repo, "a.txt")).Ours);
        await repos.AbortMergeAsync(repo);
        Assert.False((await repos.MergeStateAsync(repo)).Active);
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(dir, "a.txt")));
    }
}
