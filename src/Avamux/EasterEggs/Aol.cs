using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avamux.Core.EasterEggs;
using Avamux.Views;

namespace Avamux.EasterEggs;

/// <summary>
/// AOL.EXE: Avamux On-Line. Boots a pretend 16-colour windowed desktop, signs on through a dial-up modem (with the
/// noises), and then offers a Welcome page of 1996 headlines, a mailbox, a buddy list with instant messages, and
/// chat rooms full of people saying "a/s/l" in spirit if not in letter.
/// </summary>
public sealed class Aol : DosProgram
{
    private readonly Desktop _desk = new();
    private readonly Random _rnd = new();
    private CancellationTokenSource _sounds = new();
    private double _boot;
    private bool _booted;
    private string _me = "NetSurfer96";

    private Wnd _frame = null!;
    private Wnd? _signOn, _welcome, _buddyWin, _mailWin;
    private ListPane? _buddyList, _mailList;
    private bool _online, _triedOnce;
    private int _session, _cascade;
    private double _onlineAt, _usedSeconds, _mailFlash;
    private readonly List<AolBuddy> _buddies = AolTalk.NewBuddies();
    private readonly Dictionary<AolBuddy, (double At, bool On)> _doors = new();
    private List<AolMail> _mail = [];
    private readonly Dictionary<AolBuddy, (Wnd Win, ChatLog Log)> _ims = new();
    private (Wnd Win, ChatLog Log, ListPane People, AolRoom Room)? _room;

    private const int ToolbarH = 38;

    public override bool WantsMouse => true;
    public override bool HandlesEscape => true;

    public override void Start()
    {
        _desk.Beep = DosScreen.Beep;
        _me = DriveAState.Current.AolScreenName ?? "NetSurfer96";
        _desk.Icons.Add(new DeskIcon { Label = "Program Manager", Art = Pics.ProgMan, Open = () => _desk.MessageBox("Program Manager", "There is not enough memory to start Program Manager.\n\nClose Avamux On-Line and try again. Or don't.", Desktop.Icon.Warning) });
        _desk.Icons.Add(new DeskIcon { Label = "Clock", Paint = (g, r) => DrawClock(g, r.Deflate(1)), Open = OpenClock });
        _desk.Icons.Add(new DeskIcon { Label = "AVA-DOS Prompt", Art = Pics.Dos, Open = ConfirmExit });
    }

    public override void Stop()
    {
        StopSounds();
        _desk.Busy = false;
    }

    private void Boot()
    {
        _booted = true;
        _frame = new Wnd
        {
            Title = "Avamux On-Line", MinMax = true, KeepBottom = true, Back = W3.Gray, IconArt = Pics.Globe,
            Bounds = new Rect(4, 4, 632, 340), Closing = () => { ConfirmExit(); return false; },
        };
        _frame.ChildArea = () => { var c = _frame.Client; return new Rect(c.X, c.Y + ToolbarH, c.Width, c.Height - ToolbarH); };
        _frame.MenuBar =
        [
            new Menu("File", () => [new("New"), new("Open..."), new("Save"), new("-"), new("Sign Off", () => SignOff(), () => _online), new("Exit", ConfirmExit)]),
            new Menu("Edit", () => [new("Undo"), new("-"), new("Cut"), new("Copy"), new("Paste")]),
            new Menu("Go To", () =>
            [
                new("Welcome", OpenWelcome, On), new("Keyword...", Keyword, On, "Ctrl+K"), new("-"),
                new("Chat Rooms", OpenRoomList, On), new("Buddy List", OpenBuddies, On), new("Today's News", OpenNews, On), new("Games", OpenGames, On),
            ]),
            new Menu("Mail", () => [new("Read New Mail", OpenMailbox, On), new("Compose Mail", () => Compose(), On, "Ctrl+M")]),
            new Menu("Members", () =>
            [
                new("Edit Profile", () => _desk.MessageBox("My Profile", $"Screen Name: {_me}\nLocation: drive A:\nHobbies: finding easter eggs\nComputer: AVA-DOS 6.26 on a floppy", Desktop.Icon.Info), On),
                new("Member Directory", () => _desk.MessageBox("Member Directory", "Searching 4,812,330 members...\n\nThe Member Directory is busy. Please try again after midnight, when the rates are lower.", Desktop.Icon.Info), On),
                new("-"),
                new("Preferences", () => _desk.MessageBox("Preferences", "Modem: Hayes compatible, 28,800 bps on COM2\nNumber: 555-0196\nSpeaker: ON (of course)", Desktop.Icon.Info)),
            ]),
            new Menu("Window", () =>
            {
                var items = new List<MenuItem> { new("Cascade", Cascade, () => Children().Any()), new("Close All", () => { foreach (var c in Children().ToList()) if (c.Closable && !c.Modal) c.Close(); }, () => Children().Any()) };
                var open = Children().Where(c => !c.Modal).ToList();
                if (open.Count > 0) items.Add(new("-"));
                for (var i = 0; i < open.Count; i++) { var w = open[i]; items.Add(new($"{i + 1} {w.Title}", () => _desk.Activate(w))); }
                return items;
            }),
            new Menu("Help", () => [new("About Avamux On-Line...", About)]),
        ];
        _frame.Paint = (g, c) =>
        {
            W3.Raised(g, new Rect(c.X, c.Y, c.Width, ToolbarH), false, 1);
            W3.Fill(g, c.X, c.Y + ToolbarH - 1, c.Width, 1, W3.Black);
            // A faint logo in the empty workspace.
            var area = _frame.ChildArea!();
            Txt.Centre(g, "Avamux On-Line", new Rect(area.X, area.Bottom - 40, area.Width, 40), TF.Logo, W3.Silver);
        };
        (string[] Art, string Label, Action Do, bool Always)[] tools =
        [
            (Pics.Mail, "Mail", OpenMailbox, false), (Pics.Write, "Write", () => Compose(), false), (Pics.Home, "Welcome", OpenWelcome, false),
            (Pics.Chat, "Chat", () => EnterRoom(AolTalk.Rooms[0]), false), (Pics.Buddy, "Buddies", OpenBuddies, false), (Pics.Key, "Keyword", Keyword, false),
            (Pics.News, "News", OpenNews, false), (Pics.Games, "Games", OpenGames, false), (Pics.Globe, "Web", Web, false),
            (Pics.Help, "Help", About, true), (Pics.Door, "Sign Off", () => SignOff(), false),
        ];
        for (var i = 0; i < tools.Length; i++)
        {
            var t = tools[i];
            _frame.Add(new ToolButton(t.Art, t.Label, t.Do) { R = new Rect(4 + i * 52, 3, 50, ToolbarH - 7), EnabledIf = t.Always ? null : () => _online });
        }
        _desk.Open(_frame);
        OpenSignOn();
    }

    private bool On() => _online;
    private IEnumerable<Wnd> Children() => _desk.Windows.Where(w => w.Owner == _frame);

    private void Later(double seconds, Action a)
    {
        var session = _session;
        _desk.After(seconds, () => { if (session == _session) a(); });
    }

    private static double R(Random rnd, double a, double b) => a + rnd.NextDouble() * (b - a);

    // ----- Sound -----

    private void Play(string name, Func<float[]> synth) => App.Services.Sounds.PlayClip(name, synth, _sounds.Token);

    private void StopSounds()
    {
        _sounds.Cancel();
        _sounds = new CancellationTokenSource();
    }

    // ----- The program's own loop -----

    public override void Update(double dt)
    {
        if (!_booted)
        {
            _boot += dt;
            if (_boot > 2.8) Boot();
            return;
        }
        _mailFlash = Math.Max(0, _mailFlash - dt);
        _desk.Update(dt);
    }

