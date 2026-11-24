using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avamux.Views;

namespace Avamux.UiTests;

public sealed class CommandPaletteTests
{
    [Fact]
    public void Shift_tapped_twice_opens_but_shift_held_for_capitals_does_not()
    {
        var key = new PaletteKey();
        key.Configure("Shift Shift");
        // Tap, tap.
        Assert.False(key.Down(PaletteKey.Mod.Shift, 0));
        Assert.False(key.Up(PaletteKey.Mod.Shift, 60));
        Assert.False(key.Down(PaletteKey.Mod.Shift, 150));
        Assert.True(key.Up(PaletteKey.Mod.Shift, 210));
        // Shift+A, Shift+B typed quickly: letters in between, so no taps.
        Assert.False(key.Down(PaletteKey.Mod.Shift, 1000)); key.Down(PaletteKey.Mod.None, 1020); Assert.False(key.Up(PaletteKey.Mod.Shift, 1060));
        Assert.False(key.Down(PaletteKey.Mod.Shift, 1100)); key.Down(PaletteKey.Mod.None, 1120); Assert.False(key.Up(PaletteKey.Mod.Shift, 1160));
        // Too slow between taps.
        key.Down(PaletteKey.Mod.Shift, 2000); key.Up(PaletteKey.Mod.Shift, 2050);
        key.Down(PaletteKey.Mod.Shift, 2600); Assert.False(key.Up(PaletteKey.Mod.Shift, 2650));
        // A long hold is not a tap; auto-repeat of the held key changes nothing.
        key.Down(PaletteKey.Mod.Shift, 4000); key.Down(PaletteKey.Mod.Shift, 4300); key.Up(PaletteKey.Mod.Shift, 4800);
        key.Down(PaletteKey.Mod.Shift, 4900); Assert.False(key.Up(PaletteKey.Mod.Shift, 4950));
        // Ctrl taps do nothing while the key is Shift Shift.
        key.Down(PaletteKey.Mod.Ctrl, 6000); key.Up(PaletteKey.Mod.Ctrl, 6050); key.Down(PaletteKey.Mod.Ctrl, 6100);
        Assert.False(key.Up(PaletteKey.Mod.Ctrl, 6150));

        key.Configure("Ctrl+Shift+P");
        Assert.Equal(PaletteKey.Mod.None, key.Tap);
        Assert.True(key.Matches(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.True(key.Matches("P", ctrl: true, shift: true, alt: false));
        Assert.False(key.Matches("P", ctrl: true, shift: false, alt: false));
        key.Configure("Ctrl Ctrl");
        Assert.Equal(PaletteKey.Mod.Ctrl, key.Tap);
        key.Configure("");
        Assert.Equal(PaletteKey.Mod.None, key.Tap);
        Assert.Null(key.Gesture);
    }

    [Fact]
    public void Typing_narrows_by_words_and_letters_best_first()
    {
        Assert.NotNull(PaletteMatch.Match("term", "Terminals"));
        Assert.NotNull(PaletteMatch.Match("gac", "Git Activity")); // letter by letter
        Assert.Null(PaletteMatch.Match("xyz", "Terminals"));
        Assert.NotNull(PaletteMatch.Match("daily 19", "Daily/2026-11-19 Thursday.md"));
        Assert.Null(PaletteMatch.Match("daily 42", "Daily/2026-11-19 Thursday.md"));
        Assert.True(PaletteMatch.Match("set", "Settings")!.Value.Score > PaletteMatch.Match("set", "Reset Everything")!.Value.Score);
        Assert.Equal([0, 1, 2], PaletteMatch.Match("ter", "Terminals")!.Value.Hits);
    }

    private static List<string> Ran = [];

    private static List<PaletteCommand> Sample() =>
    [
        new("Terminals", "Go to", null, () => Ran.Add("Terminals")),
        new("Settings", "Go to", null, () => Ran.Add("Settings")),
        new("Wiki", "Go to", null, () => Ran.Add("Wiki")),
        new("New Terminal", "Terminal", null, () => Ran.Add("New Terminal")),
        .. Enumerable.Range(1, 3000).Select(i => new PaletteCommand($"Notes/page {i:0000}.md", "Wiki page", null, () => Ran.Add("page " + i))),
    ];

    [AvaloniaFact]
    public void The_palette_narrows_as_you_type_and_runs_the_one_chosen()
    {
        Ran = [];
        var owner = new Window { Width = 900, Height = 600 };
        owner.Show();
        var palette = new CommandPaletteWindow(Sample(), owner, null);
        palette.ShowOver(owner);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Nothing typed: the pages and actions, not the 3000 wiki pages.
        Assert.Equal(["Terminals", "Settings", "Wiki", "New Terminal"], palette.Shown.Select(c => c.Title));

        palette.Filter.Text = "term"; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Terminals", "New Terminal"], palette.Shown.Select(c => c.Title));
        Assert.Equal("Terminals", palette.Selected!.Title);
        palette.Filter.Focus();
        palette.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal("New Terminal", palette.Selected!.Title);
        palette.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["New Terminal"], Ran);
        Assert.False(palette.IsVisible);

        // Thousands of pages: a few letters find one.
        var again = new CommandPaletteWindow(Sample(), owner, null);
        again.ShowOver(owner);
        again.Filter.Text = "page 2718"; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("Notes/page 2718.md", again.Selected!.Title);
        again.Filter.Text = "page"; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(CommandPaletteWindow.MaxRows, again.Shown.Count);
        again.Filter.Focus();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(again.IsVisible);
        Assert.Equal(["New Terminal"], Ran);
        owner.Close();
    }

    [AvaloniaFact]
    public void Shift_twice_in_any_window_opens_it_and_again_closes_it()
    {
        using var h = new WikiHarness(x => File.WriteAllText(x.Full("Home.md"), "# Home\n"), open: "Home.md");
        CommandPalette.Install();
        h.Editor.TextArea.Focus();
        void Tap() { h.Window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift); h.Window.KeyReleaseQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None); }
        Tap(); Tap();
        h.Settle();
        Assert.True(CommandPalette.IsOpen);
        Assert.Equal("", h.Text.Replace("# Home\n", "")); // nothing was typed into the page
        Tap(); Tap(); // from the editor's window again: closes it
        h.Settle();
        Assert.False(CommandPalette.IsOpen);
    }
}
