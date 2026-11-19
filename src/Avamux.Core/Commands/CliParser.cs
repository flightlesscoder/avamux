using System.Text;
using System.Text.Json.Nodes;

namespace Avamux.Core.Commands;

public sealed record CliParseResult(CommandDef? Command, JsonObject Args, bool Help, string? HelpTopic, bool Json, string? Error);

/// <summary>Turns `avamux terminal new --name build -- htop` into a command name plus JSON arguments.</summary>
public static class CliParser
{
    private static readonly HashSet<string> HelpWords = new(StringComparer.OrdinalIgnoreCase)
        { "/?", "-?", "--?", "/h", "-h", "/help", "-help", "--help", "help", "?" };

    public static bool IsHelp(string arg) => HelpWords.Contains(arg);

    public static CliParseResult Parse(CommandRegistry registry, IReadOnlyList<string> argv)
    {
        var args = argv.ToList();
        var json = args.RemoveAll(a => a == "--json") > 0;
        var help = false;
        // Help words count anywhere before a literal "--".
        var stop = args.IndexOf("--");
        for (var i = (stop < 0 ? args.Count : stop) - 1; i >= 0; i--)
            if (IsHelp(args[i])) { help = true; args.RemoveAt(i); }

        if (args.Count == 0) return new(null, new JsonObject(), true, null, json, null);

        // Longest command name that matches the leading words ("terminal new", "dashboard source add").
        CommandDef? def = null;
        var used = 0;
        for (var n = Math.Min(3, args.Count); n >= 1; n--)
        {
            def = registry.Find(string.Join(' ', args.Take(n)));
            if (def is not null) { used = n; break; }
        }
        if (def is null)
        {
            var topic = args[0];
            if (registry.Groups.Contains(topic, StringComparer.OrdinalIgnoreCase))
                return help || args.Count == 1
                    ? new(null, new JsonObject(), true, topic, json, null)
                    : new(null, new JsonObject(), false, topic, json, $"Unknown command '{string.Join(' ', args.Take(2))}'.");
            return new(null, new JsonObject(), help, null, json, help ? null : $"Unknown command '{topic}'.");
        }
        if (help) return new(def, new JsonObject(), true, def.Name, json, null);

        var result = new JsonObject();
        var positionals = def.Params.Where(p => p.Positional).ToList();
        var pi = 0;
        var literal = false;
        var rest = new List<string>();
        for (var i = used; i < args.Count; i++)
        {
            var a = args[i];
            if (!literal && a == "--") { literal = true; continue; }
            if (!literal && a.Length == 2 && a[0] == '-' && char.IsLetter(a[1]) && def.Params.FirstOrDefault(x => x.Short == a[1]) is { } sp)
            {
                if (sp.Flag) { result[sp.Name] = true; continue; }
                if (i + 1 >= args.Count) return new(def, result, false, def.Name, json, $"Option '{a}' needs a value.");
                result[sp.Name] = args[++i];
                continue;
            }
            if (!literal && a.StartsWith("--") && a.Length > 2)
            {
                var key = a[2..];
                string? value = null;
                var eq = key.IndexOf('=');
                if (eq > 0) { value = key[(eq + 1)..]; key = key[..eq]; }
                var negated = false;
                var p = def.Params.FirstOrDefault(x => !x.Positional && Same(x.Name, key));
                if (p is null && key.StartsWith("no-")) { p = def.Params.FirstOrDefault(x => x.Flag && Same(x.Name, key[3..])); negated = p is not null; }
                p ??= def.Params.FirstOrDefault(x => Same(x.Name, key));
                if (p is null) return new(def, result, false, def.Name, json, $"Unknown option '--{key}' for '{def.Name}'.");
                if (p.Flag)
                {
                    result[p.Name] = value is null ? !negated : value is "true" or "1" or "yes" or "on";
                    continue;
                }
                if (value is null)
                {
                    if (i + 1 >= args.Count) return new(def, result, false, def.Name, json, $"Option '--{key}' needs a value.");
                    value = args[++i];
                }
                if (p.Multi)
                {
                    if (result[p.Name] is not JsonArray arr) result[p.Name] = arr = new JsonArray();
                    arr.Add(value);
                }
                else result[p.Name] = value;
                continue;
            }
            // positional
            while (pi < positionals.Count && result.ContainsKey(positionals[pi].Name) && !positionals[pi].Rest) pi++;
            if (pi >= positionals.Count)
                return new(def, result, false, def.Name, json, $"Unexpected argument '{a}' for '{def.Name}'.");
            if (positionals[pi].Rest) rest.Add(a);
            else result[positionals[pi++].Name] = a;
        }
        var restParam = positionals.FirstOrDefault(p => p.Rest);
        if (restParam is not null && rest.Count > 0) result[restParam.Name] = new JsonArray(rest.Select(x => (JsonNode)x).ToArray());
        foreach (var p in def.Params.Where(p => p.Required))
            if (!result.ContainsKey(p.Name)) return new(def, result, false, def.Name, json, $"Missing required argument {(p.Positional ? $"<{p.Name}>" : "--" + p.Name)}.");
        return new(def, result, false, null, json, null);
    }