    public override void Draw(DrawingContext g)
    {
        using (g.PushTextOptions(Txt.Aliased))
        using (g.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }))
        {
            if (_booted) _desk.Draw(g);
            else Splash(g);
        }
    }

    private void Splash(DrawingContext g)
    {
        // First the screen goes black while the video mode changes, then the logo.
        W3.Fill(g, new Rect(0, 0, Desktop.W, Desktop.H), _boot < 0.5 ? W3.Black : W3.White);
        if (_boot < 0.5) return;
        DrawLogo(g, new Rect(280, 50, 80, 80));
        Txt.Centre(g, "AVA-WIN", new Rect(0, 140, Desktop.W, 60), TF.Splash, W3.Navy);
        Txt.Centre(g, "Version 3.1", new Rect(0, 206, Desktop.W, 20), TF.Big, W3.Black);
        Txt.Centre(g, "Copyright \u00a9 1985-1996 avamux soft. All rights reserved.", new Rect(0, 350, Desktop.W, 16), TF.Small, W3.Gray);
        if (_boot > 1.2) Art.Draw(g, Art.Hourglass, _desk.Mouse.X - 5, _desk.Mouse.Y - 7);
    }

    private static readonly Bitmap? Logo = AppIcon.Bitmap;

    private static void DrawLogo(DrawingContext g, Rect r)
    {
        if (Logo is null) { Txt.Centre(g, "A", r, TF.Splash, W3.Navy); return; }
        using (g.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            g.DrawImage(Logo, new Rect(Logo.Size), r);
    }

    public override void KeyDown(Key key, KeyModifiers mods)
    {
        if (!_booted) { if (_boot > 0.5) _boot = 99; return; }
        if (mods.HasFlag(KeyModifiers.Control) && _online && _desk.TopModal() is null)
        {
            if (key == Key.K) { Keyword(); return; }
            if (key == Key.M) { Compose(); return; }
        }
        if (_desk.OnKey(key, mods)) return;
        if (key == Key.Escape && _desk.TopModal() is null) ConfirmExit();
    }

    public override void TextInput(string text) { if (_booted) _desk.Text(text); }
    public override void PointerMoved(Point p) => _desk.Move(p);

    public override void PointerPressed(Point p, int clicks)
    {
        if (!_booted) { if (_boot > 0.5) _boot = 99; return; }
        _desk.Press(p, clicks);
    }

    public override void PointerReleased(Point p) { if (_booted) _desk.Release(p); }
    public override void Wheel(Point p, double dy) { if (_booted) _desk.Wheel(p, dy); }

    private void ConfirmExit()
    {
        if (_desk.Windows.Any(w => w.Title == "Exit AVA-WIN")) return;
        _desk.MessageBox("Exit AVA-WIN", "This will end your AVA-WIN session.", Desktop.Icon.Warning, ("OK", Exit), ("Cancel", null)).Title = "Exit AVA-WIN";
    }

    private void Exit()
    {
        if (_online) { _usedSeconds += _desk.Time - _onlineAt; _online = false; }
        StopSounds();
        if (_usedSeconds > 0)
        {
            var used = (int)Math.Ceiling(_usedSeconds / 60);
            var left = 50 * 60 - used;
            ExitMessage = $"Thank you for using Avamux On-Line!\nTime on-line: {used} min. Free trial time left: {left / 60} hours, {left % 60} minutes.";
        }
        Done = true;
    }

    // ----- Windows inside Avamux On-Line -----

    private Wnd Child(string title, double w, double h, Color? back = null, bool activate = true)
    {
        var win = new Wnd { Title = title, Owner = _frame, Back = back ?? W3.Silver }.Sized(w, h);
        var area = _frame.ChildArea!();
        var k = _cascade++ % 6;
        win.Bounds = new Rect(area.X + 10 + k * 18, area.Y + 6 + k * 14, win.Bounds.Width, win.Bounds.Height);
        return _desk.Open(win, activate: activate);
    }

    private void Cascade()
    {
        var area = _frame.ChildArea!();
        var k = 0;
        foreach (var w in Children().Where(c => !c.Modal).ToList())
        {
            _desk.MoveTo(w, _desk.Keep(w, new Rect(area.X + 6 + k * 18, area.Y + 4 + k * 14, w.Bounds.Width, w.Bounds.Height)));
            _desk.Activate(w);
            k++;
        }
    }

    private static void Label(DrawingContext g, Rect c, string text, double x, double y, TF f = TF.Sys, Color? color = null) =>
        Txt.Draw(g, text, c.X + x, c.Y + y, f, color ?? W3.Black);

    private static void Groove(DrawingContext g, Rect c, double y)
    {
        W3.Fill(g, c.X + 6, c.Y + y, c.Width - 12, 1, W3.Gray);
        W3.Fill(g, c.X + 6, c.Y + y + 1, c.Width - 12, 1, W3.White);
    }

    // ----- Signing on -----

    private void OpenSignOn()
    {
        if (_signOn is not null) { _desk.Activate(_signOn); return; }
        var w = new Wnd { Title = "Welcome", Owner = _frame, Back = W3.Silver, Closable = false }.Sized(380, 214);
        _signOn = w;
        w.Closed = () => _signOn = null;
        var name = w.Add(new EditField { R = new Rect(130, 94, 236, 24), Max = 16 });
        name.Set(_me);
        var password = w.Add(new EditField { R = new Rect(130, 122, 236, 24), Max = 16, Password = true });
        w.Focused = password;
        w.Paint = (g, c) =>
        {
            DrawLogo(g, new Rect(c.X + 14, c.Y + 12, 64, 64));
            Label(g, c, "Avamux", 92, 6, TF.Logo, W3.Navy);
            Label(g, c, "On-Line", 124, 32, TF.Logo, W3.Maroon);
            Label(g, c, "for AVA-WIN  \u2022  Version 2.6", 94, 64, TF.Small, W3.Gray);
            Groove(g, c, 84);
            Label(g, c, "Screen Name:", 14, 98);
            Label(g, c, "Password:", 14, 126);
            Label(g, c, "Location:", 14, 154);
            Label(g, c, "Home (28,800 bps)", 132, 155, TF.Small);
        };
        w.Add(new PushButton("Setup", () => _desk.MessageBox("Setup", "Modem: Hayes compatible, 28,800 bps\nPort: COM2, IRQ 3\nNumber: 555-0196\n\nEverything looks fine. Probably.", Desktop.Icon.Info)) { R = new Rect(14, 180, 80, 26) });
        w.Default = w.Add(new PushButton("Sign On", () => SignOn(name.Value.Trim(), password.Value)) { R = new Rect(142, 180, 96, 26) });
        w.Add(new PushButton("Help", () => _desk.MessageBox("Help", "Type a screen name and any password, then click Sign On.\n\nMake sure nobody needs the phone.", Desktop.Icon.Info)) { R = new Rect(286, 180, 80, 26) });
        _desk.Open(w, centre: true);
    }

    private void SignOn(string name, string password)
    {
        if (name.Length == 0) { _desk.MessageBox("Avamux On-Line", "Please type a screen name.", Desktop.Icon.Warning); return; }
        if (password.Length == 0) { _desk.MessageBox("Avamux On-Line", "Please type your password.\n\n(Any password will do. It is 1996; nobody is checking.)", Desktop.Icon.Warning); return; }
        _me = name;
        DriveAState.Current.AolScreenName = name;
        DriveAState.Current.Save();

        string[] steps =
        [
            "Step 1: Initializing modem", "Step 2: Dialing 555-0196", "Step 3: Waiting for an answer", "Step 4: Handshaking...",
            "Step 5: Connected at 28,800 bps", "Step 6: Requesting network attention", "Step 7: Checking password", "Step 8: Loading Avamux On-Line",
        ];
        // The first try of the day finds the line busy, some of the time.
        var busy = !_triedOnce && _rnd.Next(3) == 0;
        _triedOnce = true;
        var offset = busy ? AolSounds.BusyLength + 0.8 : 0;
        var started = !busy;
        var t = 0.0;
        var status = steps[0];
        var panel = 0;
        var w = new Wnd { Title = "Avamux On-Line", Owner = _frame, Modal = true, DialogFrame = true, Closable = false, Back = W3.Silver }.Sized(372, 150);
        string[][] pictures = [Pics.Modem, Pics.Phone, Pics.Globe];
        w.Paint = (g, c) =>
        {
            for (var i = 0; i < 3; i++)
            {
                var box = new Rect(c.X + 14 + i * 118, c.Y + 10, 104, 70);
                var lit = i <= panel;
                W3.Sunken(g, box, lit ? W3.White : W3.Silver);
                // The step in progress blinks, as if the picture were thinking.
                var blink = i == panel && _desk.Time % 0.8 < 0.25;
                Art.Draw(g, pictures[i], box.X + 28, box.Y + 11, 3, !lit || blink);
            }
            Txt.Centre(g, status, new Rect(c.X, c.Y + 86, c.Width, 20), TF.Sys, W3.Black);
        };
        w.Add(new PushButton("Cancel", () =>
        {
            StopSounds();
            _desk.Busy = false;
            w.Close();
        }) { R = new Rect(146, 114, 80, 26) });
        w.Escape = () => w.Widgets[0].OnKey(Key.Enter, KeyModifiers.None);
        w.Tick = dt =>
        {
            t += dt;
            if (!started && t >= offset) { started = true; Play("aol-modem-1", () => AolSounds.Modem(false)); }
            if (t < offset)
            {
                status = t < AolSounds.ModemSteps[1] ? steps[0] : t < AolSounds.ModemSteps[2] ? steps[1] : t < AolSounds.BusyLength ? "The number is busy." : "Redialing...";
                panel = 0;
                return;
            }
            var local = t - offset;
            var step = Array.FindLastIndex(AolSounds.ModemSteps, s => s <= local);
            if (step >= steps.Length)
            {
                w.Close();
                GoOnline();
                return;
            }
            status = steps[step];
            panel = step <= 2 ? 0 : step <= 4 ? 1 : 2;
        };
        _desk.Busy = true;
        _desk.Open(w, centre: true);
        Play(busy ? "aol-busy-1" : "aol-modem-1", () => AolSounds.Modem(busy));
    }

    private void GoOnline()
    {
        _desk.Busy = false;
        _online = true;
        _onlineAt = _desk.Time;
        _signOn?.Close();
        _mail = AolTalk.NewMail(_me);
        foreach (var b in _buddies) { b.Online = false; b.Heard = 0; }
        foreach (var b in _buddies.OrderBy(_ => _rnd.Next()).Take(3)) b.Online = true;
        _doors.Clear();
        Play("aol-welcome-voice-1", AolSounds.WelcomeVoice);
        Later(1.4, () => { Play("aol-mail-voice-1", AolSounds.MailVoice); _mailFlash = 3.5; });
        OpenWelcome();
        OpenBuddies();
        _desk.Activate(_welcome!);
        BuddyComings();
        Later(R(_rnd, 18, 30), SurpriseIm);
    }

    private void SignOff(string? lost = null)
    {
        if (!_online) return;
        _usedSeconds += _desk.Time - _onlineAt;
        var minutes = (int)Math.Ceiling((_desk.Time - _onlineAt) / 60);
        _online = false;
        _session++;
        StopSounds();
        foreach (var c in Children().ToList()) _desk.Close(c, true);
        _ims.Clear();
        _room = null;
        OpenSignOn();
        if (lost is not null) _desk.MessageBox("Avamux On-Line", lost, Desktop.Icon.Stop);
        else _desk.MessageBox("Avamux On-Line", $"Goodbye from Avamux On-Line!\n\nYou were on-line for {minutes} minute{(minutes == 1 ? "" : "s")}.", Desktop.Icon.Info);
    }

    // ----- Welcome -----

    private static readonly (string Title, string Body)[] Stories =
    [
        ("Virtual pets: 62% survive the week", "A survey of 400 schoolchildren has found that almost two thirds of virtual pets lived through the whole of last week, a record.\n\nExperts credit the pause button, and teachers, who looked after many of the pets in their desk drawers between nine and three.\n\n\"Mine evolved twice,\" said one owner. \"Then it ran away.\""),
        ("Is your PC ready for the year 2000?", "Many computers keep only the last two digits of the year. When 99 becomes 00, they may decide it is 1900 and refuse to believe in fax machines.\n\nOur experts advise readers not to panic, to keep a copy of everything on floppy disk, and to stock up on tinned food, just in case. You have three years."),
        ("Win a Pentium 166 with 16 MB RAM!", "Enter the Avamux On-Line sweepstakes for a chance to win a top-of-the-line multimedia PC!\n\n  * Pentium 166 MHz processor\n  * 16 MB of RAM\n  * 1.2 GB hard disk (you will never fill it)\n  * 8x CD-ROM drive and 16-bit sound card\n  * 15-inch SVGA monitor\n\nNo purchase necessary. Keyword: SWEEPS"),
        ("That dance song: 14 weeks at #1", "Radio stations report that listeners are still requesting the song with the dance, the one everyone knows the moves to and nobody knows the words to.\n\nWedding DJs say it is now played an average of four times per reception. \"Twice is fine,\" one told us. \"Four is a cry for help.\""),
        ("Browser wars: pick your Navigator", "There are now several programs for exploring the World Wide Web, and they cannot agree on how to show a blinking table.\n\nOur reviewers tried them all on a 14.4 modem. Each one took four minutes to load a picture of a cat. \"Best viewed with\" buttons are recommended."),
        ("Tonight: 'Truth Is Out There' chat", "Join fellow fans of everyone's favourite paranormal investigation show in the Auditorium at 9pm Eastern for a live chat.\n\nTopics include: aliens, conspiracies, and whether that was really a flashlight. Bring your own theories. Trust no one."),
    ];

    private const string Computing = "Ask the Help Desk: How much RAM do I need?\n\nQ: My computer has 4 MB of RAM. Is that enough?\n\nA: For word processing, yes. For the new games, you will want 8 MB. For running two programs at once, 16 MB, which is a lot of money. Ask your parents nicely.\n\nQ: What is an IRQ?\n\nA: An IRQ is how a sound card gets the computer's attention. If two cards want the same one, nothing works and you hear a high-pitched whine. That whine is called \"setting up a PC\".";
    private const string Kids = "Kids Only: How to keep your virtual pet alive\n\n1. Feed it. Every hour. Even during maths.\n2. Clean up after it. You know what we mean.\n3. Play with it, but not at the dinner table.\n4. Turn the sound off before class.\n5. If it beeps at night, it is your problem now.\n\nRemember: a virtual pet is for life. Or about a week.";

    private int Unread => _mail.Count(m => !m.Read);

    private void OpenWelcome()
    {
        if (_welcome is not null) { _desk.Activate(_welcome); return; }
        var w = new Wnd { Title = $"Welcome, {_me}!", Owner = _frame, Back = W3.White }.Sized(430, 222);
        var area = _frame.ChildArea!();
        w.Bounds = new Rect(area.X + 4, area.Y + 2, w.Bounds.Width, w.Bounds.Height);
        _welcome = w;
        w.Closed = () => _welcome = null;
        var date = new DateTime(1996, DateTime.Now.Month, Math.Min(DateTime.Now.Day, DateTime.DaysInMonth(1996, DateTime.Now.Month)));
        w.Paint = (g, c) =>
        {
            W3.Fill(g, c.X, c.Y, 150, c.Height, W3.Navy);
            W3.Fill(g, c.X + 156, c.Y + 4, 268, 20, W3.Maroon);
            Label(g, c, "TOP STORIES", 162, 6, TF.Sys, W3.White);
            var d = date.ToString("dddd, MMM d", CultureInfo.InvariantCulture);
            Label(g, c, d, 418 - Txt.Width(d, TF.Small), 7, TF.Small, W3.Yellow);
            // Visitors to the page, counting up.
            var visitors = (1_996_000 + (int)(_desk.Time / 3)).ToString("0000000", CultureInfo.InvariantCulture);
            Label(g, c, "You are visitor number", 156, 200, TF.Small, W3.Black);
            for (var i = 0; i < visitors.Length; i++)
            {
                var box = new Rect(c.X + 290 + i * 13, c.Y + 199, 12, 16);
                W3.Fill(g, box, W3.Black);
                Txt.Centre(g, visitors[i].ToString(), box, TF.SmallBold, i == visitors.Length - 1 ? W3.Lime : W3.White);
            }
        };
        w.Add(new HotSpot((g, r, hover) =>
        {
            W3.Raised(g, r, false, 2);
            Art.Draw(g, Pics.Mailbox, r.X + 6, r.Y + 8, 2);
            var n = Unread;
            var flash = _mailFlash > 0 && _desk.Time % 0.5 < 0.3;
            Txt.Draw(g, n > 0 ? "You have" : "No new", r.X + 46, r.Y + 8, TF.SmallBold, flash ? W3.Red : W3.Black);
            Txt.Draw(g, n > 0 ? $"{n} new letter{(n == 1 ? "" : "s")}!" : "mail. Sorry!", r.X + 46, r.Y + 22, TF.SmallBold, flash ? W3.Red : W3.Navy, hover);
        }, OpenMailbox) { R = new Rect(6, 6, 138, 48) });
        (string Name, Action Do)[] channels =
        [
            ("Today's News", OpenNews), ("Chat Rooms", OpenRoomList), ("Computing", () => Article(Computing)),
            ("Games", OpenGames), ("Kids Only", () => Article(Kids)),
            ("Members", () => _desk.MessageBox("Members", $"There are 4,812,330 members on-line.\n\nYou are one of them, {_me}. Isn't that something?", Desktop.Icon.Info)),
        ];
        for (var i = 0; i < channels.Length; i++)
            w.Add(new PushButton(channels[i].Name, channels[i].Do) { R = new Rect(8, 62 + i * 26, 134, 24) });
        for (var i = 0; i < Stories.Length; i++)
        {
            var story = Stories[i];
            var first = i == 0;
            w.Add(new HotSpot((g, r, hover) =>
            {
                W3.Fill(g, r.X, r.Y + 7, 5, 5, W3.Red);
                Txt.Draw(g, Txt.Fit(story.Title, TF.Head, r.Width - (first ? 46 : 12)), r.X + 10, r.Y + 1, TF.Head, hover ? W3.Blue : W3.Navy, hover);
                if (first && _desk.Time % 1 < 0.6)
                {
                    var badge = new Rect(r.Right - 34, r.Y + 3, 32, 14);
                    W3.Fill(g, badge, W3.Red);
                    Txt.Centre(g, "NEW!", badge, TF.SmallBold, W3.Yellow);
                }
            }, () => Article(story.Title + "\n\n" + story.Body)) { R = new Rect(156, 28 + i * 21, 268, 20) });
        }
        const string ad = "\u2605 50 FREE HOURS \u2605 Tell a friend about Avamux On-Line! \u2605 Free trial disk in every cereal box \u2605 ";
        w.Add(new HotSpot((g, r, hover) =>
        {
            var on = _desk.Time % 0.6 < 0.3;
            W3.Fill(g, r, W3.Purple);
            // Marquee lights around the edge.
            for (var x = r.X; x < r.Right; x += 6)
            {
                var lit = ((int)((x - r.X) / 6) + (on ? 1 : 0)) % 2 == 0;
                W3.Fill(g, x, r.Y, 3, 2, lit ? W3.Yellow : W3.Fuchsia);
                W3.Fill(g, x, r.Bottom - 2, 3, 2, lit ? W3.Fuchsia : W3.Yellow);
            }
            var width = Txt.Width(ad, TF.Big);
            var shift = _desk.Time * 50 % width;
            using (g.PushClip(r.Deflate(new Thickness(0, 2))))
                for (var x = r.X - shift; x < r.Right; x += width) Txt.Draw(g, ad, x, r.Y + 6, TF.Big, hover ? W3.White : W3.Yellow);
        }, () => _desk.MessageBox("50 FREE HOURS!", "A free Avamux On-Line trial disk is on its way to you now.\n\nAnd another one tomorrow. And the day after that.", Desktop.Icon.Info)) { R = new Rect(156, 158, 268, 32) });
        w.PaintOver = (g, c) =>
        {
            if (_mailFlash <= 0) return;
            var box = new Rect(c.X + c.Width / 2 - 120, c.Y + c.Height / 2 - 26, 240, 52);
            W3.Fill(g, box.Translate(new Vector(3, 3)), W3.Black);
            W3.Fill(g, box, W3.Navy);
            W3.Outline(g, box, W3.Yellow, 2);
            Txt.Centre(g, "You've Got Mail!", box, TF.Logo, _desk.Time % 0.4 < 0.25 ? W3.Yellow : W3.White);
        };
        _desk.Open(w);
    }

    private void Article(string text)
    {
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var title = text[..split];
        var w = Child(title, 400, 206);
        var lines = Txt.Wrap(title, TF.Head, 386);
        w.Paint = (g, c) =>
        {
            for (var i = 0; i < lines.Count && i < 2; i++) Label(g, c, lines[i], 8, 4 + i * 16, TF.Head, W3.Navy);
        };
        var top = lines.Count > 1 ? 40 : 24;
        var body = w.Add(new ChatLog { R = new Rect(6, top, 388, 170 - top) });
        body.Add(null, text[(split + 2)..]);
        body.ScrollToTop();
        w.Default = w.Add(new PushButton("Close", w.Close) { R = new Rect(164, 176, 72, 26) });
    }

    // ----- Mail -----

    private void OpenMailbox()
    {
        if (_mailWin is not null) { _desk.Activate(_mailWin); RefreshMail(); return; }
        var w = Child($"{_me}'s Mailbox", 370, 176);
        _mailWin = w;
        w.Closed = () => { _mailWin = null; _mailList = null; };
        w.Paint = (g, c) => { Label(g, c, "", 6, 4, TF.SmallBold); Label(g, c, "From", 46, 4, TF.SmallBold); Label(g, c, "Subject", 140, 4, TF.SmallBold); };
        var list = w.Add(new ListPane { R = new Rect(6, 20, 358, 116), Tabs = [34, 128], Activate = row => ReadMail((AolMail)row.Tag!) });
        _mailList = list;
        w.Focused = list;
        w.Default = w.Add(new PushButton("Read", () => { if (list.Current?.Tag is AolMail m) ReadMail(m); }) { R = new Rect(6, 144, 80, 26) });
        w.Add(new PushButton("Write", () => Compose()) { R = new Rect(94, 144, 80, 26) });
        w.Add(new PushButton("Delete", () =>
        {
            if (list.Current?.Tag is not AolMail m) return;
            _desk.MessageBox("Delete Mail", m.From == "Grandma1932" ? "Delete this letter?\n\nGrandma will know." : "Delete this letter?", Desktop.Icon.Question, ("OK", () => { _mail.Remove(m); RefreshMail(); }), ("Cancel", null));
        }) { R = new Rect(182, 144, 80, 26) });
        RefreshMail();
        if (list.Rows.Count > 0) list.Selected = 0;
    }

    private void RefreshMail()
    {
        if (_mailList is not { } list) return;
        var selected = list.Current?.Tag;
        list.Rows.Clear();
        foreach (var m in _mail) list.Rows.Add(new ListRow($"{(m.Read ? "" : "NEW")}\t{m.From}\t{m.Subject}", m.Read ? null : W3.Navy, !m.Read, m));
        list.Selected = selected is null ? (list.Rows.Count > 0 ? 0 : -1) : list.Rows.FindIndex(r => r.Tag == selected);
    }

    private void ReadMail(AolMail m)
    {
        m.Read = true;
        RefreshMail();
        var w = Child(m.Subject, 400, 210);
        w.Paint = (g, c) =>
        {
            Label(g, c, "From:", 8, 4, TF.SmallBold); Label(g, c, m.From, 62, 4, TF.Small);
            Label(g, c, "Subject:", 8, 18, TF.SmallBold); Label(g, c, Txt.Fit(m.Subject, TF.Small, 320), 62, 18, TF.Small);
        };
        var body = w.Add(new ChatLog { R = new Rect(6, 36, 388, 136) });
        body.Add(null, m.Body);
        body.ScrollToTop();
        w.Default = w.Add(new PushButton("Reply", () => Compose(m.From, m.Subject.StartsWith("Re:", StringComparison.Ordinal) ? m.Subject : "Re: " + m.Subject)) { R = new Rect(6, 178, 80, 26) });
        w.Add(new PushButton("Forward", () => _desk.MessageBox("Forward", m.From == "Grandma1932" ? "You forwarded it to everyone you know.\n\nGrandma is very proud." : "Forwarded to everyone in your address book. All three of them.", Desktop.Icon.Info)) { R = new Rect(94, 178, 80, 26) });
        w.Add(new PushButton("Close", w.Close) { R = new Rect(314, 178, 80, 26) });
    }

    private void Compose(string to = "", string subject = "")
    {
        var w = Child("Compose Mail", 400, 210);
        w.Paint = (g, c) => { Label(g, c, "To:", 8, 9); Label(g, c, "Subject:", 8, 35); };
        var toField = w.Add(new EditField { R = new Rect(72, 6, 322, 24), Max = 40 });
        var subjectField = w.Add(new EditField { R = new Rect(72, 32, 322, 24), Max = 60 });
        var body = w.Add(new MemoField { R = new Rect(6, 60, 388, 112) });
        toField.Set(to);
        subjectField.Set(subject);
        w.Focused = to.Length > 0 ? body : toField;
        w.Add(new PushButton("Send", () =>
        {
            var address = toField.Value.Trim();
            if (address.Length == 0) { _desk.MessageBox("Compose Mail", "Who is it to? Please fill in the To: box.", Desktop.Icon.Warning); return; }
            var subj = subjectField.Value.Trim().Length > 0 ? subjectField.Value.Trim() : "(no subject)";
            w.Close();
            _desk.MessageBox("Compose Mail", "Your mail has been sent.", Desktop.Icon.Info);
            Later(R(_rnd, 7, 12), () =>
            {
                var buddy = _buddies.FirstOrDefault(b => b.Name.Equals(address, StringComparison.OrdinalIgnoreCase));
                AolMail reply = buddy is not null ? new AolMail(buddy.Name, "Re: " + subj, buddy.MailReply(subj, _rnd))
                    : address.Equals(_me, StringComparison.OrdinalIgnoreCase) ? new AolMail(_me, subj, body.Value.Length > 0 ? body.Value : "(You wrote to yourself. It happens to the best of us.)")
                    : AolTalk.Bounce(address, subj);
                _mail.Insert(0, reply);
                Play("aol-mail-voice-1", AolSounds.MailVoice);
                _mailFlash = 3;
                RefreshMail();
            });
        }) { R = new Rect(6, 178, 80, 26) });
        w.Add(new PushButton("Cancel", w.Close) { R = new Rect(94, 178, 80, 26) });
    }

    // ----- The buddy list and instant messages -----

    private void OpenBuddies()
    {
        if (_buddyWin is not null) { _desk.Activate(_buddyWin); return; }
        var w = new Wnd { Title = "Buddy List", Owner = _frame, Back = W3.Silver }.Sized(150, 200);
        var area = _frame.ChildArea!();
        w.Bounds = new Rect(area.Right - w.Bounds.Width - 4, area.Y + 2, w.Bounds.Width, w.Bounds.Height);
        _buddyWin = w;
        w.Closed = () => { _buddyWin = null; _buddyList = null; };
        var list = w.Add(new ListPane { R = new Rect(6, 6, 138, 154), Activate = row => { if (row.Tag is AolBuddy b) OpenIm(b); } });
        _buddyList = list;
        w.Focused = list;
        w.Default = w.Add(new PushButton("IM", () => { if (list.Current?.Tag is AolBuddy b) OpenIm(b); else _desk.MessageBox("Buddy List", "Pick a buddy first.", Desktop.Icon.Info); }) { R = new Rect(6, 166, 42, 26) });
        w.Add(new PushButton("Info", () => { if (list.Current?.Tag is AolBuddy b) _desk.MessageBox($"Profile: {b.Name}", b.Profile, Desktop.Icon.Info); }) { R = new Rect(52, 166, 46, 26) });
        w.Add(new PushButton("Chat", OpenRoomList) { R = new Rect(102, 166, 42, 26) });
        var since = 0.0;
        w.Tick = dt => { if ((since += dt) > 0.5) { since = 0; RefreshBuddies(); } };
        _desk.Open(w);
        RefreshBuddies();
    }

    private void RefreshBuddies()
    {
        if (_buddyList is not { } list) return;
        var selected = list.Current?.Tag;
        list.Rows.Clear();
        list.Rows.Add(new ListRow($"Buddies ({_buddies.Count(b => b.Online)}/{_buddies.Count})", Header: true));
        foreach (var b in _buddies)
        {
            var door = _doors.TryGetValue(b, out var d) && _desk.Time - d.At < 6 ? d : ((double, bool)?)null;
            if (!b.Online && door is null) continue;
            var icon = door is { } x ? x.Item2 ? Pics.DoorOpen : Pics.DoorShut : Pics.Blank;
            list.Rows.Add(new ListRow(b.Name, b.Online ? null : W3.Gray, door is { Item2: true }, b, icon));
        }
        list.Rows.Add(new ListRow("Family (0/1)", Header: true));
        list.Rows.Add(new ListRow("Co-Workers (0/0)", Header: true));
        list.Selected = selected is null ? -1 : list.Rows.FindIndex(r => r.Tag == selected);
    }

    private void SetOnline(AolBuddy b, bool on)
    {
        if (b.Online == on) return;
        b.Online = on;
        b.Heard = 0;
        _doors[b] = (_desk.Time, on);
        if (on) Play("aol-door-open-1", AolSounds.DoorOpen);
        else Play("aol-door-shut-1", AolSounds.DoorClose);
        if (_ims.TryGetValue(b, out var im)) im.Log.Add(null, $"{b.Name} has signed {(on ? "on" : "off")}.", textColor: W3.Gray);
        RefreshBuddies();
    }

    /// <summary>Every so often a buddy signs on or off.</summary>
    private void BuddyComings() => Later(R(_rnd, 14, 26), () =>
    {
        var online = _buddies.Count(b => b.Online);
        var on = online <= 2 || online < 5 && _rnd.Next(2) == 0;
        // Someone in the middle of a conversation stays put.
        var who = _buddies.Where(b => b.Online != on && (on || !_ims.ContainsKey(b))).OrderBy(_ => _rnd.Next()).FirstOrDefault();
        if (who is not null) SetOnline(who, on);
        BuddyComings();
    });

    /// <summary>A buddy sends a message out of the blue.</summary>
    private void SurpriseIm()
    {
        var who = _buddies.Where(b => b.Online && !_ims.ContainsKey(b)).OrderBy(_ => _rnd.Next()).FirstOrDefault();
        if (who is null || _desk.TopModal() is not null) { Later(R(_rnd, 8, 15), SurpriseIm); return; }
        // It pops up on top, but whatever the member is typing keeps going where it was going.
        var im = OpenIm(who, activate: false);
        im.Log.Add(who.Name, who.Opener, W3.Navy);
        Play("aol-im-1", AolSounds.Message);
        Later(R(_rnd, 70, 140), SurpriseIm);
    }

    private (Wnd Win, ChatLog Log) OpenIm(AolBuddy b, bool activate = true)
    {
        if (_ims.TryGetValue(b, out var open))
        {
            if (activate) _desk.Activate(open.Win);
            return open;
        }
        var w = Child($"Instant Message: {b.Name}", 320, 160, activate: activate);
        var log = w.Add(new ChatLog { R = new Rect(6, 6, 308, 112) });
        var field = w.Add(new EditField { R = new Rect(6, 126, 236, 26), Max = 120 });
        w.Default = w.Add(new PushButton("Send", () => SendIm(b, field)) { R = new Rect(248, 124, 66, 30) });
        w.Focused = field;
        w.Closed = () => { _ims.Remove(b); b.Heard = 0; };
        var im = (w, log);
        _ims[b] = im;
        if (!b.Online) log.Add(null, $"{b.Name} is not signed on right now.", textColor: W3.Gray);
        return im;
    }

    private void SendIm(AolBuddy b, EditField field)
    {
        var text = field.Value.Trim();
        if (text.Length == 0 || !_ims.TryGetValue(b, out var im)) return;
        field.Set("");
        im.Log.Add(_me, text, W3.Red);
        if (!b.Online) { Later(0.6, () => { if (_ims.TryGetValue(b, out var x)) x.Log.Add(null, $"{b.Name} is not currently signed on.", textColor: W3.Gray); }); return; }
        var (reply, leaves) = b.Reply(text, _rnd);
        Later(R(_rnd, 1.2, 2.6) + reply.Length * 0.04, () =>
        {
            if (!b.Online) return;
            var window = OpenIm(b, activate: false);
            window.Log.Add(b.Name, reply, W3.Navy);
            if (_desk.Active != window.Win) Play("aol-im-1", AolSounds.Message);
            if (leaves) Later(1.5, () => SetOnline(b, false));
        });
    }

    // ----- Chat -----

    private static readonly string[] Lurkers =
    [
        "SunnyD_96", "PixelPal", "JellyBean22", "Lurker", "QuietOne", "BlueRaspberry", "FloppyDisk", "Slinky42", "Gameboy_Kid",
        "MixTape", "LavaLamp", "ScrunchieQueen", "Furby_Fan", "NeonWindbreaker", "CrystalPepsi", "Walkman", "Glowstick", "Yo_Yo_Ma_Fan",
    ];

    private void OpenRoomList()
    {
        var w = Child("Chat Rooms", 300, 150);
        w.Paint = (g, c) => Label(g, c, "Pick a room and click Go:", 6, 4, TF.SmallBold);
        var list = w.Add(new ListPane { R = new Rect(6, 20, 288, 92), Tabs = [230], Activate = row => { w.Close(); EnterRoom((AolRoom)row.Tag!); } });
        foreach (var room in AolTalk.Rooms) list.Rows.Add(new ListRow($"{room.Name}\t{room.Crowd}", Tag: room));
        list.Selected = 0;
        w.Focused = list;
        w.Default = w.Add(new PushButton("Go", () => { if (list.Current?.Tag is AolRoom r) { w.Close(); EnterRoom(r); } }) { R = new Rect(6, 118, 80, 26) });
        w.Add(new PushButton("Cancel", w.Close) { R = new Rect(94, 118, 80, 26) });
    }

    private void EnterRoom(AolRoom room)
    {
        if (_room is { } current)
        {
            if (current.Room == room) { _desk.Activate(current.Win); return; }
            current.Win.Close();
        }
        var w = Child(room.Name, 470, 210);
        var log = w.Add(new ChatLog { R = new Rect(6, 20, 330, 152) });
        var people = w.Add(new ListPane { R = new Rect(342, 20, 122, 124), Activate = row => _desk.MessageBox($"Profile: {row.Text}", "No profile available.\n\n(They are too busy chatting.)", Desktop.Icon.Info) });
        people.Rows.Add(new ListRow(_me, W3.Red, true));
        foreach (var name in room.People.Concat(Lurkers.OrderBy(_ => _rnd.Next())).Take(room.Crowd - 1)) people.Rows.Add(new ListRow(name));
        w.Paint = (g, c) =>
        {
            Label(g, c, Txt.Fit("Topic: " + room.Topic, TF.SmallBold, 326), 6, 3, TF.SmallBold, W3.Navy);
            Label(g, c, $"People Here: {people.Rows.Count}", 342, 3, TF.SmallBold, W3.Navy);
        };
        w.Add(new PushButton("Rooms", OpenRoomList) { R = new Rect(342, 148, 58, 24) });
        w.Add(new PushButton("Leave", w.Close) { R = new Rect(406, 148, 58, 24) });
        var field = w.Add(new EditField { R = new Rect(6, 178, 262, 26), Max = 92 });
        w.Default = w.Add(new PushButton("Send", () => Say(field)) { R = new Rect(274, 176, 62, 30) });
        w.Focused = field;
        _room = (w, log, people, room);
        w.Closed = () => { if (_room?.Win == w) _room = null; };
        log.Add(AolTalk.Host, $"*** You are in \"{room.Name}\". ***", W3.Black, W3.Gray);
        var next = 1.0;
        w.Tick = dt =>
        {
            if ((next -= dt) > 0) return;
            next = R(_rnd, 1.4, 4.2);
            if (_rnd.NextDouble() < 0.1)
            {
                // Someone comes or goes.
                if (_rnd.Next(2) == 0 && people.Rows.Count > 4)
                {
                    var i = _rnd.Next(1, people.Rows.Count);
                    log.Add(AolTalk.Host, $"{people.Rows[i].Text} has left the room.", W3.Black, W3.Gray);
                    people.Rows.RemoveAt(i);
                    if (people.Selected >= people.Rows.Count) people.Selected = -1;
                }
                else
                {
                    var name = Lurkers.Concat(room.People).Where(n => people.Rows.All(r => r.Text != n)).OrderBy(_ => _rnd.Next()).FirstOrDefault();
                    if (name is null) return;
                    people.Rows.Add(new ListRow(name));
                    log.Add(AolTalk.Host, $"{name} has entered the room.", W3.Black, W3.Gray);
                }
                return;
            }
            var (who, text) = AolTalk.Ambient(room, _me, _rnd);
            log.Add(who, text);
        };
    }

    private void Say(EditField field)
    {
        var text = field.Value.Trim();
        if (text.Length == 0 || _room is not { } room) return;
        field.Set("");
        room.Log.Add(_me, text, W3.Red);
        foreach (var (delay, who, reply) in AolTalk.Replies(room.Room, _me, text, _rnd))
            Later(delay, () => { if (_room?.Win == room.Win) room.Log.Add(who, reply); });
    }

    // ----- News, games, keywords -----

    private void OpenNews()
    {
        var w = Child("Today's News", 330, 150);
        w.Paint = (g, c) => Label(g, c, "Double-click a story to read it:", 6, 4, TF.SmallBold);
        var list = w.Add(new ListPane { R = new Rect(6, 20, 318, 92), Activate = row => Article((string)row.Tag!) });
        foreach (var s in Stories) list.Rows.Add(new ListRow(s.Title, Tag: s.Title + "\n\n" + s.Body, Icon: Pics.Bullet));
        list.Selected = 0;
        w.Focused = list;
        w.Default = w.Add(new PushButton("Read", () => { if (list.Current?.Tag is string s) Article(s); }) { R = new Rect(6, 118, 80, 26) });
        w.Add(new PushButton("Close", w.Close) { R = new Rect(94, 118, 80, 26) });
    }

    private static readonly (string File, string What, int Bytes)[] Downloads =
    [
        ("NEBULA.ZIP", "Nebula Blaster '96 (shareware)", 1_204_733), ("CATFISH.ZIP", "Cats throw fish. Educational!", 29_434),
        ("TOASTERS.ZIP", "Flying toaster screen saver", 388_117), ("BIGGAME.ZIP", "A very big game (8 disks)", 11_534_336),
    ];

    private void OpenGames()
    {
        var w = Child("Games Channel", 340, 150);
        w.Paint = (g, c) => Label(g, c, "Download the hottest shareware:", 6, 4, TF.SmallBold);
        var list = w.Add(new ListPane { R = new Rect(6, 20, 328, 92), Tabs = [92, 266], Activate = row => Download(((string, string, int))row.Tag!) });
        foreach (var d in Downloads) list.Rows.Add(new ListRow($"{d.File}\t{d.What}\t{d.Bytes / 1024:N0}K", Tag: d));
        list.Selected = 0;
        w.Focused = list;
        w.Default = w.Add(new PushButton("Download", () => { if (list.Current?.Tag is ValueTuple<string, string, int> d) Download(d); }) { R = new Rect(6, 118, 96, 26) });
        w.Add(new PushButton("Close", w.Close) { R = new Rect(110, 118, 80, 26) });
    }

    private void Download((string File, string What, int Bytes) d)
    {
        var w = Child("Download Manager", 330, 120);
        var progress = 0.0;
        // The line's real speed drifts towards a target that changes every few seconds; what the dialog shows
        // is sampled from it once a second, as transfer dialogs did.
        double rate = 2900, target = 2900, retarget = 2, shownRate = 2900, sample = 0;
        var shownLeft = (int)(d.Bytes / rate);
        var huge = d.Bytes > 5_000_000;
        w.Paint = (g, c) =>
        {
            Label(g, c, $"Downloading: {d.File}", 10, 6);
            Label(g, c, $"{d.Bytes:N0} bytes", 10, 22, TF.Small, W3.Gray);
            var bar = new Rect(c.X + 10, c.Y + 42, 310, 20);
            W3.Sunken(g, bar, W3.White);
            var blocks = (int)(progress * 30);
            for (var i = 0; i < blocks; i++) W3.Fill(g, bar.X + 3 + i * 10.2, bar.Y + 3, 8, 14, W3.Navy);
            var left = shownLeft;
            Label(g, c, $"Time remaining: about {(left >= 3600 ? $"{left / 3600} hr {left % 3600 / 60} min" : $"{Math.Max(1, left / 60)} min")}", 10, 68, TF.Small);
            Label(g, c, $"{shownRate:N0} bytes/sec", 236, 68, TF.Small);
        };
        w.Default = w.Add(new PushButton("Cancel", w.Close) { R = new Rect(10, 88, 100, 26) });
        w.Add(new PushButton("Finish Later", () => { w.Close(); _desk.MessageBox("Download Manager", "Your download will continue next time. From the beginning.", Desktop.Icon.Info); }) { R = new Rect(118, 88, 110, 26) });
        w.Tick = dt =>
        {
            // Bursts and stalls, like a real phone line: now and then it all but stops for a moment.
            if ((retarget -= dt) <= 0)
            {
                var stall = _rnd.NextDouble() < 0.15;
                target = stall ? R(_rnd, 60, 240) : R(_rnd, 1700, 3400);
                retarget = stall ? R(_rnd, 1, 2.5) : R(_rnd, 2, 5);
            }
            rate += (target - rate) * Math.Min(1, dt * 1.5);
            if ((sample -= dt) <= 0)
            {
                sample = 1;
                shownRate = Math.Round(rate);
                shownLeft = (int)(d.Bytes * (1 - progress) / Math.Max(rate, 1));
            }
            progress += dt * (huge ? 0.008 : 0.07) * rate / 2900;
            if (huge && progress > 0.06)
            {
                w.Close();
                SignOff("Your connection to Avamux On-Line was lost.\n\n(Someone in the house picked up the phone.)");
                return;
            }
            if (progress < 1) return;
            w.Close();
            WriteProtected(d.File);
        };
    }

    private void WriteProtected(string file) => _desk.MessageBox("Download Manager", $"Download complete!\n\nSaving {file} to A:\\DOWNLOAD...\n\nWrite protect error writing drive A.", Desktop.Icon.Stop,
        ("Abort", null), ("Retry", () => WriteProtected(file)), ("Fail", () => _desk.MessageBox("Download Manager", "Fail on INT 24.\n\nThe file has gone back into the phone line.", Desktop.Icon.Info)));

    private void Web() => _desk.MessageBox("World Wide Web", "The Avamux On-Line Web browser is coming soon!\n\nIn the meantime, try Gopher, or a Web Ring.", Desktop.Icon.Info);

    private void About() => _desk.MessageBox("About Avamux On-Line",
        $"Avamux On-Line for AVA-WIN\nVersion 2.6 (16-bit)\n\n\u00a9 1996 avamux soft. All rights reserved.\n\n{(_online ? $"Signed on as {_me}." : "Not signed on.")}\n50 hours free. Then $2.95 an hour.", Desktop.Icon.Info);

    private void Keyword()
    {
        if (!_online) return;
        var w = new Wnd { Title = "Keyword", Owner = _frame, Modal = true, DialogFrame = true, Back = W3.Silver }.Sized(300, 92);
        w.Paint = (g, c) => Label(g, c, "Type a keyword (try NEWS, CHAT or GAMES):", 8, 6, TF.SmallBold);
        var field = w.Add(new EditField { R = new Rect(8, 24, 284, 26), Max = 30 });
        w.Default = w.Add(new PushButton("Go", () => { w.Close(); GoKeyword(field.Value.Trim()); }) { R = new Rect(124, 58, 80, 26) });
        w.Add(new PushButton("Cancel", w.Close) { R = new Rect(212, 58, 80, 26) });
        w.Focused = field;
        _desk.Open(w, centre: true);
    }

    private void GoKeyword(string word)
    {
        switch (word.ToUpperInvariant().Replace(" ", ""))
        {
            case "": return;
            case "MAIL" or "MAILBOX" or "INBOX": OpenMailbox(); break;
            case "WRITE" or "COMPOSE": Compose(); break;
            case "CHAT" or "PEOPLE" or "LOBBY" or "TOWNSQUARE": EnterRoom(AolTalk.Rooms[0]); break;
            case "ROOMS" or "CHATROOMS": OpenRoomList(); break;
            case "HELP" or "HELPDESK" or "COMPUTING": EnterRoom(AolTalk.Rooms[1]); break;
            case "TREK" or "STARTREK": EnterRoom(AolTalk.Rooms[2]); break;
            case "GRUNGE" or "MUSIC": EnterRoom(AolTalk.Rooms[3]); break;
            case "BUDDY" or "BUDDIES" or "BUDDYLIST": OpenBuddies(); break;
            case "HOME" or "WELCOME": OpenWelcome(); break;
            case "NEWS": OpenNews(); break;
            case "GAMES" or "DOWNLOAD" or "SHAREWARE": OpenGames(); break;
            case "KIDS" or "KIDSONLY": Article(Kids); break;
            case "WEB" or "WWW" or "INTERNET" or "NETSCAPE": Web(); break;
            case "SWEEPS": _desk.MessageBox("Sweepstakes", "You have been entered!\n\nThe winner will be announced in 1997. Keep your modem on.", Desktop.Icon.Info); break;
            case "AVAMUX" or "ABOUT": About(); break;
            case "ZORK" or "AVAZORK": _desk.MessageBox("Keyword", "That keyword is a floppy disk away.\n\nExit and type AVAZORK at the A:\\> prompt.", Desktop.Icon.Info); break;
            case "HOTDOG": _desk.MessageBox("Keyword", "Ketchup and mustard are available at the A:\\> prompt. Type HOTDOG.", Desktop.Icon.Info); break;
            case "Y2K": Article(Stories[1].Title + "\n\n" + Stories[1].Body); break;
            case "SIGNOFF" or "BYE" or "LOGOFF": SignOff(); break;
            default: _desk.MessageBox("Keyword", $"\"{word}\" is not a valid keyword. Please check the spelling and try again.", Desktop.Icon.Warning); break;
        }
    }

    // ----- The clock -----

    private void OpenClock()
    {
        if (_desk.Windows.FirstOrDefault(w => w.Title == "Clock") is { } open) { _desk.Activate(open); return; }
        var w = new Wnd { Title = "Clock", Back = W3.Silver }.Sized(120, 124);
        w.Bounds = new Rect(Desktop.W - w.Bounds.Width - 20, 40, w.Bounds.Width, w.Bounds.Height);
        w.Paint = (g, c) => DrawClock(g, new Rect(c.X + 6, c.Y + 6, c.Width - 12, c.Height - 12));
        _desk.Open(w);
    }

    private static void DrawClock(DrawingContext g, Rect r)
    {
        var centre = r.Center;
        var radius = Math.Min(r.Width, r.Height) / 2 - 1;
        g.DrawEllipse(W3.B(W3.White), new Pen(W3.B(W3.Black), 1), centre, radius, radius);
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            var p = centre + new Vector(Math.Sin(a), -Math.Cos(a)) * (radius - Math.Max(2, radius / 8));
            var s = i % 3 == 0 ? Math.Max(2, radius / 12) : 1;
            W3.Fill(g, Math.Round(p.X - s / 2), Math.Round(p.Y - s / 2), s, s, i % 3 == 0 ? W3.Navy : W3.Black);
        }
        var now = DateTime.Now;
        void Hand(double turns, double length, double width, Color c)
        {
            var a = turns * 2 * Math.PI;
            g.DrawLine(new Pen(W3.B(c), width), centre, centre + new Vector(Math.Sin(a), -Math.Cos(a)) * radius * length);
        }
        Hand((now.Hour % 12 + now.Minute / 60.0) / 12, 0.5, Math.Max(1, radius / 12), W3.Black);
        Hand((now.Minute + now.Second / 60.0) / 60, 0.78, Math.Max(1, radius / 18), W3.Black);
        if (radius > 20) Hand(now.Second / 60.0, 0.85, 1, W3.Red);
    }
}

