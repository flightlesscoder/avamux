using System.Text.RegularExpressions;

namespace Avamux.Core.Workloads;

/// <summary>A start script as <see cref="ScriptCatalog"/> read it, for a <see cref="ScriptConvention"/> to look at.</summary>
/// <param name="StartScript">The start-&lt;name&gt;.sh file.</param>
/// <param name="Folder">The folder it is in (what <c>$(dirname "${BASH_SOURCE[0]}")</c> means in it).</param>
/// <param name="Text">The script's text.</param>
/// <param name="Vars">The plain variables it and the files it sources set, expanded.</param>
public sealed record ScriptFacts(string StartScript, string Folder, string Text, IReadOnlyDictionary<string, string> Vars);

/// <summary>
/// A convention some start scripts follow (the shared files they source, how they name their pid and log files …),
/// added by a plugin: it fills in or changes what was read into the profile, or returns false to leave the script out.
/// </summary>
public delegate bool ScriptConvention(ScriptFacts script, WorkloadProfile profile);

/// <summary>
/// Turns folders of start-&lt;name&gt;.sh / stop-&lt;name&gt;.sh pairs into script profiles. The scripts are the
/// config: their plain variables (BIN, MODEL, PORT, CTX, ALIAS, PID_FILE, LOG_FILE, RPC_HOST, RPC_PORT) are read from
/// the text, as is what the files they source set. Nothing is run to find out. A start script without a stop script,
/// or one that needs arguments to do anything (it prints usage without them), is left out. Plugins add conventions
/// (<see cref="ScriptConvention"/>) for scripts that keep their settings elsewhere.
/// </summary>
public static partial class ScriptCatalog
{
    [GeneratedRegex(@"^\s*(?:export\s+|local\s+)?([A-Z][A-Z0-9_]*)=(.*)$", RegexOptions.Multiline)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^\s*(?:source|\.)\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Source();

    [GeneratedRegex(@"\$\{?([A-Za-z_][A-Za-z0-9_]*|[0-9])(?::-([^}]*))?\}?")]
    private static partial Regex Reference();

    public static List<WorkloadProfile> Discover(IEnumerable<string> folders, IEnumerable<ScriptConvention>? conventions = null)
    {
        var list = new List<WorkloadProfile>();
        var each = conventions?.ToList() ?? [];
        foreach (var folder in folders.Select(AiPath).Where(Directory.Exists))
            foreach (var start in Directory.GetFiles(folder, "start-*.sh").Order())
                if (Read(start, each) is { } p) list.Add(p);
        return list;
    }

    private static string AiPath(string path) => Ai.AiService.ExpandHome(path);

    public static WorkloadProfile? Read(string startScript, IReadOnlyList<ScriptConvention>? conventions = null)
    {
        var folder = Path.GetDirectoryName(startScript)!;
        var file = Path.GetFileName(startScript);
        var stop = Path.Combine(folder, "stop-" + file["start-".Length..]);
        if (!File.Exists(stop)) return null;
        string text;
        try { text = File.ReadAllText(startScript); } catch { return null; }
        // Scripts that need an argument to know what to start (they print usage and exit without one) have no single thing to offer.
        if (Regex.IsMatch(text, @"\$\{1:\?|\[\[\s*-z\s+""?\$\{?1\}?""?\s*\]\]\s*&&\s*(usage|die)", RegexOptions.IgnoreCase)) return null;

        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var here = folder;
        foreach (Match src in Source().Matches(text))
        {
            var raw = src.Groups[1].Value;
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1];
            var target = Expand(raw.Replace("$(dirname \"${BASH_SOURCE[0]}\")", here).Replace("$(dirname \"$0\")", here), vars);
            if (!Path.IsPathRooted(target)) target = Path.Combine(here, target);
            if (File.Exists(target)) Assign(File.ReadAllText(target), vars, here);
        }
        var firstArgIsContext = Regex.IsMatch(text, @"^\s*CTX=""?\$\{1:-", RegexOptions.Multiline);
        Assign(text, vars, here);

        var name = file["start-".Length..^3];
        var p = new WorkloadProfile
        {
            Id = "script:" + name, Kind = WorkloadProfile.ScriptKind, Name = Pretty(name), Discovered = true,
            StartScript = startScript, StopScript = stop, Folder = folder,
            Binary = vars.GetValueOrDefault("BIN") ?? "", Model = vars.GetValueOrDefault("MODEL") ?? "",
            Alias = vars.GetValueOrDefault("ALIAS") ?? "",
        };
        p.Port = int.TryParse(vars.GetValueOrDefault("PORT"), out var port) ? port : 0;
        p.PidFile = vars.GetValueOrDefault("PID_FILE") ?? "";
        p.LogFile = vars.GetValueOrDefault("LOG_FILE") ?? "";
        p.ContextArg = firstArgIsContext;
        if (long.TryParse(vars.GetValueOrDefault("CTX"), out var c)) p.Context = c;
        if (vars.GetValueOrDefault("RPC_HOST") is { Length: > 0 } rh) p.RpcAddress = $"{rh}:{vars.GetValueOrDefault("RPC_PORT") ?? "50052"}";
        if (conventions is { Count: > 0 })
        {
            var facts = new ScriptFacts(startScript, folder, text, vars);
            foreach (var convention in conventions)
            {
                try { if (!convention(facts, p)) return null; }
                catch (Exception ex) { Logging.Log.Warn("workloads", $"A plugin could not read {file}: {ex.Message}"); }
            }
        }
        return p;
    }

    private static void Assign(string text, Dictionary<string, string> vars, string here)
    {
        foreach (Match m in Assignment().Matches(text))
        {
            var key = m.Groups[1].Value;
            var raw = m.Groups[2].Value.Trim();
            // Only plain values: arrays, command output and multi-step logic are not config.
            if (raw.StartsWith('(')) continue;
            if (raw.Contains("$(") && !raw.Contains("dirname") && !raw.Contains("BASH_SOURCE")) continue;
            raw = raw.Replace("$(cd \"$(dirname \"${BASH_SOURCE[0]}\")\" && pwd)", here).Replace("$(dirname \"${BASH_SOURCE[0]}\")", here).Replace("$(dirname \"$0\")", here);
            if (raw.Length > 0 && raw[0] is '"' or '\'' && raw.IndexOf(raw[0], 1) is var close and > 0) raw = raw[1..close]; // "value"   # a comment
            else if (raw.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0) raw = raw[..hash].Trim();
            vars[key] = Expand(raw, vars);
        }
    }

    private static string Expand(string value, Dictionary<string, string> vars) =>
        Reference().Replace(value, m =>
        {
            var name = m.Groups[1].Value;
            if (name == "1" || char.IsDigit(name[0])) return m.Groups[2].Success ? m.Groups[2].Value : "";
            return vars.TryGetValue(name, out var v) ? v : m.Groups[2].Success ? m.Groups[2].Value : m.Value;
        });

    private static string Pretty(string name) => name.Replace('-', ' ');
}