    private static bool Same(string a, string b) => string.Equals(Args.Normalize(a), Args.Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Usage(CommandDef c)
    {
        var sb = new StringBuilder("avamux " + c.Name);
        foreach (var p in c.Params.Where(p => p.Positional))
        {
            var n = p.Rest ? $"<{p.Name}...>" : $"<{p.Name}>";
            sb.Append(' ').Append(p.Required ? n : $"[{n}]");
        }
        if (c.Params.Any(p => !p.Positional)) sb.Append(" [options]");
        return sb.ToString();
    }

    public static string Help(CommandRegistry registry, string? topic)
    {
        var sb = new StringBuilder();
        var exact = topic is null ? null : registry.Find(topic);
        if (exact is not null)
        {
            sb.AppendLine(exact.Help).AppendLine().AppendLine("Usage:").AppendLine("  " + Usage(exact));
            var pos = exact.Params.Where(p => p.Positional).ToList();
            var opt = exact.Params.Where(p => !p.Positional).ToList();
            if (pos.Count > 0)
            {
                sb.AppendLine().AppendLine("Arguments:");
                foreach (var p in pos) sb.AppendLine($"  {("<" + p.Name + ">").PadRight(22)} {p.Help}{(p.Required ? "" : " (optional)")}");
            }
            if (opt.Count > 0)
            {
                sb.AppendLine().AppendLine("Options:");
                foreach (var p in opt)
                {
                    var label = (p.Short is { } c ? $"-{c}, " : "") + "--" + p.Name + (p.Flag ? "" : " <value>");
                    sb.AppendLine($"  {label.PadRight(22)} {p.Help}{(p.Required ? " (required)" : "")}{(p.Multi ? " (repeatable)" : "")}");
                }
            }
            sb.AppendLine().AppendLine("  --json                 Print the raw JSON result.");
            return sb.ToString();
        }

        var groups = registry.All.GroupBy(c => c.Group).OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var i && i < 0 ? 99 : i).ToList();
        if (topic is not null) groups = groups.Where(g => string.Equals(g.Key, topic, StringComparison.OrdinalIgnoreCase)).ToList();
        if (topic is null)
        {
            sb.AppendLine("avamux - terminals, watches, events, a dashboard, logs, a wiki and repositories in one window.");
            sb.AppendLine();
            sb.AppendLine("Usage:");
            sb.AppendLine("  avamux                      Start the app (or bring the running instance to the front).");
            sb.AppendLine("  avamux <command> [args]     Control the running instance.");
            sb.AppendLine("  avamux <command> /?         Help for one command. Also: -?, --?, -h, --help, /help, help.");
            sb.AppendLine("  avamux <group> /?           Help for a group, e.g. `avamux terminal /?`.");
            sb.AppendLine();
            sb.AppendLine("Add --json to any command for machine-readable output.");
            sb.AppendLine();
        }
        foreach (var g in groups)
        {
            sb.AppendLine($"{char.ToUpperInvariant(g.Key[0])}{g.Key[1..]} commands:");
            foreach (var c in g.OrderBy(c => c.Name))
                sb.AppendLine($"  {Usage(c)[7..].PadRight(46)} {c.Help}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static readonly string[] GroupOrder = ["app", "terminal", "browser", "settings", "watch", "event", "dashboard", "logs", "wiki", "repo", "page"];

    /// <summary>Human-friendly rendering of a command result for the terminal.</summary>
    public static string Format(JsonNode? result)
    {
        switch (result)
        {
            case null: return "";
            case JsonValue v: return v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
            case JsonArray arr when arr.Count == 0: return "(none)";
            case JsonArray arr when arr.All(x => x is JsonObject):
            {
                var rows = arr.Cast<JsonObject>().ToList();
                var cols = rows.SelectMany(r => r.Where(p => p.Value is null or JsonValue).Select(p => p.Key)).Distinct().ToList();
                if (cols.Count == 0) return JsonUtil.Pretty(result);
                string Cell(JsonObject r, string c)
                {
                    var t = r[c] is JsonValue jv ? (jv.TryGetValue<string>(out var str) ? str : jv.ToJsonString()) : "";
                    t = t.Replace("\r", "").Replace('\n', ' ');
                    return t.Length > 70 ? t[..69] + "…" : t;
                }
                var widths = cols.Select(c => Math.Max(c.Length, rows.Max(r => Cell(r, c).Length))).ToList();
                var sb = new StringBuilder();
                sb.AppendLine(string.Join("  ", cols.Select((c, i) => c.ToUpperInvariant().PadRight(widths[i]))).TrimEnd());
                foreach (var r in rows) sb.AppendLine(string.Join("  ", cols.Select((c, i) => Cell(r, c).PadRight(widths[i]))).TrimEnd());
                return sb.ToString().TrimEnd();
            }
            case JsonArray arr when arr.All(x => x is JsonValue): return string.Join('\n', arr.Select(x => Format(x)));
            case JsonObject o when o.All(p => p.Value is null or JsonValue):
            {
                var w = o.Max(p => p.Key.Length);
                return string.Join('\n', o.Select(p => $"{(p.Key + ":").PadRight(w + 1)} {Format(p.Value)}"));
            }
            default: return JsonUtil.Pretty(result);
        }
    }
}
