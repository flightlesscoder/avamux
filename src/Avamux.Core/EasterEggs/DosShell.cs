using System.Globalization;
using System.Text;

namespace Avamux.Core.EasterEggs;

/// <summary>Where the shell writes. Colours are the 16 CGA colours, 0-15.</summary>
public interface IDosConsole
{
    void Write(string text);
    void Color(int fg, int bg);
    void Clear();
    void Beep();
}

/// <summary>A file on the pretend floppy.</summary>
public sealed record DosFile(string Name, string Ext, int Size, DateTime Date, string? Text = null, bool Program = false)
{
    public string Full => Ext.Length > 0 ? $"{Name}.{Ext}" : Name;
}

/// <summary>
/// A make-believe COMMAND.COM for a write-protected floppy in drive A:. It understands the everyday DOS commands
/// and runs the programs on the disk through <c>launch</c>, which returns text to print (or null when a program
/// took over the screen).
/// </summary>
public sealed class DosShell
{
    private readonly IDosConsole _con;
    private readonly Func<string, string[], string?> _launch;
    private readonly Action _exit;
    private readonly List<DosFile> _files;
    private Action<string>? _pending;
    private string? _pendingPrompt;
    private ITextGame? _game;
    private string _promptSpec = "$p$g";
    private bool _echo = true;
    private bool _cleared;

    private const int DiskBytes = 1457664;
    public static readonly DateTime DiskDate = new(1996, 8, 15, 6, 22, 0);

    public DosShell(IDosConsole console, Func<string, string[], string?> launch, Action exit)
    {
        _con = console;
        _launch = launch;
        _exit = exit;
        DateTime D(int m, int d, int h, int min) => new(1996, m, d, h, min, 0);
        _files =
        [
            new("COMMAND", "COM", 54645, new(1994, 5, 31, 6, 22, 0), Program: true),
            new("AUTOEXEC", "BAT", 172, D(8, 15, 21, 4), Autoexec),
            new("CONFIG", "SYS", 96, D(8, 15, 21, 2), ConfigSys),
            new("README", "TXT", 1642, D(8, 15, 23, 59), Readme),
            new("STARFLD", "EXE", 18304, D(3, 2, 14, 10), Program: true),
            new("FLYING", "EXE", 61440, D(4, 1, 9, 0), Program: true),
            new("MAZE", "EXE", 40960, D(5, 12, 16, 45), Program: true),
            new("DEFRAG", "EXE", 75033, D(6, 9, 10, 30), Program: true),
            new("AVAZORK", "EXE", 87412, D(7, 4, 22, 17), Program: true),
            new("HOTDOG", "EXE", 6288, D(7, 20, 12, 0), Program: true),
            new("SOL", "EXE", 180688, D(2, 14, 8, 5), Program: true),
            new("QBASIC", "EXE", 194309, new(1994, 5, 31, 6, 22, 0), Program: true),
            new("CATFISH", "BAS", 29434, D(8, 1, 19, 40), "10 REM Cats throw fish across the city. Run it: QBASIC /RUN CATFISH.BAS"),
            new("AVATREK", "BAS", 41216, D(8, 10, 2, 47), "10 REM Brave cats go where no cat has gone before.\n20 REM Run it: QBASIC /RUN AVATREK.BAS\n30 REM (No dogs were harmed. Several were barked at.)"),
            new("SHMUP", "EXE", 131072, D(8, 12, 1, 23), Program: true),
            new("AOL", "EXE", 311296, D(8, 14, 20, 15), Program: true),
        ];
    }

    public IReadOnlyList<DosFile> Files => _files;

    /// <summary>What to show before the cursor.</summary>
    public string Prompt => _pendingPrompt ?? _game?.Prompt ?? (_echo ? ExpandPrompt() : "");

    public bool InGame => _game is not null;

