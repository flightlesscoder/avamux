using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avamux.Core.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Avamux.Core.Theming;

/// <summary>What a colour token is for, so editors and theme files can explain it.</summary>
public sealed record ThemeToken(string Name, string Group, string Description);

/// <summary>A theme file that could not be read. <see cref="Problems"/> lists every mistake found, not just the first.</summary>
public sealed class ThemeFileException(IReadOnlyList<string> problems) : Exception(string.Join(Environment.NewLine, problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Reads and writes themes as JSON or YAML. A file names the theme and gives any of its colours; whatever it
/// leaves out comes from its <c>base</c> theme (Simple unless it says otherwise). See <see cref="Reference"/>.
/// </summary>
public static class ThemeFile
{
    public static readonly IReadOnlyList<ThemeToken> Tokens =
    [
        new("bg", "Surfaces", "Page background"),
        new("bgDeep", "Surfaces", "Navigation bar and the deepest backgrounds"),
        new("bgEditor", "Surfaces", "Editors, lists and text areas"),
        new("bgSide", "Surfaces", "Side panels"),
        new("panel", "Surfaces", "Cards, toolbars and dialogs"),
        new("panelHi", "Surfaces", "Hovered or raised panels, tooltips"),
        new("cell", "Surfaces", "Rows and cells in lists and calendars"),
        new("cellSel", "Surfaces", "Selected row or cell"),
        new("line", "Lines", "Borders and separators"),
        new("lineSoft", "Lines", "Faint separators between rows"),
        new("lineStrong", "Lines", "Emphasised borders, field outlines"),
        new("scroll", "Lines", "Scroll bar thumbs"),
        new("text", "Text", "Body text"),
        new("textStrong", "Text", "Headings and emphasised text"),
        new("textSoft", "Text", "Secondary text"),
        new("textMute", "Text", "Muted labels"),
        new("textCode", "Text", "Code and monospaced text"),
        new("dim", "Text", "Hints and descriptions"),
        new("faint", "Text", "Timestamps and least important text"),
        new("ghost", "Text", "Placeholders and disabled text"),
        new("accent", "Accent", "Accent: selection, focus, primary buttons"),
        new("link", "Accent", "Links"),
        new("linkHi", "Accent", "Hovered links"),
        new("accentDeep", "Accent", "Darker accent for filled areas"),
        new("onAccent", "Accent", "Text drawn on the accent colour"),
        new("ok", "Status", "Success"),
        new("okHi", "Status", "Success, highlighted"),
        new("warn", "Status", "Warnings"),
        new("error", "Status", "Errors"),
        new("errorHi", "Status", "Errors, highlighted"),
        new("errorText", "Status", "Error messages"),
        new("purple", "Status", "Purple highlights (tags, kinds)"),
        new("cyan", "Status", "Cyan highlights"),
        new("orange", "Status", "Orange highlights"),
        new("addBg", "Diffs", "Added lines"),
        new("delBg", "Diffs", "Removed lines"),
        new("hunkBg", "Diffs", "Hunk headers"),
        new("termBg", "Terminal", "Terminal background"),
        new("termFg", "Terminal", "Terminal text"),
        new("termSel", "Terminal", "Terminal selection"),
        new("titleBg", "Title bar", "Title bar background (avamux-drawn window frames)"),
        new("titleFg", "Title bar", "Title bar text"),
    ];

    public static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    ];

    public static readonly string[] Extensions = [".yaml", ".yml", ".json"];

    private static readonly string[] Keys = ["name", "description", "base", "light", "radius", "chrome", "sounds", "bevel", "font", "derive", "accent", "colors", "ansi"];

    public static bool IsThemeFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>"json" when the text looks like JSON, otherwise "yaml".</summary>
    public static string DetectFormat(string text) => text.TrimStart().StartsWith('{') ? "json" : "yaml";

    public static string FormatOf(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";

    /// <summary>Turns JSON or YAML into a tree. YAML scalars all come back as strings; the reader below accepts either.</summary>
    public static JsonNode ReadTree(string text, string format)
    {
        if (format == "json")
        {
            try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? throw new ThemeFileException(["The file is empty."]); }
            catch (JsonException ex) { throw new ThemeFileException([$"Line {(ex.LineNumber ?? 0) + 1}: {ex.Message.Split(" Path:")[0].Split(" LineNumber:")[0]}"]); }
        }
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0) throw new ThemeFileException(["The file is empty."]);
            return Convert(stream.Documents[0].RootNode) ?? throw new ThemeFileException(["The file is empty."]);
        }
        catch (YamlException ex)
        {
            var message = (ex.InnerException ?? ex).Message;
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            throw new ThemeFileException([$"Line {ex.Start.Line}, column {ex.Start.Column}: {(at >= 0 ? message[(at + 3)..] : message)}"]);
        }
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? "", Convert(kv.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode { Value: null or "" or "~" or "null", Style: ScalarStyle.Plain } => null,
        YamlScalarNode s => JsonValue.Create(s.Value),
        _ => null,
    };

    /// <summary>Reads a theme from text. <paramref name="lookup"/> finds the theme named by <c>base</c>.</summary>
    public static ThemeDef Parse(string text, string id, string? format = null, Func<string, ThemeDef?>? lookup = null) =>
        Parse(ReadTree(text, format ?? DetectFormat(text)), id, lookup);

    public static ThemeDef Parse(JsonNode tree, string id, Func<string, ThemeDef?>? lookup = null)
    {
        lookup ??= Themes.TryFind;
        var problems = new List<string>();
        if (tree is not JsonObject root) throw new ThemeFileException(["A theme file must be a set of keys (name: …, colors: …), not a list or a single value."]);
        foreach (var key in root.Select(kv => kv.Key).Where(k => !Keys.Contains(k)))
            problems.Add($"'{key}' is not a theme setting. Known: {string.Join(", ", Keys)}.");

        var baseName = Str(root, "base");
        var theme = baseName is null && Themes.IsBuiltinId(id) ? Themes.Builtin.First(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) : Themes.Simple;
        if (baseName is not null)
        {
            if (string.Equals(baseName, id, StringComparison.OrdinalIgnoreCase) && Themes.IsBuiltinId(id)) theme = Themes.Builtin.First(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
            else if (string.Equals(baseName, id, StringComparison.OrdinalIgnoreCase)) problems.Add("base: a theme cannot be based on itself.");
            else theme = lookup(baseName) ?? Fail($"base: there is no theme called '{baseName}'.", Themes.Simple);
        }

        if (root["derive"] is { } derive)
        {
            if (derive is not JsonObject d) problems.Add("derive: give background, foreground and accent (and optionally link and ansi).");
            else
            {
                foreach (var key in d.Select(kv => kv.Key).Where(k => k is not ("background" or "foreground" or "accent" or "link" or "ansi")))
                    problems.Add($"derive.{key} is not known. Use background, foreground, accent, link and ansi.");
                var bg = Colour(d, "background", "derive.background", required: true);
                var fg = Colour(d, "foreground", "derive.foreground", required: true);
                var accent = Colour(d, "accent", "derive.accent", required: true);
                var link = Colour(d, "link", "derive.link");
                var ansi = d["ansi"] is { } a ? Ansi(a, "derive.ansi") : root["ansi"] is { } top ? Ansi(top, "ansi") : theme.Ansi;
                if (bg is not null && fg is not null && accent is not null && ansi is not null)
                {
                    var derived = Themes.Derive(id, theme.Name, theme.Description, bg, fg, accent, link, ansi);
                    theme = derived with { SoundSet = theme.SoundSet, Font = theme.Font, Bevel = theme.Bevel, Radius = theme.Radius, Chrome = theme.Chrome };
                }
            }
        }

        if (Colour(root, "accent", "accent") is { } accentHex) theme = theme.WithAccent(accentHex);

        var colors = new Dictionary<string, string>(theme.Colors);
        foreach (var t in Themes.Simple.Colors.Keys) colors.TryAdd(t, theme[t]);
        if (root["colors"] is { } c)
        {
            if (c is not JsonObject map) problems.Add("colors: give one colour per token, e.g. bg: \"#171a23\".");
            else
                foreach (var (token, _) in map)
                {
                    if (!Themes.Simple.Colors.ContainsKey(token))
                    {
                        var near = Themes.Simple.Colors.Keys.FirstOrDefault(k => string.Equals(k, token, StringComparison.OrdinalIgnoreCase));
                        problems.Add($"colors.{token} is not a colour token{(near is null ? "" : $" (did you mean {near}?)")}.");
                        continue;
                    }
                    if (Colour(map, token, "colors." + token, required: true) is { } hex) colors[token] = hex;
                }
        }
        var ansiColours = root["ansi"] is { } ansiNode && root["derive"] is null ? Ansi(ansiNode, "ansi") ?? theme.Ansi : theme.Ansi;

        var light = Bool(root, "light") ?? (root["derive"] is null && baseName is null && root["colors"] is JsonObject cm && cm.ContainsKey("bg") && Rgb.TryParse(colors["bg"], out var bgc) ? bgc.Luminance > 0.5 : theme.Light);
        var radius = Number(root, "radius") ?? theme.Radius;
        if (radius is < 0 or > 30) { problems.Add("radius must be between 0 and 30."); radius = theme.Radius; }
        var chrome = Str(root, "chrome") ?? theme.Chrome;
        if (chrome is not ("system" or "custom")) { problems.Add("chrome must be system or custom."); chrome = theme.Chrome; }
        var sounds = Str(root, "sounds") ?? theme.SoundSet;
        if (!SoundService.Sets.Contains(sounds)) { problems.Add($"sounds must be one of {string.Join(", ", SoundService.Sets)}."); sounds = theme.SoundSet; }

        if (problems.Count > 0) throw new ThemeFileException(problems);
        var name = Str(root, "name");
        return new ThemeDef(id, string.IsNullOrWhiteSpace(name) ? (baseName is null ? id : theme.Name) : name.Trim(), Str(root, "description") ?? (name is null ? theme.Description : ""),
            light, colors, ansiColours, radius, chrome, sounds, Bool(root, "bevel") ?? theme.Bevel, root.ContainsKey("font") ? NullIfEmpty(Str(root, "font")) : theme.Font);

        T Fail<T>(string message, T fallback) { problems.Add(message); return fallback; }

        string? Str(JsonObject o, string key) => o[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => Fail<string?>($"{key} must be a single value.", null),
        };

        bool? Bool(JsonObject o, string key) => Str(o, key)?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "true" or "yes" or "on" => true,
            "false" or "no" or "off" => false,
            var other => Fail<bool?>($"{key} must be true or false, not '{other}'.", null),
        };

        double? Number(JsonObject o, string key) => Str(o, key) is not { } s ? null
            : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : Fail<double?>($"{key} must be a number, not '{s}'.", null);

        string? Colour(JsonObject o, string key, string path, bool required = false)
        {
            if (!o.ContainsKey(key)) return required && path.StartsWith("derive", StringComparison.Ordinal) ? Fail<string?>($"{path} is missing.", null) : null;
            var s = Str(o, key);
            if (s is null) return Fail<string?>($"{path} is empty. In YAML, put colours in quotes (\"#171a23\"): an unquoted # starts a comment.", null);
            return Rgb.TryParse(s, out var rgb) ? rgb.Hex : Fail<string?>($"{path}: '{s}' is not a colour. Write it as #rrggbb.", null);
        }

        string[]? Ansi(JsonNode node, string path)
        {
            var result = new string[16];
            if (node is JsonArray list)
            {
                if (list.Count != 16) return Fail<string[]?>($"{path} needs 16 colours (8 normal, then 8 bright); it has {list.Count}.", null);
                for (var i = 0; i < 16; i++)
                {
                    var holder = new JsonObject { ["v"] = list[i]?.DeepClone() };
                    if (Colour(holder, "v", $"{path}[{i}]") is not { } hex) return null;
                    result[i] = hex;
                }
                return result;
            }
            if (node is JsonObject named)
            {
                var start = theme.Ansi;
                for (var i = 0; i < 16; i++) result[i] = start[i];
                foreach (var (key, _) in named)
                {
                    var i = Array.FindIndex(AnsiNames, n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) { problems.Add($"{path}.{key} is not an ANSI colour. Use {string.Join(", ", AnsiNames)}."); continue; }
                    if (Colour(named, key, $"{path}.{key}", required: true) is { } hex) result[i] = hex;
                }
                return result;
            }
            return Fail<string[]?>($"{path} must be a list of 16 colours or a map such as red: \"#e06c75\".", null);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Writes a theme in full (every colour), so the file shows everything there is to change.</summary>
    public static string Write(ThemeDef t, string format) => format == "json" ? ToJson(t) : ToYaml(t);

    public static string ToJson(ThemeDef t)
    {
        var o = new JsonObject
        {
            ["name"] = t.Name, ["description"] = t.Description, ["light"] = t.Light, ["radius"] = t.Radius, ["chrome"] = t.Chrome,
            ["sounds"] = t.SoundSet, ["bevel"] = t.Bevel,
        };
        if (t.Font is not null) o["font"] = t.Font;
        o["colors"] = new JsonObject(Tokens.Select(k => KeyValuePair.Create(k.Name, (JsonNode?)t[k.Name])));
        o["ansi"] = new JsonArray(t.Ansi.Select(a => (JsonNode)a).ToArray());
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static string ToYaml(ThemeDef t)
    {
        var sb = new StringBuilder();
        sb.Append("# avamux theme. Colours are #rrggbb and must be in quotes. Anything left out comes from\n");
        sb.Append("# the base theme (add  base: <theme id>  to pick one; Simple otherwise).\n");
        sb.Append($"name: {Scalar(t.Name)}\n");
        sb.Append($"description: {Scalar(t.Description)}\n");
        void Line(string text, string comment) => sb.Append(text.PadRight(18)).Append(" # ").Append(comment).Append('\n');
        Line($"light: {(t.Light ? "true" : "false")}", "light or dark controls");
        Line($"radius: {t.Radius.ToString(CultureInfo.InvariantCulture)}", "corner radius; 0 gives square corners");
        Line($"chrome: {t.Chrome}", "system (the desktop's title bar) or custom (drawn by avamux)");
        Line($"sounds: {t.SoundSet}", string.Join(", ", SoundService.Sets));
        Line($"bevel: {(t.Bevel ? "true" : "false")}", "raised buttons and sunken fields");
        if (t.Font is not null) sb.Append($"font: {Scalar(t.Font)}\n");
        sb.Append("colors:\n");
        foreach (var group in Tokens.GroupBy(k => k.Group))
        {
            sb.Append($"  # {group.Key}\n");
            foreach (var k in group) sb.Append($"  {(k.Name + ":").PadRight(12)}\"{t[k.Name]}\"   # {k.Description}\n");
        }
        sb.Append("# Terminal colours: black, red, green, yellow, blue, magenta, cyan, white, then the bright ones.\n");
        sb.Append("ansi:\n");
        for (var i = 0; i < t.Ansi.Length; i++) sb.Append($"  - \"{t.Ansi[i]}\"   # {AnsiNames[i]}\n");
        return sb.ToString();
    }

    private static string Scalar(string s) =>
        s.Length == 0 || s.IndexOfAny([':', '#', '"', '\'', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0 || s != s.Trim() || s is "true" or "false" or "yes" or "no" or "null"
            ? JsonSerializer.Serialize(s) : s;

    /// <summary>A short description of the format, for the README, the editor and <c>theme help</c>.</summary>
    public const string Reference = """
        A theme file is JSON or YAML (.json, .yaml or .yml) in the themes folder; its file name is the theme's id.
        Every key is optional:
          name, description   what the theme is called and a line about it
          base                id of a theme to start from (default: simple); a file named after a built-in replaces it
          light               true for light controls
          radius              corner radius, 0 to 30 (0 = square corners)
          chrome              system or custom title bars
          sounds              soft, retro or synth
          bevel               true for raised buttons and sunken fields
          font                comma-separated font families for the interface
          derive              { background, foreground, accent, link?, ansi? }: build every colour from these
          accent              one colour that recolours accent, links and selection
          colors              any of the colour tokens, e.g.  bg: "#171a23"
          ansi                16 terminal colours as a list, or a map such as  red: "#e06c75"
        Order: base, then derive, then accent, then colors and ansi.
        """;
}

/// <summary>The theme files in the themes folder: loads them, keeps them up to date and saves the editor's changes.</summary>
public sealed class ThemeStore : IDisposable
{
    private readonly bool _publish;
    private FileSystemWatcher? _watcher;
    private Timer? _reload;
    private IReadOnlyList<ThemeDef> _themes = [];
    private IReadOnlyList<(string File, IReadOnlyList<string> Problems)> _problems = [];

    /// <param name="publish">Make the loaded themes the app's theme list (<see cref="Themes.All"/>).</param>
    public ThemeStore(string dir, bool publish = false)
    {
        Dir = dir;
        _publish = publish;
        Directory.CreateDirectory(dir);
        Load();
    }

    public string Dir { get; }
    public IReadOnlyList<ThemeDef> Themes => _themes;
    /// <summary>Files that could not be read, with what is wrong with them.</summary>
    public IReadOnlyList<(string File, IReadOnlyList<string> Problems)> Problems => _problems;
    public event Action? Changed;

    public void Load()
    {
        var files = Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir).Where(ThemeFile.IsThemeFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        // Read every file first so that a theme can be based on another file whatever their order.
        var trees = new Dictionary<string, (string File, JsonNode Tree)>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<(string, IReadOnlyList<string>)>();
        foreach (var file in files)
        {
            var id = IdOf(file);
            if (trees.ContainsKey(id)) { problems.Add((file, [$"Another file already defines the theme '{id}' ({Path.GetFileName(trees[id].File)})."])); continue; }
            try { trees[id] = (file, ThemeFile.ReadTree(File.ReadAllText(file), ThemeFile.FormatOf(file))); }
            catch (ThemeFileException ex) { problems.Add((file, ex.Problems)); }
            catch (Exception ex) { problems.Add((file, [ex.Message])); }
        }
        var done = new Dictionary<string, ThemeDef>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ThemeDef? Resolve(string id)
        {
            if (done.TryGetValue(id, out var t)) return t;
            if (!trees.TryGetValue(id, out var entry))
                return Theming.Themes.Builtin.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(b.Name, id, StringComparison.OrdinalIgnoreCase));
            if (!visiting.Add(id)) throw new ThemeFileException([$"base: '{id}' is part of a loop of themes based on each other."]);
            try
            {
                var theme = ThemeFile.Parse(entry.Tree, id, other => string.Equals(other, id, StringComparison.OrdinalIgnoreCase) ? null : Resolve(other)) with { File = entry.File };
                return done[id] = theme;
            }
            finally { visiting.Remove(id); }
        }
        foreach (var (id, entry) in trees)
        {
            try { Resolve(id); }
            catch (ThemeFileException ex) { problems.Add((entry.File, ex.Problems)); }
        }
        foreach (var (file, list) in problems) Log.Warn("theme", $"{Path.GetFileName(file)}: {string.Join(" ", list)}");
        _themes = done.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _problems = problems;
        if (_publish) Theming.Themes.SetFileThemes(_themes);
        Changed?.Invoke();
    }

    /// <summary>Reloads when files in the folder change (edited by hand, copied in, deleted).</summary>
    public void Watch()
    {
        if (_watcher is not null) return;
        try
        {
            _reload = new Timer(_ => { try { Load(); } catch (Exception ex) { Log.Error("theme", "Could not reload themes", ex); } });
            _watcher = new FileSystemWatcher(Dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            FileSystemEventHandler soon = (_, e) => { if (ThemeFile.IsThemeFile(e.FullPath)) _reload.Change(300, Timeout.Infinite); };
            _watcher.Changed += soon; _watcher.Created += soon; _watcher.Deleted += soon;
            _watcher.Renamed += (_, _) => _reload.Change(300, Timeout.Infinite);
        }
        catch (Exception ex) { Log.Warn("theme", $"Not watching the themes folder: {ex.Message}"); }
    }

    public static string IdOf(string file) => Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant();

    /// <summary>A file-name-safe id made from a theme's name.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "theme" : slug;
    }

    /// <summary>An id that no theme (built-in or file) uses yet.</summary>
    public string UniqueId(string name)
    {
        var slug = Slug(name);
        var id = slug;
        for (var n = 2; Theming.Themes.IsBuiltinId(id) || _themes.Any(t => t.Id == id) || FilesOf(id).Count > 0; n++) id = $"{slug}-{n}";
        return id;
    }

    public string PathFor(string id, string format) =>
        _themes.FirstOrDefault(t => t.Id == id)?.File is { } existing && ThemeFile.FormatOf(existing) == format ? existing
            : Path.Combine(Dir, id + (format == "json" ? ".json" : ".yaml"));

    /// <summary>
    /// Checks <paramref name="text"/> and writes it as theme <paramref name="id"/>, replacing a file of that theme
    /// in the other format. Returns the theme as loaded from the new file.
    /// </summary>
    public ThemeDef SaveText(string id, string text, string? format = null)
    {
        id = Slug(id);
        format ??= ThemeFile.DetectFormat(text);
        ThemeFile.Parse(text, id, format, other => string.Equals(other, id, StringComparison.OrdinalIgnoreCase) ? null : Theming.Themes.TryFind(other));
        var path = PathFor(id, format);
        foreach (var other in FilesOf(id))
            if (!string.Equals(other, path, StringComparison.Ordinal)) File.Delete(other);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        Load();
        return _themes.FirstOrDefault(t => t.Id == id) ?? throw new ThemeFileException(Problems.FirstOrDefault(p => p.File == path).Problems ?? ["The theme could not be read back."]);
    }

    public ThemeDef Save(ThemeDef theme, string format = "yaml") => SaveText(theme.Id, ThemeFile.Write(theme, format), format);

    /// <summary>Removes a theme's file. For a file that replaced a built-in theme, the built-in one comes back.</summary>
    public bool Delete(string id)
    {
        var removed = false;
        foreach (var path in FilesOf(Slug(id))) { File.Delete(path); removed = true; }
        if (removed) Load();
        return removed;
    }

    /// <summary>The files that define a theme id, whatever their extension or the case of their name.</summary>
    private List<string> FilesOf(string id) => Directory.Exists(Dir)
        ? Directory.EnumerateFiles(Dir).Where(f => ThemeFile.IsThemeFile(f) && IdOf(f) == id).ToList()
        : [];

    /// <summary>Copies a theme file into the folder after checking it. Returns the imported theme.</summary>
    public ThemeDef Import(string path, string? id = null)
    {
        if (!ThemeFile.IsThemeFile(path)) throw new ArgumentException("Theme files end in .yaml, .yml or .json.");
        var text = File.ReadAllText(path);
        return SaveText(id ?? IdOf(path), text, ThemeFile.FormatOf(path));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reload?.Dispose();
    }
}