/// <summary>The little pictures of AOL.EXE and its desktop.</summary>
internal static class Pics
{
    public static readonly string[] Mail =
    [
        "................", "................", "kkkkkkkkkkkkkkkk", "kkwwwwwwwwwwrrkk", "kwkwwwwwwwwwrkwk", "kwwkwwwwwwwwkwwk",
        "kwwwkwwwwwwkwwwk", "kwwwwkwwwwkwwwwk", "kwwwwwkwwkwwwwwk", "kwwwwkwkkwkwwwwk", "kwwwkwwwwwwkwwwk", "kwwkwwwwwwwwkwwk",
        "kwkwwwwwwwwwwkwk", "kkkkkkkkkkkkkkkk",
    ];

    public static readonly string[] Write =
    [
        "kkkkkkkkkk......", "kwwwwwwwwk......", "kwggggggwk....kk", "kwwwwwwwwk...kyk", "kwggggggwk..kyyk", "kwwwwwwwwk.kyyk.",
        "kwggggwwwkkyyk..", "kwwwwwwwkyyyk...", "kwgggwwkyyyk....", "kwwwwwkyyyk.....", "kwggwkkyyk......", "kwwwwkokk.......",
        "kwwwwkkk........", "kwwwwwwwwk......", "kkkkkkkkkk......",
    ];

