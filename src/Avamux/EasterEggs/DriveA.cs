using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avamux.Core.Config;
using Avamux.Views;

namespace Avamux.EasterEggs;

/// <summary>What drive A: remembers between runs: whether it was found, the theme before HOTDOG, high scores.</summary>
public sealed class DriveAState
{
    public bool Unlocked { get; set; }
    public string? ThemeBeforeHotdog { get; set; }
    public int ShmupHighScore { get; set; }
    public string? AolScreenName { get; set; }

    private static string File => App.Services.Paths.StateFile("drive-a");
    private static DriveAState? _current;

    public static DriveAState Current
    {
        get
        {
            if (_current is not null) return _current;
            try { _current = System.IO.File.Exists(File) ? JsonSerializer.Deserialize<DriveAState>(System.IO.File.ReadAllText(File)) : null; }
            catch { /* a damaged file starts over */ }
            return _current ??= new DriveAState();
        }
    }

    public void Save()
    {
        try { System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Core.Logging.Log.Warn("drive-a", "Could not save: " + ex.Message); }
    }
}

/// <summary>
/// The secret "Drive A:\" section of Settings. Typing the old arcade code (up up down down left right left right
/// B A) while the settings search box has focus reveals it; it holds a button that opens COMMAND.COM.
/// </summary>
public static class DriveA
{
    private static readonly Key[] Code = [Key.Up, Key.Up, Key.Down, Key.Down, Key.Left, Key.Right, Key.Left, Key.Right, Key.B, Key.A];

