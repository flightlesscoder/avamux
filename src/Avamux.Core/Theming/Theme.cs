using System.Globalization;

namespace Avamux.Core.Theming;

/// <summary>
/// A named set of colours plus the few non-colour choices a theme makes (light or dark, corner radius, window
/// chrome, which sounds). Colours are looked up by token name; <see cref="Themes.Simple"/> defines every token.
/// </summary>
public sealed record ThemeDef(string Id, string Name, string Description, bool Light, IReadOnlyDictionary<string, string> Colors,
    string[] Ansi, double Radius, string Chrome, string SoundSet, bool Bevel = false, string? Font = null)
{
    public string this[string token] => Colors.TryGetValue(token, out var c) ? c : Themes.Simple.Colors[token];

    /// <summary>The same theme with its accent family (accent, links, selection) rebuilt from one colour.</summary>
    public ThemeDef WithAccent(string? hex)
    {
        if (!Rgb.TryParse(hex, out var a)) return this;
        var colors = new Dictionary<string, string>(Colors)
        {
            ["accent"] = a.Hex,
            ["link"] = (Light ? a.Mix(Rgb.Black, 0.15) : a.Mix(Rgb.White, 0.22)).Hex,
            ["linkHi"] = (Light ? a.Mix(Rgb.Black, 0.35) : a.Mix(Rgb.White, 0.5)).Hex,
            ["accentDeep"] = (Light ? a : a.Mix(Rgb.Black, 0.35)).Hex,
            ["onAccent"] = a.Luminance > 0.45 ? "#11131a" : "#ffffff",
        };
        return this with { Colors = colors };
    }
}

/// <summary>Minimal colour arithmetic for deriving palettes.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public static bool TryParse(string? hex, out Rgb rgb)
    {
        rgb = default;
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Rgb Parse(string hex) => TryParse(hex, out var c) ? c : throw new FormatException($"Not a colour: '{hex}'.");
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";
    public double Luminance => (0.299 * R + 0.587 * G + 0.114 * B) / 255.0;

    /// <summary>Moves <paramref name="amount"/> (0..1) of the way towards <paramref name="other"/>.</summary>
    public Rgb Mix(Rgb other, double amount) => new(
        (byte)Math.Round(R + (other.R - R) * amount), (byte)Math.Round(G + (other.G - G) * amount), (byte)Math.Round(B + (other.B - B) * amount));
}

public static class Themes
{
    private static readonly string[] SimpleAnsi =
    [
        "#1c1f2b", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#c8ccd4",
        "#5c6370", "#ff7b86", "#b1e18b", "#ffd68a", "#7cc0ff", "#dd91f2", "#6fd3df", "#ffffff",
    ];

    /// <summary>The original look. Its colour table is the list of tokens every other theme may override.</summary>
    public static readonly ThemeDef Simple = new("simple", "Simple", "The original avamux look: quiet dark blue-greys with a blue accent.", false,
        new Dictionary<string, string>
        {
            ["bg"] = "#171a23", ["bgDeep"] = "#10121a", ["bgEditor"] = "#11131a", ["bgSide"] = "#141720",
            ["panel"] = "#1f2330", ["panelHi"] = "#2b3142", ["cell"] = "#1b1e29", ["cellSel"] = "#252a3a",
            ["line"] = "#2e3440", ["lineSoft"] = "#232735", ["lineStrong"] = "#4c566a", ["scroll"] = "#3b4252",
            ["text"] = "#d8dee9", ["textStrong"] = "#ffffff", ["textSoft"] = "#b8c0d0", ["textMute"] = "#a7b0c0", ["textCode"] = "#c8ccd4",
            ["dim"] = "#8b93a5", ["faint"] = "#6b7280", ["ghost"] = "#4b5263",
            ["accent"] = "#61afef", ["link"] = "#7cc0ff", ["linkHi"] = "#a9d6ff", ["accentDeep"] = "#3b6ea8", ["onAccent"] = "#11131a",
            ["ok"] = "#98c379", ["okHi"] = "#b1e18b", ["warn"] = "#e5c07b", ["error"] = "#e06c75", ["errorHi"] = "#ff7b86", ["errorText"] = "#ff8f98",
            ["purple"] = "#c678dd", ["cyan"] = "#56b6c2", ["orange"] = "#d19a66",
            ["addBg"] = "#1b2b20", ["delBg"] = "#33202a", ["hunkBg"] = "#1a2233",
            ["termBg"] = "#11131a", ["termFg"] = "#d8dee9", ["termSel"] = "#4c566a",
            ["titleBg"] = "#10121a", ["titleFg"] = "#d8dee9",
        }, SimpleAnsi, 8, "system", "soft");