    public static readonly string[] Home =
    [
        ".......kk..kk...", "......krrk.kk...", ".....krrrrkkk...", "....krrrrrrkk...", "...krrrrrrrrk...", "..krrrrrrrrrrk..",
        ".kkkkkkkkkkkkkk.", "..kyyyyyyyyyyk..", "..kykkkyykkkyk..", "..kykakyykakyk..", "..kykkkyykkkyk..", "..kyyyykkyyyyk..",
        "..kyyyykmkyyyk..", "..kyyyykmkyyyk..", "..kkkkkkkkkkkk..",
    ];

    public static readonly string[] Chat =
    [
        "kkkkkkkkk.......", "kwwwwwwwk.......", "kwkkwkkwk.......", "kwwwwwwwk.......", "kwkkkkwwk.......", "kwwwwwkkkkkkkkk.",
        "kkwkkkkyyyyyyyk.", ".kk...kykkykkyk.", "..k...kyyyyyyyk.", "......kykkkkyyk.", "......kyyyyyyyk.", "......kkkkkkyykk",
        "...........kykk.", "...........kk...",
    ];

    public static readonly string[] Buddy =
    [
        "................", "...kkkk.........", "..kyyyyk..kkkk..", "..kykyyk.kyyyyk.", "..kyyyyk.kykyyk.", "..kykkyk.kyyyyk.",
        "...kyyk..kykkyk.", "..kkkkkk..kyyk..", ".kbbbbbbk.kkkk..", "kbbbbbbbbkrrrrk.", "kbbbbbbbbkrrrrrk", "kbbbbbbbbkrrrrrk",
        "kbbbbbbbbkrrrrrk", "kkkkkkkkkkkkkkkk",
    ];