    public static void Attach(TextBox search, StackPanel body, List<Control> hiddenWhileSearching, Action clearSearch)
    {
        var card = BuildCard(out var eject);
        card.IsVisible = DriveAState.Current.Unlocked;
        body.Children.Add(card);
        // The card shows only when not searching, and only once found.
        var wrapper = new Border { Child = null };
        hiddenWhileSearching.Add(wrapper);
        wrapper.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) card.IsVisible = wrapper.IsVisible && DriveAState.Current.Unlocked; };
        eject.Click += (_, _) =>
        {
            DriveAState.Current.Unlocked = false;
            DriveAState.Current.Save();
            card.IsVisible = false;
            Ui.Toast(card, "Drive A: ejected", "The disk is back in its sleeve. You remember how to find it.");
        };

        var progress = 0;
        search.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            var expected = Code[progress];
            if (e.Key == expected)
            {
                progress++;
                // B and A would otherwise be typed into the search box.
                if (expected is Key.B or Key.A) e.Handled = true;
                if (progress < Code.Length) return;
                progress = 0;
                Unlocked(card, clearSearch);
            }
            else progress = e.Key == Code[0] ? 1 : 0;
        }, RoutingStrategies.Tunnel);
    }

    private static void Unlocked(Control card, Action clearSearch)
    {
        var state = DriveAState.Current;
        if (state.Unlocked) { DosWindow.Open(card); return; }
        state.Unlocked = true;
        state.Save();
        clearSearch();
        card.IsVisible = true;
        DosScreen.Beep();
        Ui.Toast(card, "Drive A: is ready", "Something was down the back of the settings.");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => card.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private static readonly Geometry Floppy = Icons.ContentSave;

    private static Border BuildCard(out Button eject)
    {
        var run = Ui.IconButton(Icons.Console, "Boot from drive A:", () => { }, "Run COMMAND.COM");
        run.Classes.Add("accent");
        eject = Ui.IconButton(Icons.Close, "Put the disk away and hide this section again", () => { }, "Eject");
        var card = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Row(10, new PathIcon { Data = Floppy, Width = 22, Height = 22 }, Ui.Text("Drive A:\\", "h2")),
                Ui.Text("A 3½-inch floppy disk was hiding down the back of the settings. 1.44 MB, write-protected, label reads \"FUN PACK VOL. 1\".", "dim", 12.5),
                Ui.Row(8, run, eject),
            },
        });
        run.Click += (_, _) => DosWindow.Open(card);
        return card;
    }

    // ----- Programs on the disk -----

    public static DosProgram? Program(string name, string[] args) => name switch
    {
        "STARFLD" => new Starfield(),
        "FLYING" => new Toasters(),
        "MAZE" => new Maze(),
        "DEFRAG" => new Defrag(),
        "SOL" => new Solitaire(),
        "CATFISH" => new Catfish(),
        "SHMUP" => new Shmup(),
        "AOL" => new Aol(),
        _ => null,
    };

    /// <summary>Programs that only print something.</summary>
    public static string? TextCommand(string name, string[] args) => name == "HOTDOG" ? Hotdog(args.Any(a => a.Equals("/U", StringComparison.OrdinalIgnoreCase))) : null;

    private const string HotdogId = "hotdog";

    private const string HotdogTheme = """
        # Written by HOTDOG.EXE from drive A:. Run HOTDOG /U to go back.
        name: Hot Dog Cart
        description: Ketchup and mustard. Sunglasses recommended.
        base: classic90s
        light: true
        colors:
          bg: "#ffff00"
          bgDeep: "#ff0000"
          bgEditor: "#ffff00"
          bgSide: "#ff0000"
          panel: "#ff0000"
          panelHi: "#ffff00"
          cell: "#ffff00"
          cellSel: "#ff0000"
          line: "#000000"
          lineSoft: "#000000"
          lineStrong: "#000000"
          scroll: "#000000"
          text: "#000000"
          textStrong: "#000000"
          textSoft: "#000000"
          textMute: "#000000"
          textCode: "#000000"
          dim: "#202020"
          faint: "#400000"
          ghost: "#600000"
          accent: "#000000"
          link: "#000000"
          linkHi: "#ffffff"
          accentDeep: "#000000"
          onAccent: "#ffffff"
          termBg: "#ff0000"
          termFg: "#ffff00"
          termSel: "#000000"
          titleBg: "#ff0000"
          titleFg: "#ffffff"
        """;

    private static string Hotdog(bool undo)
    {
        var state = DriveAState.Current;
        var config = App.Services.Config;
        var current = config.Settings.Theme.Name;
        if (undo)
        {
            if (!string.Equals(current, HotdogId, StringComparison.OrdinalIgnoreCase)) return "HOTDOG is not running. Your colours are your own.\n";
            config.Set("theme.name", JsonValue.Create(state.ThemeBeforeHotdog ?? "simple"), ConfigScope.Home);
            return $"Restoring the colour scheme \"{state.ThemeBeforeHotdog ?? "simple"}\"... done.\nThe world is beige again.\n";
        }
        if (!string.Equals(current, HotdogId, StringComparison.OrdinalIgnoreCase))
        {
            state.ThemeBeforeHotdog = current;
            state.Save();
        }
        App.Services.ThemeFiles.SaveText(HotdogId, HotdogTheme, "yaml");
        config.Set("theme.name", JsonValue.Create(HotdogId), ConfigScope.Home);
        return "HOTDOG.EXE  Colour Scheme Installer v1.0\n\nFrying the palette........ done.\nAdding mustard............ done.\nApplying \"Hot Dog Cart\"... done.\n\nHave a look at avamux. Type HOTDOG /U to undo.\n";
    }
}

/// <summary>The COMMAND.COM window. One at a time.</summary>
public sealed class DosWindow : AppWindow
{
    private static DosWindow? _open;
    private readonly DosScreen _screen;

    private DosWindow()
    {
        Title = "COMMAND.COM - A:\\";
        Width = 960;
        Height = 720;
        MinWidth = 400;
        MinHeight = 320;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _screen = new DosScreen(DriveA.Program, DriveA.TextCommand, Close);
        Body = _screen;
        Opened += (_, _) => _screen.Focus();
        Activated += (_, _) => _screen.Focus();
        Closed += (_, _) => _open = null;
    }

    public static void Open(Control from)
    {
        if (_open is { } w) { w.Activate(); return; }
        _open = new DosWindow();
        if (TopLevel.GetTopLevel(from) is Window owner) _open.Show(owner);
        else _open.Show();
    }
}
