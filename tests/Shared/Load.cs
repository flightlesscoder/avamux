using System.Diagnostics;
using System.Text;

namespace Avamux.TestSupport;

/// <summary>
/// Load tests: big pages, very long lines, thousands of files. They are slow and opt in: they run only when
/// AVAMUX_LOAD_TESTS=1 (see scripts/load-tests.sh) and are skipped otherwise. Each measurement is written to the
/// test output and, when AVAMUX_LOAD_REPORT names a file, added to that file as a row of a markdown table, so a run
/// leaves a report of where time and memory go. A measurement over its budget fails its test: a budget is what an
/// interactive app can afford there, so a failure marks a performance gap or a capacity limit to look at.
/// </summary>
public static class Load
{
    public const string Why = "Load test: opt in with AVAMUX_LOAD_TESTS=1 (scripts/load-tests.sh)";

    public static bool On => Environment.GetEnvironmentVariable("AVAMUX_LOAD_TESTS") is "1" or "true" or "yes";

    /// <summary>Scales every size the load tests use (AVAMUX_LOAD_SCALE, default 1): 0.1 for a quick run, 10 to push harder.</summary>
    public static double Scale => double.TryParse(Environment.GetEnvironmentVariable("AVAMUX_LOAD_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;

    public static int N(int count) => Math.Max(1, (int)(count * Scale));

    public sealed record Sample(double Ms, long AllocatedBytes, long ManagedBytes, long WorkingSetBytes);

    private static readonly object Gate = new();

    /// <summary>Runs <paramref name="work"/> once and measures it: wall time, bytes allocated, memory held afterwards.</summary>
    public static Sample Measure(Action work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        work();
        watch.Stop();
        var after = GC.GetTotalAllocatedBytes(true);
        return new Sample(watch.Elapsed.TotalMilliseconds, after - allocated, GC.GetTotalMemory(false), Process.GetCurrentProcess().WorkingSet64);
    }

    /// <summary>Records a measurement and says whether it kept to its budget (null: no budget, only reported).</summary>
    public static (bool Ok, string Line) Report(string test, string what, Sample s, double? budgetMs = null, string? note = null)
    {
        var ok = budgetMs is not { } b || s.Ms <= b;
        static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        // Timings taken from another process (the startup tests) have no memory figures: leave those cells empty.
        var memory = s.AllocatedBytes == 0 && s.ManagedBytes == 0 && s.WorkingSetBytes == 0 ? " |  |  | " : $" {Mb(s.AllocatedBytes)} | {Mb(s.ManagedBytes)} | {Mb(s.WorkingSetBytes)} | ";
        var line = $"| {test} | {what} | {s.Ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} | {(budgetMs is { } bm ? bm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "")} |{memory}{(ok ? "ok" : "**over budget**")}{(note is null ? "" : " " + note)} |";
        if (Environment.GetEnvironmentVariable("AVAMUX_LOAD_REPORT") is { Length: > 0 } file)
            lock (Gate)
            {
                if (!File.Exists(file))
                    File.WriteAllText(file, "| Test | Measure | ms | Budget ms | Allocated MB | Managed MB after | Process MB | Result |\n|---|---|---:|---:|---:|---:|---:|---|\n");
                File.AppendAllText(file, line + "\n");
            }
        return (ok, line);
    }

    // ---- content ----

    private static readonly string[] Words = "the quick brown fox jumps over a lazy dog while seven wizards quietly hex jovial dwarves near forty bright lanterns".Split(' ');

    /// <summary>
    /// A realistic markdown page of about <paramref name="chars"/> characters: headings, paragraphs, lists, a table,
    /// code blocks, links and wiki links; with <paramref name="pictureEvery"/> &gt; 0, a picture line every that many
    /// lines, cycling through <paramref name="pictures"/>.
    /// </summary>
    public static string Markdown(int chars, int pictureEvery = 0, IReadOnlyList<string>? pictures = null)
    {
        var sb = new StringBuilder(chars + 1024);
        var rnd = new Random(42);
        var line = 0;
        var section = 0;
        void Text(int words)
        {
            for (var i = 0; i < words; i++) sb.Append(i == 0 ? "" : " ").Append(Words[rnd.Next(Words.Length)]);
        }
        while (sb.Length < chars)
        {
            var kind = line % 40;
            if (pictureEvery > 0 && pictures is { Count: > 0 } && line % pictureEvery == pictureEvery - 1)
                sb.Append("![picture ").Append(line).Append("](").Append(pictures[line / pictureEvery % pictures.Count]).Append(')');
            else if (kind == 0) sb.Append("## Section ").Append(++section);
            else if (kind is 5 or 6 or 7) { sb.Append("- "); Text(8); }
            else if (kind == 10) sb.Append("| a | b | c |\n|---|---|---|\n| 1 | two | [link](https://example.com/").Append(line).Append(") |");
            else if (kind == 20) { sb.Append("```csharp\nvar x = ").Append(line).Append(";\nConsole.WriteLine(x);\n```"); }
            else if (kind == 30) { sb.Append("See [[Page ").Append(line % 97).Append("]] and `code ").Append(line).Append("` for **more** _detail_."); }
            else Text(14);
            sb.Append('\n');
            line++;
        }
        return sb.ToString();
    }
}