    public static readonly string[] Key =
    [
        "................", "................", "..kkkk..........", ".kyyyyk.........", "kyykkyyk........", "kyk..kyk........",
        "kyk..kykkkkkkkk.", "kyykkyyyyyyyyyyk", ".kyyyykkkkkkykyk", "..kkkk......kkkk",
    ];

    public static readonly string[] News =
    [
        ".kkkkkkkkkkkkkk.", ".kwwwwwwwwwwwwk.", ".kwkkkkkkkkkkwk.", ".kwwwwwwwwwwwwk.", ".kwgggwkkkkkkwk.", ".kwgggwwwwwwwwk.",
        ".kwgggwkkkkkkwk.", ".kwwwwwwwwwwwwk.", ".kwkkkkkwkkkkwk.", ".kwwwwwwwwwwwwk.", ".kwkkkkkwkkkkwk.", ".kwwwwwwwwwwwwk.",
        ".kkkkkkkkkkkkkk.",
    ];

    public static readonly string[] Games =
    [
        "......kkk.......", ".....krrrk......", ".....krrrk......", "......kkk.......", ".......k........", ".......k........",
        ".......k...kk...", "..kkkkkkkkkrrk..", ".kssssssssskkk..", "kssssssssssssk..", "kggggggggggggk..", ".kkkkkkkkkkkk...",
    ];