    /// <summary>Grey bevels, navy title bar, white fields: a desktop from the middle of the 1990s.</summary>
    public static readonly ThemeDef Classic90s = new("classic90s", "90's Desktop Computer OS Classic", "Grey raised panels, a navy title bar, square corners and beeps.", true,
        new Dictionary<string, string>
        {
            ["bg"] = "#c0c0c0", ["bgDeep"] = "#c0c0c0", ["bgEditor"] = "#ffffff", ["bgSide"] = "#c0c0c0",
            ["panel"] = "#c0c0c0", ["panelHi"] = "#ffffe1", ["cell"] = "#ffffff", ["cellSel"] = "#d6d6f5",
            ["line"] = "#808080", ["lineSoft"] = "#808080", ["lineStrong"] = "#000000", ["scroll"] = "#808080",
            ["text"] = "#000000", ["textStrong"] = "#000000", ["textSoft"] = "#1a1a1a", ["textMute"] = "#2a2a2a", ["textCode"] = "#000000",
            ["dim"] = "#404040", ["faint"] = "#606060", ["ghost"] = "#8a8a8a",
            ["accent"] = "#000080", ["link"] = "#0000ee", ["linkHi"] = "#0000aa", ["accentDeep"] = "#000080", ["onAccent"] = "#ffffff",
            ["ok"] = "#008000", ["okHi"] = "#006400", ["warn"] = "#806000", ["error"] = "#c00000", ["errorHi"] = "#e00000", ["errorText"] = "#a00000",
            ["purple"] = "#800080", ["cyan"] = "#008080", ["orange"] = "#a04000",
            ["addBg"] = "#d9f2d9", ["delBg"] = "#f8dada", ["hunkBg"] = "#dbe4f8",
            ["termBg"] = "#000000", ["termFg"] = "#c0c0c0", ["termSel"] = "#000080",
            ["titleBg"] = "#000080", ["titleFg"] = "#ffffff",
        },
        [
            "#000000", "#aa0000", "#00aa00", "#aa5500", "#0000aa", "#aa00aa", "#00aaaa", "#aaaaaa",
            "#555555", "#ff5555", "#55ff55", "#ffff55", "#5555ff", "#ff55ff", "#55ffff", "#ffffff",
        ], 0, "custom", "retro", Bevel: true, Font: "Liberation Sans, Arimo, Arial, Tahoma, DejaVu Sans");

    /// <summary>Builds a dark theme from a terminal palette (background, foreground, 16 ANSI colours) and an accent.</summary>
    private static ThemeDef FromTerminal(string id, string name, string description, string bg, string fg, string accent, string link, string[] ansi, string sounds = "synth")
    {
        var (b, f, a) = (Rgb.Parse(bg), Rgb.Parse(fg), Rgb.Parse(accent));
        string M(double t) => b.Mix(f, t).Hex;
        string Tint(string colour, double t) => b.Mix(Rgb.Parse(colour), t).Hex;
        return new ThemeDef(id, name, description, false, new Dictionary<string, string>
        {
            ["bg"] = M(0.035), ["bgDeep"] = b.Mix(Rgb.Black, 0.25).Hex, ["bgEditor"] = bg, ["bgSide"] = M(0.02),
            ["panel"] = M(0.08), ["panelHi"] = M(0.16), ["cell"] = M(0.055), ["cellSel"] = Tint(accent, 0.2),
            ["line"] = M(0.17), ["lineSoft"] = M(0.11), ["lineStrong"] = M(0.32), ["scroll"] = M(0.26),
            ["text"] = fg, ["textStrong"] = "#ffffff", ["textSoft"] = M(0.82), ["textMute"] = M(0.72), ["textCode"] = M(0.9),
            ["dim"] = M(0.58), ["faint"] = M(0.44), ["ghost"] = M(0.3),
            ["accent"] = accent, ["link"] = link, ["linkHi"] = Rgb.Parse(link).Mix(Rgb.White, 0.4).Hex, ["accentDeep"] = a.Mix(b, 0.35).Hex,
            ["onAccent"] = b.Mix(Rgb.Black, 0.2).Hex,
            ["ok"] = ansi[2], ["okHi"] = ansi[10], ["warn"] = ansi[3], ["error"] = ansi[1], ["errorHi"] = ansi[9], ["errorText"] = ansi[9],
            ["purple"] = ansi[13], ["cyan"] = ansi[6], ["orange"] = Rgb.Parse(ansi[3]).Mix(Rgb.Parse(ansi[1]), 0.4).Hex,
            ["addBg"] = Tint(ansi[2], 0.14), ["delBg"] = Tint(ansi[1], 0.18), ["hunkBg"] = Tint(ansi[4], 0.14),
            ["termBg"] = bg, ["termFg"] = fg, ["termSel"] = a.Mix(b, 0.45).Hex,
            ["titleBg"] = b.Mix(Rgb.Black, 0.25).Hex, ["titleFg"] = fg,
        }, ansi, 8, "system", sounds);
    }

