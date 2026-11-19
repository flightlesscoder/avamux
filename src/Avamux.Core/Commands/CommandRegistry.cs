using System.Text.Json.Nodes;
using Avamux.Core.Logging;

namespace Avamux.Core.Commands;

/// <param name="Positional">Filled from bare arguments, in declaration order.</param>
/// <param name="Rest">Positional that swallows all remaining arguments (joined with spaces).</param>
/// <param name="Flag">Boolean switch that takes no value.</param>
/// <param name="Multi">May be repeated; collected into an array.</param>
/// <param name="Short">One-letter form (-i), for options scripts commonly abbreviate.</param>
public sealed record CommandParam(string Name, string Help, bool Required = false, bool Positional = false, bool Rest = false, bool Flag = false, bool Multi = false, char? Short = null);

public sealed record CommandDef(string Name, string Help, CommandParam[] Params, Func<Args, Task<JsonNode?>> Handler)
{
    public string Group => Name.Split(' ')[0];
}

/// <summary>Typed access to a command's JSON arguments.</summary>
public sealed class Args(JsonObject raw)
{
    public JsonObject Raw => raw;

    private JsonNode? Node(string name) =>
        raw.FirstOrDefault(p => string.Equals(Normalize(p.Key), Normalize(name), StringComparison.OrdinalIgnoreCase)).Value;

    internal static string Normalize(string s) => s.Replace("-", "").Replace("_", "");

    public bool Has(string name) => Node(name) is not null;

    public string? Str(string name)
    {
        var n = Node(name);
        return n switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray a => string.Join(' ', a.Select(x => x?.ToString())),
            _ => n.ToString(),
        };
    }

    public string Require(string name) => Str(name) is { Length: > 0 } s ? s : throw new ArgumentException($"Missing required argument '{name}'.");

    public int? Int(string name) => Str(name) is { } s ? int.TryParse(s, out var i) ? i : throw new ArgumentException($"'{name}' must be a whole number.") : null;

    public double? Num(string name) => Str(name) is { } s
        ? double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"'{name}' must be a number.") : null;

    public bool? Bool(string name)
    {
        var n = Node(name);
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return n.ToString().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new ArgumentException($"'{name}' must be true or false."),
        };
    }

    public bool Flag(string name) => Bool(name) ?? false;

    public List<string> List(string name) => Node(name) switch
    {
        null => [],
        JsonArray a => a.Select(x => x?.ToString() ?? "").ToList(),
        var n => [n.ToString()],
    };

    public DateTime? Time(string name) => Str(name) is { } s ? TimeParsing.ParseDateTime(s) : null;
}

public static class TimeParsing
{
    /// <summary>Accepts ISO dates/times, "today 17:00", "tomorrow", "now", and relative "+90m".</summary>
    public static DateTime ParseDateTime(string text)
    {
        var t = text.Trim();
        var lower = t.ToLowerInvariant();
        if (lower == "now") return DateTime.Now;
        if (lower.StartsWith('+')) return DateTime.Now + ParseDuration(lower[1..]);
        foreach (var (word, day) in new[] { ("today", DateTime.Today), ("tomorrow", DateTime.Today.AddDays(1)) })
        {
            if (!lower.StartsWith(word)) continue;
            var rest = t[word.Length..].Trim();
            return rest.Length == 0 ? day : day + ParseClock(rest);
        }
        if (TimeSpan.TryParse(t, out _) && t.Contains(':') && !t.Contains('-') && !t.Contains('/'))
        {
            var at = DateTime.Today + ParseClock(t);
            return at < DateTime.Now ? at.AddDays(1) : at;
        }
        if (DateTime.TryParse(t, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dt)) return dt;
        if (DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out dt)) return dt;
        throw new ArgumentException($"Could not understand the date/time '{text}'. Try 2026-12-24T18:00, \"tomorrow 9:00\" or +90m.");
    }

    private static TimeSpan ParseClock(string s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var d)
            ? d.TimeOfDay : throw new ArgumentException($"Could not understand the time '{s}'.");

    /// <summary>"90s", "5m", "1h30m", "2d", "1:30:00", or a bare number of minutes.</summary>
    public static TimeSpan ParseDuration(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) throw new ArgumentException("Empty duration.");
        if (double.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var minutes)) return TimeSpan.FromMinutes(minutes);
        if (t.Contains(':') && TimeSpan.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var ts)) return ts;
        var total = TimeSpan.Zero;
        var num = "";
        foreach (var c in t)
        {
            if (char.IsDigit(c) || c == '.') { num += c; continue; }
            if (char.IsWhiteSpace(c)) continue;
            if (num.Length == 0) throw new ArgumentException($"Could not understand the duration '{text}'. Try 90s, 5m, 1h30m or 2d.");
            var v = double.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
            total += c switch
            {
                's' => TimeSpan.FromSeconds(v), 'm' => TimeSpan.FromMinutes(v), 'h' => TimeSpan.FromHours(v), 'd' => TimeSpan.FromDays(v), 'w' => TimeSpan.FromDays(7 * v),
                _ => throw new ArgumentException($"Unknown duration unit '{c}' in '{text}'."),
            };
            num = "";
        }
        if (num.Length > 0) total += TimeSpan.FromMinutes(double.Parse(num, System.Globalization.CultureInfo.InvariantCulture));
        return total;
    }
}