    public static readonly string[] Globe =
    [
        ".....kkkkkk.....", "...kkbbbbbbkk...", "..kbbleebbbbbk..", ".kbblleeebbbbbk.", ".kbbleeeelbbbbk.", "kbbbbleeebbblbbk",
        "kbbbbbleebbllbbk", "kbbbbbbebblllebk", "kbbbbbbbblleeebk", "kbblbbbbbbleebbk", "kbbllbbbbbbebbbk", ".kbblebbbbbbbbk.",
        ".kbbbllbbbbbbbk.", "..kbbbbbbbbbbk..", "...kkbbbbbbkk...", ".....kkkkkk.....",
    ];

    public static readonly string[] Help =
    [
        "....kkkkkk......", "...kyyyyyyk.....", "..kyyykkyyyk....", "..kyyk..kyyk....", "...kk...kyyk....", ".......kyyk.....",
        "......kyyk......", ".....kyyk.......", ".....kyyk.......", "......kk........", "................", ".....kkkk.......",
        ".....kyyk.......", ".....kkkk.......",
    ];

    public static readonly string[] Door =
    [
        "kkkkkkkkkk......", "kmmmmmmmmk......", "kmkkkkkkmk......", "kmkmmmmkmk......", "kmkmmmmkmk....k.", "kmkkkkkkmk....kk",
        "kmmmmmmmmkkkkkkk", "kmmmmmmykkkkkkkk", "kmmmmmmmmk....kk", "kmkkkkkkmk....k.", "kmkmmmmkmk......", "kmkkkkkkmk......",
        "kmmmmmmmmk......", "kkkkkkkkkk......",
    ];

