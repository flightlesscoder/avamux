using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Theming;

namespace Avamux.Tests;

/// <summary>The theme list is app-wide, so the tests that change it do not run alongside others.</summary>
[CollectionDefinition("theme list", DisableParallelization = true)]
public class ThemeListCollection;

[Collection("theme list")]
public class ThemeFileTests
{
    [Fact]
    public void Every_token_is_described_once()
    {
        Assert.Equal(Themes.Simple.Colors.Keys.Order(), ThemeFile.Tokens.Select(t => t.Name).Order());
        Assert.Equal(16, ThemeFile.AnsiNames.Length);
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("json")]
    public void Every_built_in_theme_survives_a_round_trip(string format)
    {
        foreach (var theme in Themes.Builtin)
        {
            var text = ThemeFile.Write(theme, format);
            var back = ThemeFile.Parse(text, theme.Id, format);
            Assert.Equal(theme.Fingerprint(), back.Fingerprint());
            Assert.Equal(theme.Name, back.Name);
            Assert.Equal(theme.Description, back.Description);
        }
    }

    [Fact]
    public void A_small_yaml_file_fills_in_the_rest_from_its_base()
    {
        var t = ThemeFile.Parse("""
            name: Pink Classic
            base: classic90s
            accent: "#ff2fd4"
            colors:
              bg: "#dddddd"   # a comment
              text: 101010    # no # and no quotes works too
            ansi:
              red: "#ff0000"
            """, "pink-classic");
        Assert.Equal("Pink Classic", t.Name);
        Assert.True(t.Light);
        Assert.True(t.Bevel);
        Assert.Equal(0, t.Radius);
        Assert.Equal("#dddddd", t["bg"]);
        Assert.Equal("#101010", t["text"]);
        Assert.Equal("#ff2fd4", t["accent"]);
        Assert.NotEqual(Themes.Classic90s["link"], t["link"]); // the accent family follows the accent
        Assert.Equal("#ff0000", t.Ansi[1]);
        Assert.Equal(Themes.Classic90s.Ansi[2], t.Ansi[2]);
        Assert.Equal(Themes.Classic90s["panel"], t["panel"]);
    }

    [Fact]
    public void Derive_builds_every_colour_from_a_terminal_palette()
    {
        var t = ThemeFile.Parse("""{ "name": "Paper", "derive": { "background": "#fbf8f1", "foreground": "#2d2a26", "accent": "#b5487a" } }""", "paper");
        Assert.True(t.Light); // worked out from the background
        Assert.Equal("#fbf8f1", t["bgEditor"]);
        Assert.Equal("#b5487a", t["accent"]);
        Assert.All(Themes.Simple.Colors.Keys, k => Assert.True(Rgb.TryParse(t[k], out _), k));
        Assert.True(Rgb.Parse(t["textStrong"]).Luminance < 0.3, "strong text is dark on a light theme");
    }

    [Fact]
    public void Mistakes_are_all_reported_with_where_they_are()
    {
        var ex = Assert.Throws<ThemeFileException>(() => ThemeFile.Parse("""
            name: Broken
            colour: x
            radius: wide
            chrome: fancy
            colors:
              bg: #171a23
              txt: "#ffffff"
              accent: "not a colour"
            ansi: ["#000000"]
            """, "broken"));
        var all = string.Join("\n", ex.Problems);
        Assert.Contains("'colour' is not a theme setting", all);
        Assert.Contains("radius must be a number", all);
        Assert.Contains("chrome must be system or custom", all);
        Assert.Contains("colors.bg is empty", all);
        Assert.Contains("in quotes", all);
        Assert.Contains("colors.txt is not a colour token", all);
        Assert.Contains("colors.accent: 'not a colour' is not a colour", all);
        Assert.Contains("ansi needs 16 colours", all);

        var syntax = Assert.Throws<ThemeFileException>(() => ThemeFile.Parse("name: [unclosed", "x", "yaml"));
        Assert.StartsWith("Line ", syntax.Problems[0]);
        var json = Assert.Throws<ThemeFileException>(() => ThemeFile.Parse("{ \"name\": ", "x", "json"));
        Assert.StartsWith("Line 1", json.Problems[0]);
        Assert.Contains("no theme called", Assert.Throws<ThemeFileException>(() => ThemeFile.Parse("base: nope", "x")).Message);
    }

