using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Avamux.Core.Diagnostics;

/// <summary>
/// Milestones of the app's life with the time each was reached, in milliseconds since the process started (the
/// runtime's own start-up included): the main window's first frame, the first terminal output, the first wiki page
/// rendered, the steps of shutting down. `avamux app timeline` lists them; with AVAMUX_TIMELINE_FILE set they are
/// also written to that file as the app exits, so the steps of shutting down can be read afterwards. The startup
/// performance tests (tests/Avamux.Tests/StartupTests.cs) read them to see where time goes.
/// </summary>
public static class Timeline
{
    public sealed record Mark(string Name, double Ms, string? Detail);

    private static readonly DateTime ProcessStart = StartTime();
    private static readonly List<Mark> Marks = [];
    private const int MaxMarks = 5000;

    private static DateTime StartTime()
    {
        try { return Process.GetCurrentProcess().StartTime; }
        catch { return DateTime.Now; }
    }

    /// <summary>Milliseconds since the process started.</summary>
    public static double Now => (DateTime.Now - ProcessStart).TotalMilliseconds;

    /// <summary>Notes that something happened now. Things that happen again (each wiki render) are noted every time.</summary>
    public static void Note(string name, string? detail = null)
    {
        var mark = new Mark(name, Math.Round(Now, 1), detail);
        lock (Marks)
        {
            if (Marks.Count >= MaxMarks) Marks.RemoveRange(0, MaxMarks / 10);
            Marks.Add(mark);
        }
    }

    /// <summary>Notes something only the first time it happens (the first terminal output, say).</summary>
    public static void NoteOnce(string name, string? detail = null)
    {
        lock (Marks) if (Marks.Any(m => m.Name == name)) return;
        Note(name, detail);
    }

    public static IReadOnlyList<Mark> All() { lock (Marks) return Marks.ToList(); }

    public static JsonArray ToJson() => new(All().Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["ms"] = m.Ms, ["detail"] = m.Detail }).ToArray());

    /// <summary>Writes the marks to AVAMUX_TIMELINE_FILE, if set. Called as the app exits.</summary>
    public static void Save()
    {
        if (Environment.GetEnvironmentVariable("AVAMUX_TIMELINE_FILE") is not { Length: > 0 } file) return;
        try { File.WriteAllText(file, ToJson().ToJsonString()); } catch { }
    }
}