    public static readonly string[] Modem =
    [
        "................", "................", "................", "....kkkkkkkkkk..", "...kssssssssssk.", "..kssssssssssssk",
        ".kkkkkkkkkkkkkkk", ".kgggggggggggggk", ".kglkrkllkrkgggk", ".kgggggggggggggk", ".kkkkkkkkkkkkkkk", "..kk.........kk.",
    ];

    public static readonly string[] Phone =
    [
        "................", ".kkkk......kkkk.", "krrrrkkkkkkrrrrk", "krrrrrrrrrrrrrrk", ".kkkrrrrrrrrkkk.", "...krrrrrrrrk...",
        "..krrkkkkkkrrk..", "..krkwkwkwkkrk..", "..krkkkkkkkkrk..", "..krkwkwkwkkrk..", "..krkkkkkkkkrk..", "..krrrrrrrrrrk..",
        "..kkkkkkkkkkkk..",
    ];

    public static readonly string[] Mailbox =
    [
        "..........kk....", "..........krk...", "..........krrk..", "..kkkkkkkkkrrk..", ".kbbbbbbbbkkk...", "kbbbbbbbbbbbk...",
        "kbbbbbbbbbbbbk..", "kbkkkkkbbbbbbk..", "kbkwwwkbbbbbbk..", "kbkkkkkbbbbbbk..", "kbbbbbbbbbbbbk..", "kkkkkkkkkkkkkk..",
        "......kk........", "......kk........", "......kk........", ".....kkkk.......",
    ];