    // Palettes below come from the kitty themes in the terminal-stuff repository.
    public static readonly ThemeDef SynthwaveKat = FromTerminal("synthwave-kat", "Synthwave Kat", "Deep violet with fuchsia and cyan neon.",
        "#0d0221", "#ede7f6", "#ff2fd4", "#00f0ff",
        ["#1a0b2e", "#ff2f6e", "#00ffb3", "#ffe066", "#00d4ff", "#ff2fd4", "#00f0ff", "#e0d7f5", "#4a2d6b", "#ff5c8a", "#5affce", "#fff59d", "#5ce1ff", "#ff8ae2", "#7df9ff", "#ffffff"]);

    public static readonly ThemeDef HotPinkNeon = FromTerminal("hot-pink-neon", "Hot Pink Neon", "Near-black purple with hot pink and acid green.",
        "#0a0014", "#ffe5fb", "#ff0080", "#ff66c4",
        ["#14001f", "#ff0054", "#39ff14", "#fcee09", "#01cdfe", "#ff0080", "#01ffc3", "#ffe5fb", "#3d0a52", "#ff5c8a", "#7dff5c", "#fff97d", "#5ce1ff", "#ff6ec7", "#7dffdb", "#ffffff"]);

    public static readonly ThemeDef MiamiRetro = FromTerminal("miami-retro", "Miami Retro", "Indigo evening with pink, aqua and sunset orange.",
        "#1b1035", "#fef6ff", "#ff6ec7", "#40c9ff",
        ["#1b1035", "#ff5277", "#2de8b0", "#ffb86c", "#40c9ff", "#ff6ec7", "#2de8e0", "#fef6ff", "#4b3a75", "#ff8aa3", "#7ff5d4", "#ffd199", "#8fe3ff", "#ffa3dc", "#8ff5f0", "#ffffff"]);

    public static readonly ThemeDef RoseGoldSoft = FromTerminal("rose-gold-soft", "Rose Gold Soft", "Warm plum with muted rose and gold.",
        "#241521", "#f7e7e0", "#e8a0bf", "#f0c987",
        ["#2e1a28", "#d16b8f", "#a3c9a8", "#f0c987", "#b599c7", "#d68fc9", "#9fd4d0", "#f7e7e0", "#5c3a52", "#eb93b3", "#c3e0c6", "#f7dca8", "#d1b8e0", "#eab8e0", "#c2e8e5", "#ffffff"], "soft");

    public static readonly IReadOnlyList<ThemeDef> All = [Simple, Classic90s, SynthwaveKat, HotPinkNeon, MiamiRetro, RoseGoldSoft];

    /// <summary>Accent colours offered as one-click choices.</summary>
    public static readonly string[] AccentPresets =
        ["#61afef", "#f25cf5", "#ff2fd4", "#ff0080", "#ff6ec7", "#e8a0bf", "#c678dd", "#00f0ff", "#40c9ff", "#2de8b0", "#98c379", "#e5c07b", "#ffb86c", "#e06c75", "#000080", "#008080"];

    public static ThemeDef Find(string? idOrName) =>
        All.FirstOrDefault(t => string.Equals(t.Id, idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase)) ?? Simple;

    /// <summary>The theme the settings ask for, with the accent override applied.</summary>
    public static ThemeDef Resolve(Config.ThemeSettings s) => Find(s.Name).WithAccent(s.Accent);

    public static bool CustomChrome(Config.ThemeSettings s, ThemeDef theme) => s.WindowChrome.Trim().ToLowerInvariant() switch
    {
        "custom" => true, "system" => false, _ => theme.Chrome == "custom",
    };

    /// <summary>CSS custom properties (--bg, --text, …) for the pages avamux serves.</summary>
    public static string CssVariables(ThemeDef t) =>
        string.Join(" ", Simple.Colors.Keys.Select(k => $"--{k}: {t[k]};")) + $" --radius: {t.Radius.ToString(CultureInfo.InvariantCulture)}px;";
}