    [Fact]
    public void The_store_loads_saves_and_deletes_theme_files()
    {
        using var box = new Sandbox();
        var dir = box.Paths.ThemesDir;
        File.WriteAllText(Path.Combine(dir, "Child.yaml"), "name: Child\nbase: parent\ncolors:\n  accent: \"#00ff00\"\n");
        File.WriteAllText(Path.Combine(dir, "parent.json"), """{ "name": "Parent", "colors": { "bg": "#202020" } }""");
        File.WriteAllText(Path.Combine(dir, "bad.yaml"), "colors:\n  bg: nope\n");
        File.WriteAllText(Path.Combine(dir, "loop-a.yaml"), "base: loop-b\n");
        File.WriteAllText(Path.Combine(dir, "loop-b.yaml"), "base: loop-a\n");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a theme");
        File.WriteAllText(Path.Combine(dir, "simple.yaml"), "name: My Simple\ncolors:\n  accent: \"#ff8800\"\n");

        using var store = new ThemeStore(dir);
        // Order on disk does not matter: the child finds its parent.
        var child = Assert.Single(store.Themes, t => t.Id == "child");
        Assert.Equal("#202020", child["bg"]);
        Assert.Equal("#00ff00", child["accent"]);
        Assert.EndsWith("Child.yaml", child.File);
        // A file named after a built-in starts from it and replaces it.
        var simple = Assert.Single(store.Themes, t => t.Id == "simple");
        Assert.Equal("#ff8800", simple["accent"]);
        Assert.Equal(Themes.Simple["panel"], simple["panel"]);
        Assert.Contains(store.Problems, p => p.File.EndsWith("bad.yaml") && p.Problems[0].Contains("not a colour"));
        Assert.Contains(store.Problems, p => p.File.EndsWith("loop-a.yaml") || p.File.EndsWith("loop-b.yaml"));
        Assert.DoesNotContain(store.Themes, t => t.Id.StartsWith("loop"));

        // Saving in the other format replaces the old file rather than leaving two.
        var saved = store.Save(child with { Name = "Child Two" }, "json");
        Assert.Equal("Child Two", saved.Name);
        Assert.True(File.Exists(Path.Combine(dir, "child.json")));
        Assert.False(File.Exists(Path.Combine(dir, "Child.yaml")));
        Assert.Throws<ThemeFileException>(() => store.SaveText("child", "radius: -4"));
        Assert.Equal("Child Two", store.Themes.Single(t => t.Id == "child").Name);

        Assert.Equal("child-2", store.UniqueId("Child"));
        Assert.Equal("simple-2", store.UniqueId("Simple"));
        Assert.Equal("my-new-theme", ThemeStore.Slug("  My new  theme! "));

        Assert.True(store.Delete("child"));
        Assert.DoesNotContain(store.Themes, t => t.Id == "child");
        Assert.False(store.Delete("child"));
    }

    [Fact]
    public void File_themes_join_the_theme_list_and_can_replace_built_in_ones()
    {
        var mine = Themes.Simple with { Id = "zz-file-theme-test", Name = "ZZ File Theme", File = "/x/zz.yaml" };
        var replaced = Themes.Simple with { Name = "Simple (mine)", File = "/x/simple.yaml" };
        try
        {
            Themes.SetFileThemes([mine, replaced]);
            Assert.Equal("ZZ File Theme", Themes.Find("zz-file-theme-test").Name);
            Assert.Equal("Simple (mine)", Themes.Find("simple").Name);
            Assert.Equal(Themes.Builtin.Count + 1, Themes.All.Count);
            Assert.Equal("simple", Themes.All[0].Id); // keeps its place
        }
        finally { Themes.SetFileThemes([]); }
    }

    [Fact]
    public async Task Theme_commands_show_validate_import_and_delete()
    {
        using var box = new Sandbox();
        await using var services = new AppServices(box.Paths);
        async Task<JsonNode?> Run(string name, JsonObject args)
        {
            var reply = await services.Commands.ExecuteAsync(name, args);
            Assert.True(reply["ok"]?.GetValue<bool>() ?? false, reply.ToJsonString());
            return reply["result"];
        }

        var yaml = (await Run("theme show", new JsonObject { ["name"] = "synthwave-kat" }))!.GetValue<string>();
        Assert.Contains("name: Synthwave Kat", yaml);
        Assert.Contains("accent:", yaml);

        var file = Path.Combine(box.Root, "neon-test.yaml");
        await Run("theme show", new JsonObject { ["name"] = "hot-pink-neon", ["file"] = file });
        File.WriteAllText(file, File.ReadAllText(file).Replace("name: Hot Pink Neon", "name: Neon Test"));
        Assert.True((await Run("theme validate", new JsonObject { ["file"] = file }))!["ok"]!.GetValue<bool>());

        var imported = await Run("theme import", new JsonObject { ["file"] = file });
        Assert.Equal("neon-test", imported!["id"]!.GetValue<string>());
        Assert.Contains(services.ThemeFiles.Themes, t => t.Name == "Neon Test");

        File.WriteAllText(file, "colors:\n  bg: oops\n");
        var bad = await Run("theme validate", new JsonObject { ["file"] = file });
        Assert.False(bad!["ok"]!.GetValue<bool>());
        Assert.Contains("not a colour", bad["problems"]![0]!.GetValue<string>());

        await Run("theme delete", new JsonObject { ["name"] = "neon-test" });
        Assert.DoesNotContain(services.ThemeFiles.Themes, t => t.Id == "neon-test");
        Assert.Contains("derive", (await Run("theme help", new JsonObject()))!.GetValue<string>());
    }
}