    public void Start()
    {
        _con.Color(7, 0);
        _con.Write("\nStarting AVA-DOS...\n\n");
        _con.Write("HIMEM is testing extended memory...done.\n");
        _con.Write("Sound Blaster found at A220 I5 D1.\n\n");
        _con.Write("AVA-DOS Version 6.26\n");
        _con.Write("             (C)Copyright avamux 1981-1996.\n\n");
        _con.Write("Type HELP for a list of commands, or DIR to see what is on the disk.\n\n");
    }

    /// <summary>Ctrl+C: abandons the line, a question or a text game.</summary>
    public void Break()
    {
        _con.Write("^C\n");
        if (_pending is not null) { _pending = null; _pendingPrompt = null; }
        else if (_game is not null) { _game = null; _con.Write("\n"); }
    }

    public void Enter(string line)
    {
        _con.Write(line + "\n");
        if (_pending is { } answer)
        {
            _pending = null;
            _pendingPrompt = null;
            answer(line.Trim());
            return;
        }
        if (_game is { } game)
        {
            game.Enter(line);
            if (game.Finished) { _game = null; _con.Color(7, 0); _con.Write("\n"); }
            return;
        }
        var command = line.Trim();
        _cleared = false;
        Run(command);
        if (_echo && command.Length > 0 && !_cleared && _pending is null && _game is null) _con.Write("\n");
    }

    private void Ask(string prompt, Action<string> answer)
    {
        _pendingPrompt = prompt;
        _pending = answer;
    }

    private void AbortRetryFail(string error)
    {
        _con.Write($"\n{error}\n");
        Ask("Abort, Retry, Fail?", a =>
        {
            switch (a.ToUpperInvariant().FirstOrDefault())
            {
                case 'R': AbortRetryFail(error); break;
                case 'F': _con.Write("Fail on INT 24\n\n"); break;
                case 'A': _con.Write("\n"); break;
                default: AbortRetryFail(error); break;
            }
        });
    }