/// <summary>Every operation avamux exposes. The CLI, the IPC socket and the dashboard JavaScript API all go through this.</summary>
public sealed partial class CommandRegistry
{
    private readonly Dictionary<string, CommandDef> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CommandDef> All => _commands.Values.ToList();
    public IEnumerable<string> Groups => _commands.Values.Select(c => c.Group).Distinct();

    public void Add(string name, string help, CommandParam[] parameters, Func<Args, Task<JsonNode?>> handler) =>
        _commands[name] = new CommandDef(name, help, parameters, handler);

    public void Add(string name, string help, CommandParam[] parameters, Func<Args, JsonNode?> handler) =>
        Add(name, help, parameters, a => Task.FromResult(handler(a)));

    public CommandDef? Find(string name) => _commands.GetValueOrDefault(name.Trim().Replace('.', ' '));

    /// <summary>Runs a command and wraps the outcome as {"ok":true,"result":…} or {"ok":false,"error":"…"}.</summary>
    public async Task<JsonObject> ExecuteAsync(string name, JsonObject args)
    {
        var def = Find(name);
        if (def is null) return new JsonObject { ["ok"] = false, ["error"] = $"Unknown command '{name}'." };
        try
        {
            var a = new Args(args);
            foreach (var p in def.Params.Where(p => p.Required))
                if (!a.Has(p.Name)) throw new ArgumentException($"Missing required argument '{p.Name}'.");
            var result = await def.Handler(a);
            return new JsonObject { ["ok"] = true, ["result"] = result };
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException ag ? ag.GetBaseException() : ex;
            if (inner is not (ArgumentException or KeyNotFoundException or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException))
                Log.Error("command", $"'{name}' failed", inner);
            return new JsonObject { ["ok"] = false, ["error"] = inner.Message };
        }
    }

    public JsonArray Describe() => new(_commands.Values.OrderBy(c => c.Name).Select(c => (JsonNode)new JsonObject
    {
        ["command"] = c.Name,
        ["help"] = c.Help,
        ["params"] = new JsonArray(c.Params.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name, ["help"] = p.Help, ["required"] = p.Required, ["positional"] = p.Positional, ["flag"] = p.Flag, ["multi"] = p.Multi,
        }).ToArray()),
    }).ToArray());

    private static CommandParam P(string name, string help, bool required = false) => new(name, help, required, Positional: true);
    private static CommandParam Rest(string name, string help, bool required = false) => new(name, help, required, Positional: true, Rest: true);
    private static CommandParam O(string name, string help, bool required = false) => new(name, help, required);
    private static CommandParam F(string name, string help, char? shortName = null) => new(name, help, Flag: true, Short: shortName);
    private static CommandParam M(string name, string help) => new(name, help, Multi: true);
    private static JsonNode? J<T>(T value) => JsonUtil.ToNode(value);

    /// <summary>Builds the registry. <paramref name="s"/> may be null when only help text is needed.</summary>
    public static CommandRegistry Build(AppServices? s)
    {
        var r = new CommandRegistry();
        r.AddAppCommands(s!);
        r.AddTerminalCommands(s!);
        r.AddBrowserCommands(s!);
        r.AddSettingsCommands(s!);
        r.AddWatchCommands(s!);
        r.AddEventCommands(s!);
        r.AddDashboardCommands(s!);
        r.AddLogCommands(s!);
        r.AddWikiCommands(s!);
        r.AddRepoCommands(s!);
        r.AddActivityCommands(s!);
        r.AddSearchCommands(s!);
        r.AddPageCommands(s!);
        return r;
    }
}