    public static readonly string[] ProgMan =
    [
        "kkkkkkkkkkkkkkkk", "knnnnnnnnnnnnnnk", "kkkkkkkkkkkkkkkk", "kwwwwwwwwwwwwwwk", "kwrrwwyywwllwwwk", "kwrrwwyywwllwwwk",
        "kwwwwwwwwwwwwwwk", "kwbbwwppwwaawwwk", "kwbbwwppwwaawwwk", "kwwwwwwwwwwwwwwk", "kkkkkkkkkkkkkkkk",
    ];

    public static readonly string[] Dos =
    [
        "kkkkkkkkkkkkkkkk", "knnnnnnnnnnnnnnk", "kkkkkkkkkkkkkkkk", "kkkkkkkkkkkkkkkk", "kkkwkkwkkkkkkkkk", "kkwkwkkwkkkkkkkk",
        "kkwwwkkkwkkkkkkk", "kkwkwkkwkkkkkkkk", "kkwkwkwkkkwwwkkk", "kkkkkkkkkkkkkkkk", "kkkkkkkkkkkkkkkk",
    ];

    public static readonly string[] DoorOpen = ["kkkkkkk", "kyykmk.", "kyykmmk", "kyykmmk", "kyykmmk", "kyykmmk", "kyykmk.", "kkkkk.."];
    public static readonly string[] DoorShut = ["kkkkkk.", "kmmmmk.", "kmmmmk.", "kmmmyk.", "kmmmmk.", "kmmmmk.", "kmmmmk.", "kkkkkk."];
    public static readonly string[] Blank = [".......", ".......", ".......", ".......", ".......", ".......", ".......", "......."];
    public static readonly string[] Bullet = ["....", ".rr.", "rrrr", "rrrr", ".rr."];
}