    public void Run(string line)
    {
        if (line.Length == 0) return;
        if (line.StartsWith('@')) line = line[1..];
        // "CD.." and "DIR/W" are fine in DOS without a space.
        var end = 0;
        while (end < line.Length && line[end] is not (' ' or '/')) end++;
        var cmd = line[..end];
        var rest = line[end..].Trim();
        if (cmd.Length > 2 && cmd.StartsWith("CD", StringComparison.OrdinalIgnoreCase) && cmd[2] is '.' or '\\') { rest = cmd[2..]; cmd = "CD"; }
        var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var upper = cmd.ToUpperInvariant();
        if (upper.StartsWith("A:\\", StringComparison.Ordinal)) upper = upper[3..];

        switch (upper)
        {
            case "HELP" or "?": Help(); return;
            case "CLS": _con.Clear(); _cleared = true; return;
            case "VER": _con.Write("\nAVA-DOS Version 6.26\n"); return;
            case "DIR": Dir(rest); return;
            case "TYPE": Type(rest); return;
            case "MEM": Mem(); return;
            case "SET": _con.Write("COMSPEC=A:\\COMMAND.COM\nPATH=A:\\\nPROMPT=" + _promptSpec + "\nBLASTER=A220 I5 D1 T4\nTEMP=A:\\\n"); return;
            case "PROMPT": _promptSpec = rest.Length == 0 ? "$p$g" : rest; return;
            case "ECHO":
                if (rest.Equals("OFF", StringComparison.OrdinalIgnoreCase)) _echo = false;
                else if (rest.Equals("ON", StringComparison.OrdinalIgnoreCase)) _echo = true;
                else _con.Write(rest.Length == 0 ? $"ECHO is {(_echo ? "on" : "off")}\n" : rest.TrimStart('.') + "\n");
                return;
            case "DATE":
                _con.Write($"Current date is {DateTime.Now.ToString("ddd MM-dd-yyyy", CultureInfo.InvariantCulture)}\n");
                Ask("Enter new date (mm-dd-yy): ", a => { if (a.Length > 0) _con.Write("The system clock is write-protected too.\n"); _con.Write("\n"); });
                return;
            case "TIME":
                _con.Write($"Current time is {DateTime.Now.ToString("h:mm:ss.ff", CultureInfo.InvariantCulture)}{(DateTime.Now.Hour < 12 ? "a" : "p")}\n");
                Ask("Enter new time: ", a => { if (a.Length > 0) _con.Write("Time stands still on drive A:.\n"); _con.Write("\n"); });
                return;
            case "CD" or "CHDIR":
                if (rest is "" or "\\" or "." or ".." or "A:\\") { if (rest.Length == 0) _con.Write("A:\\\n"); }
                else _con.Write("Invalid directory\n");
                return;
            case "TREE": _con.Write("Directory PATH listing for Volume AVAMUX\nVolume Serial Number is 1996-0815\nA:.\nNo subdirectories exist\n"); return;
            case "MD" or "MKDIR" or "RD" or "RMDIR" or "DEL" or "ERASE" or "COPY" or "REN" or "RENAME" or "MOVE" or "DELTREE" or "XCOPY" or "EDIT" or "ATTRIB":
                AbortRetryFail("Write protect error writing drive A");
                return;
            case "FORMAT":
                _con.Write("Insert new diskette for drive A:\n");
                Ask("and press ENTER when ready...", _ =>
                {
                    _con.Write("\nChecking existing disk format.\n");
                    AbortRetryFail("Write protect error writing drive A");
                });
                return;
            case "VOL": _con.Write("\n Volume in drive A is AVAMUX\n Volume Serial Number is 1996-0815\n"); return;
            case "DOSKEY": _con.Write("DOSKEY installed.\n"); return;
            case "WIN": _con.Write("Not enough memory to start Windows.\nTry unloading a few toasters first.\n"); return;
            case "SCANDISK" or "CHKDSK":
                var used = _files.Sum(f => f.Size);
                _con.Write(FormattableString.Invariant($"\n{DiskBytes,12:N0} bytes total disk space\n{used,12:N0} bytes in {_files.Count} user files\n{DiskBytes - used,12:N0} bytes available on disk\n\n") +
                           "         512 bytes in each allocation unit\n       2,847 total allocation units on disk\n\nNo problems found. It is a very good disk.\n");
                return;
            case "EXIT": _exit(); return;
            case "C:" or "D:" or "B:": AbortRetryFail($"Not ready reading drive {upper[0]}"); return;
            case "A:": return;
            case "XYZZY": _con.Write("Nothing happens.\n"); return;
            case "GORILLA" or "GORILLA.BAS" or "GORILLAS":
                _con.Write("File not found - GORILLA.BAS\nThe gorillas retired to the country. Their cousins live in CATFISH.BAS.\n");
                return;
        }

        if (upper is "QBASIC" or "QBASIC.EXE")
        {
            var file = args.FirstOrDefault(a => !a.StartsWith('/'))?.ToUpperInvariant();
            if (file is null) { _con.Write("QBASIC needs a program. Try: QBASIC /RUN CATFISH.BAS or QBASIC /RUN AVATREK.BAS\n"); return; }
            if (file.StartsWith("A:\\", StringComparison.Ordinal)) file = file[3..];
            if (!file.EndsWith(".BAS", StringComparison.Ordinal)) file += ".BAS";
            if (file is not ("CATFISH.BAS" or "AVATREK.BAS"))
            {
                _con.Write(file.StartsWith("GORILLA", StringComparison.Ordinal) ? "File not found\nThe gorillas retired. Try CATFISH.BAS.\n"
                    : file.Contains("TREK", StringComparison.Ordinal) ? "File not found\nThe cats renamed it. Try AVATREK.BAS.\n" : "File not found\n");
                return;
            }
            Launch(file[..^4], args);
            return;
        }

        // A program on the disk, with or without its extension. A long name is forgiven: STARFIELD finds STARFLD.
        var name = upper.Contains('.') ? upper[..upper.IndexOf('.')] : upper;
        var ext = upper.Contains('.') ? upper[(upper.IndexOf('.') + 1)..] : "";
        if (name == "STARFIELD") name = "STARFLD";
        if (name is "AVAMUX" or "ONLINE") name = "AOL";
        var hit = _files.FirstOrDefault(f => f.Name == name && (ext.Length == 0 ? f.Ext is "EXE" or "COM" or "BAT" or "BAS" : f.Ext == ext));
        if (hit is null) { _con.Write("Bad command or file name\n"); return; }
        if (hit.Ext == "BAS") { _con.Write("Bad command or file name\n(.BAS files run in QBASIC: QBASIC /RUN " + hit.Full + ")\n"); return; }
        if (hit.Name == "COMMAND") { _con.Write("\nAVA-DOS Version 6.26\n             (C)Copyright avamux 1981-1996.\n"); return; }
        if (hit.Name == "AUTOEXEC") { _promptSpec = "$p$g"; return; }
        if (hit.Ext is "TXT" or "SYS") { _con.Write("Bad command or file name\n"); return; }
        Launch(hit.Name, args);
    }

