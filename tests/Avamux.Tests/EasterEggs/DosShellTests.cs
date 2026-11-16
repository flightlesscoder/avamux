#if EASTER_EGGS
using System.Text;
using Avamux.Core.EasterEggs;

namespace Avamux.Tests.EasterEggs;

public class DosShellTests
{
    private sealed class Screen : IDosConsole
    {
        public readonly StringBuilder Text = new();
        public int Beeps;
        public void Write(string text) => Text.Append(text);
        public void Color(int fg, int bg) { }
        public void Clear() => Text.Clear();
        public void Beep() => Beeps++;
        public string Take() { var s = Text.ToString(); Text.Clear(); return s; }
    }

    [Fact]
    public void Dir_type_and_errors_behave_like_dos()
    {
        var screen = new Screen();
        var launched = new List<string>();
        var exited = false;
        var shell = new DosShell(screen, (p, _) => { launched.Add(p); return p == "HOTDOG" ? "Mustard applied.\n" : null; }, () => exited = true);
        shell.Start();
        Assert.Equal("A:\\>", shell.Prompt);

        shell.Enter("dir");
        var dir = screen.Take();
        Assert.Contains("STARFLD  EXE", dir);
        Assert.Contains("CATFISH  BAS", dir);
        Assert.Contains("16 file(s)", dir);
        Assert.Contains("AVATREK  BAS", dir);
        Assert.All(shell.Files, f => Assert.True(f.Name.Length <= 8 && f.Ext.Length <= 3, f.Full));

        shell.Enter("DIR/W *.EXE");
        Assert.Contains("[", screen.Text.ToString() + "[");
        Assert.DoesNotContain("README.TXT", screen.Take());

        shell.Enter("type readme.txt");
        Assert.Contains("NEBULA BLASTER", screen.Take());
        shell.Enter("type sol.exe");
        Assert.StartsWith("type sol.exe\nMZ", screen.Take());
        Assert.Equal(1, screen.Beeps);

        shell.Enter("foo");
        Assert.Contains("Bad command or file name", screen.Take());

        shell.Enter("del command.com");
        Assert.Contains("Write protect error", screen.Take());
        Assert.Equal("Abort, Retry, Fail?", shell.Prompt);
        shell.Enter("r");
        Assert.Equal("Abort, Retry, Fail?", shell.Prompt);
        shell.Enter("a");
        Assert.Equal("A:\\>", shell.Prompt);

        shell.Enter("starfield");
        shell.Enter("a:\\shmup.exe");
        shell.Enter("qbasic /run catfish.bas");
        shell.Enter("hotdog");
        Assert.Equal(["STARFLD", "SHMUP", "CATFISH", "HOTDOG"], launched);
        Assert.Contains("Mustard applied.", screen.Take());

        shell.Enter("prompt $n$g");
        Assert.Equal("A>", shell.Prompt);
        shell.Enter("cd..");
        Assert.DoesNotContain("Invalid", screen.Take());

        shell.Enter("exit");
        Assert.True(exited);
    }

    [Fact]
    public void Avazork_can_be_won_with_every_point()
    {
        var screen = new Screen();
        var shell = new DosShell(screen, (_, _) => null, () => { });
        shell.Enter("avazork");
        Assert.True(shell.InGame);
        Assert.Equal(">", shell.Prompt);
        string[] walkthrough =
        [
            "e", "s", "talk to mum", "open drawer", "take flashlight", "n", "d", "take cord", "turn on the flashlight", "pick up the cord", "up", "s", "e",
            "open box", "get modem", "w", "take trash", "e", "put trash in bin", "w", "n", "e", "search couch", "take quarter", "n", "open mailbox",
            "take cd", "s", "w", "w", "dial", "connect modem", "insert cd", "dial",
        ];
        foreach (var line in walkthrough) shell.Enter(line);
        var text = screen.Take();
        Assert.Contains("grope around in the dark", text); // the cord can't be found without a light
        Assert.Contains("You are online", text);
        Assert.Contains("You scored 100 out of 100", text);
        Assert.False(shell.InGame);
        Assert.Equal("A:\\>", shell.Prompt);
    }

    [Fact]
    public void Avatrek_runs_in_qbasic()
    {
        var screen = new Screen();
        var shell = new DosShell(screen, (_, _) => null, () => { });
        shell.Enter("avatrek");
        Assert.Contains("QBASIC /RUN AVATREK.BAS", screen.Take());
        shell.Enter("qbasic /run startrek.bas");
        Assert.Contains("Try AVATREK.BAS", screen.Take());
        shell.Enter("qbasic /run avatrek");
        Assert.True(shell.InGame);
        Assert.Contains("A V A T R E K", screen.Take());
        shell.Enter("");
        Assert.Equal("Command? ", shell.Prompt);
        Assert.Contains("Stardate", screen.Take());
        shell.Enter("quit");
        Assert.False(shell.InGame);
        Assert.Contains("at the end of your mission", screen.Take());
    }

    [Fact]
    public void Avazork_mum_blocks_the_line_until_the_trash_is_out()
    {
        var screen = new Screen();
        var game = new AvaZork(screen);
        game.Start();
        foreach (var line in new[] { "e", "s", "open drawer", "take flashlight", "n", "d", "turn on flashlight", "take cord", "u", "s", "e", "open box", "take modem", "w", "n", "e", "n", "open mailbox", "take cd", "s", "w", "w", "connect modem", "insert cd", "dial" })
            game.Enter(line);
        Assert.Contains("GET OFF THE PHONE", screen.Take());
        Assert.False(game.Finished);
        game.Enter("hint");
        Assert.Contains("trash", screen.Take());
    }
}
#endif