    private void Launch(string program, string[] args)
    {
        if (program == "AVAZORK")
        {
            _game = new AvaZork(_con);
            _game.Start();
            return;
        }
        if (program == "AVATREK")
        {
            _game = new AvaTrek(_con);
            _game.Start();
            return;
        }
        if (_launch(program, args) is { } text) _con.Write(text);
    }

    private string ExpandPrompt()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _promptSpec.Length; i++)
        {
            if (_promptSpec[i] != '$' || i + 1 >= _promptSpec.Length) { sb.Append(_promptSpec[i]); continue; }
            var c = char.ToLowerInvariant(_promptSpec[++i]);
            sb.Append(c switch
            {
                'p' => "A:\\", 'g' => ">", 'l' => "<", 'n' => "A", 'b' => "|", 'q' => "=", '$' => "$", '_' => "\n",
                'd' => DateTime.Now.ToString("ddd MM-dd-yyyy", CultureInfo.InvariantCulture),
                't' => DateTime.Now.ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture),
                'v' => "AVA-DOS Version 6.26",
                _ => "",
            });
        }
        return sb.ToString();
    }

    private void Help()
    {
        _con.Write("\nAVA-DOS commands:\n\n");
        foreach (var (c, d) in new[]
                 {
                     ("DIR [/W]", "List the files on the disk"), ("TYPE file", "Show a text file (try README.TXT)"), ("CLS", "Clear the screen"),
                     ("VER, VOL, MEM", "About this computer"), ("DATE, TIME", "Show the date and time"), ("ECHO, PROMPT, SET", "The usual"),
                     ("EXIT", "Close COMMAND.COM"), ("name", "Run a program, e.g. STARFLD or SHMUP.EXE"),
                 })
            _con.Write($"  {c,-18}{d}\n");
        _con.Write("\nIn a program, Esc goes back to the prompt. Alt+Enter toggles full screen.\n");
    }

    private void Dir(string rest)
    {
        var wide = rest.Contains("/W", StringComparison.OrdinalIgnoreCase);
        var filter = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(a => !a.StartsWith('/'))?.ToUpperInvariant().Replace("A:\\", "");
        var files = _files.Where(f => filter is null or "" or "*.*" || Matches(f, filter)).ToList();
        _con.Write("\n Volume in drive A is AVAMUX\n Volume Serial Number is 1996-0815\n Directory of A:\\\n\n");
        if (files.Count == 0) { _con.Write("File not found\n"); return; }
        if (wide)
        {
            for (var i = 0; i < files.Count; i++)
                _con.Write(files[i].Full.PadRight(16) + (i % 5 == 4 || i == files.Count - 1 ? "\n" : ""));
        }
        else
            foreach (var f in files)
                _con.Write($"{f.Name,-8} {f.Ext,-3} {f.Size.ToString("N0", CultureInfo.InvariantCulture),13} {f.Date.ToString("MM-dd-yy", CultureInfo.InvariantCulture)}  {f.Date.ToString("h:mm", CultureInfo.InvariantCulture),5}{(f.Date.Hour < 12 ? "a" : "p")}\n");
        var bytes = files.Sum(f => f.Size);
        _con.Write($"{files.Count,9} file(s) {bytes.ToString("N0", CultureInfo.InvariantCulture),14} bytes\n");
        _con.Write($"{"",18}{(DiskBytes - _files.Sum(f => f.Size)).ToString("N0", CultureInfo.InvariantCulture),14} bytes free\n");
    }

    private static bool Matches(DosFile f, string pattern)
    {
        var dot = pattern.IndexOf('.');
        var (n, e) = dot < 0 ? (pattern, "*") : (pattern[..dot], pattern[(dot + 1)..]);
        static bool Part(string value, string p) => p == "*" || (p.EndsWith('*') ? value.StartsWith(p[..^1], StringComparison.Ordinal) : value == p);
        return Part(f.Name, n) && Part(f.Ext, e);
    }

    private void Type(string rest)
    {
        if (rest.Length == 0) { _con.Write("Required parameter missing\n"); return; }
        var want = rest.ToUpperInvariant().Replace("A:\\", "");
        var f = _files.FirstOrDefault(x => x.Full == want);
        if (f is null) { _con.Write("File not found - " + want + "\n"); return; }
        if (f.Text is { } text) { _con.Write(text.Replace("\r", "") + "\n"); return; }
        // A program printed as text: the classic screenful of nonsense, and a beep from the bell character.
        var rnd = new Random(f.Name.GetHashCode());
        var sb = new StringBuilder("MZ");
        const string junk = "☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼ !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■";
        for (var i = 0; i < 420; i++) sb.Append(junk[rnd.Next(junk.Length)]);
        _con.Write(sb + "\n");
        _con.Beep();
    }

    private void Mem() => _con.Write("""

        Memory Type        Total  =   Used  +   Free
        ----------------  -------   -------   -------
        Conventional         640K       38K      602K
        Upper                155K      155K        0K
        Reserved             384K      384K        0K
        Extended (XMS)    15,205K    2,113K   13,092K
        ----------------  -------   -------   -------
        Total memory      16,384K    2,690K   13,694K

        Largest executable program size       602K (616,448 bytes)
        AVA-DOS is resident in the high memory area.

        """.Replace("\r", ""));

    private const string Autoexec = """
        @ECHO OFF
        PROMPT $P$G
        PATH A:\
        SET BLASTER=A220 I5 D1 T4
        LH MOUSE.COM
        REM If you can read this, you found drive A:.
        """;

    private const string ConfigSys = """
        DEVICE=HIMEM.SYS
        DOS=HIGH,UMB
        FILES=40
        BUFFERS=20
        LASTDRIVE=Z
        """;

    private const string Readme = """
        ================================================================
          THE AVAMUX FUN PACK, VOLUME 1           (C) 1996 avamux soft
        ================================================================

        Thank you for finding this disk! It contains:

          STARFLD.EXE   Fly through the stars. (Screen saver)
          FLYING.EXE    Toasters. With wings. (Screen saver)
          MAZE.EXE      Wander a brick maze looking for logos. (Screen saver)
          DEFRAG.EXE    Tidy up drive A: and watch the blocks move.
          AVAZORK.EXE   A text adventure: get online, 1996 style.
          HOTDOG.EXE    A colour scheme you will never forget.
                        HOTDOG /U puts your old colours back.
          SOL.EXE       The best part of a card game: winning.
          CATFISH.BAS   Cats throw fish across the city. QBASIC /RUN CATFISH.BAS
          AVATREK.BAS   Brave cats go where no cat has gone before.
                        QBASIC /RUN AVATREK.BAS
          SHMUP.EXE     NEBULA BLASTER '96. Blast everything.
          AOL.EXE       Avamux On-Line. Get on the information
                        superhighway! (Mouse required.)

        Screen savers stop with any key. Esc leaves any program.
        Alt+Enter switches full screen on and off.

        This disk is write-protected. Please do not try to format it.
        """;
}

/// <summary>A game played by typing lines at a prompt (AVAZORK, AVATREK).</summary>
public interface ITextGame
{
    string Prompt { get; }
    bool Finished { get; }
    void Start();
    void Enter(string line);
}
