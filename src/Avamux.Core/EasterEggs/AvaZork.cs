namespace Avamux.Core.EasterEggs;

/// <summary>
/// AVAZORK: a small text adventure. It is a summer evening in 1996, and you want to get online. Mum is on the
/// phone, the modem is still in its box and the phone cord is somewhere in the basement.
/// </summary>
public sealed class AvaZork(IDosConsole con) : ITextGame
{
    private sealed record Room(string Name, string Description, Dictionary<string, string> Exits, bool Dark = false);

    private readonly Dictionary<string, Room> _rooms = new()
    {
        ["bedroom"] = new("Your Bedroom",
            "Glow-in-the-dark stars cover the ceiling and a poster of a band you will deny liking in ten years hangs over the bed. " +
            "Your computer sits on the desk, a beige tower with a turbo button. The hallway is to the east.",
            new() { ["east"] = "hallway" }),
        ["hallway"] = new("Upstairs Hallway",
            "Family photos line the walls, all of them taken at the mall. Your bedroom is west, the living room east and the kitchen south. " +
            "Creaky stairs lead down to the basement.",
            new() { ["west"] = "bedroom", ["east"] = "living", ["south"] = "kitchen", ["down"] = "basement" }),
        ["living"] = new("Living Room",
            "A huge television the depth of a refrigerator plays Saturday cartoons to nobody. A plaid couch sags in the middle. " +
            "The front door is to the north and the hallway west.",
            new() { ["west"] = "hallway", ["north"] = "yard" }),
        ["yard"] = new("Front Yard",
            "Sprinklers tick. Somewhere, a dog is barking at the ice cream truck. A mailbox stands at the end of the driveway. The house is south.",
            new() { ["south"] = "living" }),
        ["kitchen"] = new("Kitchen",
            "Linoleum, a wall phone with a cord stretched long enough to reach the fridge, and a junk drawer that everyone pretends is organised. " +
            "The hallway is north and the garage east.",
            new() { ["north"] = "hallway", ["east"] = "garage" }),
        ["garage"] = new("Garage",
            "Oil stains, a bicycle with streamers and a stack of cardboard boxes. A big green bin stands by the door. The kitchen is west.",
            new() { ["west"] = "kitchen" }),
        ["basement"] = new("Basement",
            "Wood panelling, a dusty exercise bike and the dehumidifier humming to itself. A tangle of cables spills out of a crate marked MISC. " +
            "The stairs go back up.",
            new() { ["up"] = "hallway" }, Dark: true),
    };

    // Where each item is: a room, "inv", or hidden in a container.
    private readonly Dictionary<string, string> _where = new()
    {
        ["flashlight"] = "drawer", ["cord"] = "basement", ["modem"] = "box", ["cd"] = "mailbox", ["trash"] = "kitchen", ["quarter"] = "couch",
    };

    private static readonly Dictionary<string, (string Name, string Look)> Items = new()
    {
        ["flashlight"] = ("a flashlight", "A heavy flashlight that takes four D batteries."),
        ["cord"] = ("a phone cord", "A beige phone cord with a little plastic clip at each end."),
        ["modem"] = ("a modem", "A 28.8k external modem. The box promised it was 'blazing fast'. It has a row of tiny red lights."),
        ["cd"] = ("a free trial CD", "A shiny CD: 'GET ONLINE NOW! 500 HOURS FREE!' The fine print is very fine."),
        ["trash"] = ("a bag of trash", "It smells like Tuesday's fish sticks."),
        ["quarter"] = ("a quarter", "Twenty-five cents. Enough for a round at the arcade."),
    };

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["torch"] = "flashlight", ["light"] = "flashlight", ["lamp"] = "flashlight",
        ["cable"] = "cord", ["wire"] = "cord", ["phonecord"] = "cord", ["cables"] = "cord",
        ["disc"] = "cd", ["disk"] = "cd", ["trial"] = "cd",
        ["bag"] = "trash", ["garbage"] = "trash", ["rubbish"] = "trash",
        ["coin"] = "quarter", ["money"] = "quarter",
        ["pc"] = "computer", ["tower"] = "computer", ["desk"] = "computer", ["mom"] = "mum", ["mother"] = "mum",
        ["boxes"] = "box", ["can"] = "bin", ["trashcan"] = "bin", ["cushion"] = "couch", ["sofa"] = "couch", ["tv"] = "television",
    };

    private string _room = "bedroom";
    private bool _drawerOpen, _boxOpen, _mailboxOpen, _light, _modemConnected, _cdInstalled, _mumOff, _online, _couchSearched;
    private readonly HashSet<string> _scored = [];
    private int _moves;

    public string Prompt => Finished ? "" : ">";
    public bool Finished { get; private set; }
    public int Score { get; private set; }
    public bool Won => _online;

    public void Start()
    {
        con.Color(15, 0);
        con.Write("AVAZORK: The Great Dial-Up\n");
        con.Color(7, 0);
        con.Write("An interactive adventure. Copyright (c) 1996 avamux soft. Release 1 / Serial number 960815\n\n");
        con.Write("It is the summer of 1996. Everyone at school says the internet is the future, and you are going to get on it tonight.\n");
        con.Write("(Type HELP if you are stuck, QUIT to leave.)\n\n");
        Look();
        con.Write("\n");
    }

    public void Enter(string line)
    {
        // Little words are dropped ("take the cord", "pick up the cord"), except the first: UP alone is a direction.
        var all = line.ToLowerInvariant().Split([' ', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        var words = all.Where((w, i) => i == 0 || w is not ("the" or "a" or "an" or "at" or "to" or "on" or "with" or "my" or "some" or "up" or "into" or "in" or "off"))
            .Select((w, i) => i == 0 ? w : Aliases.GetValueOrDefault(w, w)).ToList();
        if (words.Count > 1 && words[0] == "go" && all.Contains("up")) words[1] = "up";
        if (words.Count == 1 && words[0] == "go" && all.Length > 1 && all[1] is "up" or "down") words.Add(all[1]);
        var raw = " " + line.ToLowerInvariant() + " ";
        if (words.Count == 0) { con.Write("I beg your pardon?\n\n"); return; }
        _moves++;
        var verb = words[0];
        var noun = words.Count > 1 ? words[1] : "";

        switch (verb)
        {
            case "n" or "north" or "s" or "south" or "e" or "east" or "w" or "west" or "u" or "d" or "down" or "upstairs" or "downstairs":
                Go(verb); break;
            case "up" when words.Count == 1: Go("up"); break;
            case "go" or "walk" or "run" when noun.Length > 0: Go(noun); break;
            case "l" or "look": if (noun.Length > 0) Examine(noun); else Look(); break;
            case "i" or "inv" or "inventory": Inventory(); break;
            case "x" or "examine" or "read" or "inspect": Examine(noun); break;
            case "take" or "get" or "grab" or "pick": Take(noun); break;
            case "drop": Drop(noun); break;
            case "open": Open(noun); break;
            case "close" or "shut": con.Write("You leave it as it is. Closing things is for grown-ups.\n"); break;
            case "search" or "check": Search(noun); break;
            case "turn" or "switch" when raw.Contains(" off "): _light = false; con.Write("Click. Darkness, at least where the flashlight points.\n"); break;
            case "turn" or "switch": TurnOn(noun); break;
            case "light": TurnOn("flashlight"); break;
            case "connect" or "plug" or "install" or "attach" or "hook" or "use" or "insert" or "put" or "throw" or "dump" or "empty":
                Use(words.Skip(1).ToList()); break;
            case "give" or "show": Give(noun); break;
            case "talk" or "ask" or "say" or "hello" or "hi": Talk(noun); break;
            case "dial" or "call" or "online" or "surf" or "login" or "log": Dial(); break;
            case "wait" or "z": con.Write("Time passes. The cartoons go to commercials.\n"); break;
            case "score": con.Write($"Your score is {Score} out of 100, in {_moves} moves.\n"); break;
            case "help" or "hint": Hint(); break;
            case "xyzzy" or "plugh": con.Write("A hollow voice says \"Wrong game. This one has dial tones.\"\n"); break;
            case "jump": con.Write("Wheee! The ceiling stars wobble.\n"); break;
            case "sleep": con.Write("Sleep? It is a Saturday night. The internet is waiting.\n"); break;
            case "eat": con.Write(noun == "trash" ? "Absolutely not.\n" : "You are not hungry. You are online-hungry.\n"); break;
            case "quit" or "q" or "exit":
                con.Write($"\nYou scored {Score} out of 100 in {_moves} moves.\n");
                Finished = true;
                return;
            default: con.Write("I don't know the word \"" + verb + "\".\n"); _moves--; break;
        }
        con.Write("\n");
    }

    private bool Here(string item) => _where.TryGetValue(item, out var w) && (w == _room || w == "inv");
    private bool Carrying(string item) => _where.GetValueOrDefault(item) == "inv";
    private bool CanSee => !_rooms[_room].Dark || (_light && Carrying("flashlight"));

    private void Award(string why, int points)
    {
        if (!_scored.Add(why)) return;
        Score += points;
        con.Color(10, 0);
        con.Write($"[Your score just went up by {points} points.]\n");
        con.Color(7, 0);
    }

    private void Go(string dir)
    {
        dir = dir switch { "n" => "north", "s" => "south", "e" => "east", "w" => "west", "u" or "upstairs" => "up", "d" or "downstairs" => "down", _ => dir };
        if (!_rooms[_room].Exits.TryGetValue(dir, out var next)) { con.Write("You can't go that way.\n"); return; }
        if (!CanSee && _room == "basement" && dir != "up") { con.Write("You stub your toe on something in the dark.\n"); return; }
        _room = next;
        Look();
    }

    private void Look()
    {
        var room = _rooms[_room];
        con.Color(15, 0);
        con.Write(room.Name + "\n");
        con.Color(7, 0);
        if (!CanSee)
        {
            con.Write("It is pitch dark. You might trip over a laundry basket. The light from the stairs barely reaches the bottom step.\n");
            return;
        }
        con.Write(room.Description + "\n");
        if (_room == "kitchen")
            con.Write(_mumOff ? "Mum is reading the paper at the table, humming.\n" : "Mum is on the phone with Aunt Linda, twirling the cord. She waves you away without looking.\n");
        if (_room == "bedroom" && _modemConnected) con.Write("The modem sits next to the computer, its lights blinking.\n");
        if (_room == "kitchen" && _drawerOpen && _where["flashlight"] == "drawer") con.Write("The junk drawer is open. A flashlight lies among the rubber bands.\n");
        if (_room == "garage" && _boxOpen && _where["modem"] == "box") con.Write("An opened box holds a modem in polystyrene.\n");
        if (_room == "yard" && _mailboxOpen && _where["cd"] == "mailbox") con.Write("The mailbox is open. There's a CD in it.\n");
        foreach (var (id, w) in _where)
            if (w == _room) con.Write($"There is {Items[id].Name} here.\n");
    }

    private void Inventory()
    {
        var held = _where.Where(p => p.Value == "inv").Select(p => Items[p.Key].Name).ToList();
        con.Write(held.Count == 0 ? "You are empty-handed.\n" : "You are carrying:\n" + string.Concat(held.Select(h => "  " + h + "\n")));
    }

    private void Examine(string noun)
    {
        if (noun.Length == 0) { Look(); return; }
        if (!CanSee) { con.Write("It's too dark to see.\n"); return; }
        if (Items.TryGetValue(noun, out var item) && Here(noun)) { con.Write(item.Look + (noun == "flashlight" && _light ? " It is on.\n" : "\n")); return; }
        var text = (noun, _room) switch
        {
            ("computer", "bedroom") => "A 486 DX2/66 with 8 MB of RAM and a CD-ROM drive. " +
                (_modemConnected ? "The modem is connected. " : "It has an empty serial port at the back, perfect for a modem. ") +
                (_cdInstalled ? "The trial software is installed." : "No internet software is installed yet."),
            ("poster", "bedroom") => "The band is wearing a lot of flannel.",
            ("stars", "bedroom") or ("ceiling", "bedroom") => "You arranged them into the Big Dipper. Mostly.",
            ("photos", "hallway") or ("photo", "hallway") => "Everyone is wearing denim. Even the dog.",
            ("television", "living") => "A cartoon about four turtles who are also teenagers. Probably.",
            ("couch", "living") => _couchSearched ? "Plaid and very sat-in." : "Plaid and very sat-in. Things fall into couches like this.",
            ("mailbox", "yard") => _mailboxOpen ? "Open." : "A metal mailbox with the flag up.",
            ("phone", "kitchen") => _mumOff ? "The kitchen phone hangs on the wall, free at last." : "Mum is using it. The line is very much busy.",
            ("mum", "kitchen") => _mumOff ? "She is enjoying the quiet." : "She is deep in conversation about Aunt Linda's new neighbours. 'And the CAR they bought...'",
            ("drawer", "kitchen") => _drawerOpen ? "Rubber bands, takeout menus, batteries of unknown charge." : "The junk drawer. It is closed.",
            ("box", "garage") => _boxOpen ? "Mostly polystyrene peanuts now." : "One of the boxes is labelled MODEM - DO NOT OPEN UNTIL BIRTHDAY. It is past your birthday.",
            ("bin", "garage") => "A big green bin. It's nearly bin day.",
            ("bicycle", "garage") or ("bike", "garage") => "The streamers flutter when you breathe on them.",
            ("crate", "basement") => "MISC. The most honest label in the house.",
            _ => "You see nothing special about that.",
        };
        con.Write(text + "\n");
    }

    private void Take(string noun)
    {
        if (noun is "all" or "everything")
        {
            var any = false;
            foreach (var id in _where.Where(p => p.Value == _room).Select(p => p.Key).ToList()) { Take(id); any = true; }
            if (!any) con.Write("There is nothing here to take.\n");
            return;
        }
        if (!CanSee) { con.Write("You grope around in the dark and find only dust bunnies.\n"); return; }
        if (!Items.ContainsKey(noun))
        {
            con.Write(noun switch
            {
                "computer" => "It weighs as much as you do. It stays on the desk.",
                "phone" => _mumOff ? "You can't take the kitchen phone off the wall. Your own line is upstairs anyway." : "Mum gives you a look that could stop a clock.",
                "mum" => "That's not how this works.",
                "" => "Take what?",
                _ => "You can't take that.",
            } + "\n");
            return;
        }
        var w = _where[noun];
        var reachable = w == _room || (w == "drawer" && _drawerOpen && _room == "kitchen") || (w == "box" && _boxOpen && _room == "garage") || (w == "mailbox" && _mailboxOpen && _room == "yard");
        if (w == "inv") { con.Write("You already have that.\n"); return; }
        if (!reachable) { con.Write("You don't see that here.\n"); return; }
        _where[noun] = "inv";
        con.Write("Taken.\n");
        switch (noun)
        {
            case "flashlight": Award("flashlight", 10); break;
            case "cord": Award("cord", 15); break;
            case "modem": Award("modem", 10); break;
            case "cd": Award("cd", 10); break;
        }
    }

    private void Drop(string noun)
    {
        if (!Carrying(noun)) { con.Write("You aren't carrying that.\n"); return; }
        if (noun == "trash" && _room == "garage") { Use(["trash", "bin"]); return; }
        _where[noun] = _room;
        con.Write("Dropped.\n");
    }

    private void Open(string noun)
    {
        switch (noun, _room)
        {
            case ("drawer", "kitchen"):
                _drawerOpen = true;
                con.Write(_where["flashlight"] == "drawer" ? "You pull the drawer open. Among the rubber bands lies a flashlight.\n" : "Opened.\n");
                break;
            case ("box", "garage"):
                _boxOpen = true;
                con.Write(_where["modem"] == "box" ? "You tear the tape. Polystyrene peanuts everywhere! Inside is a brand-new modem.\n" : "It's already open.\n");
                break;
            case ("mailbox", "yard"):
                _mailboxOpen = true;
                con.Write(_where["cd"] == "mailbox" ? "Opening the mailbox reveals a CD in a shiny cardboard sleeve.\n" : "Empty, apart from a spider.\n");
                break;
            case ("door", _): con.Write("Just walk through it.\n"); break;
            default: con.Write("You can't open that.\n"); break;
        }
    }

    private void Search(string noun)
    {
        if (noun == "couch" && _room == "living")
        {
            if (_couchSearched) { con.Write("Just crumbs now.\n"); return; }
            _couchSearched = true;
            _where["quarter"] = "living";
            con.Write("Down the back of the cushions you find a quarter, three popcorn kernels and a pog.\n");
            Award("couch", 5);
            return;
        }
        if (noun == "crate" && _room == "basement") { con.Write(CanSee ? "Cables, cables, cables. One of them looks like a phone cord.\n" : "It's too dark.\n"); return; }
        Examine(noun);
    }

    private void TurnOn(string noun)
    {
        if (noun is "flashlight" or "")
        {
            if (!Carrying("flashlight")) { con.Write("You don't have a flashlight.\n"); return; }
            _light = true;
            con.Write("Click. A yellow beam that smells faintly of batteries.\n");
            if (_room == "basement") Look();
            return;
        }
        if (noun is "computer") { con.Write("The computer whirs, beeps once and shows a C:\\> prompt. It's already on, really.\n"); return; }
        if (noun is "television") { con.Write("It's already on. It's always on.\n"); return; }
        con.Write("You can't turn that on.\n");
    }

    private void Use(List<string> words)
    {
        bool Has(string w) => words.Contains(w);
        if (Has("trash") || (Has("bin") && Carrying("trash")))
        {
            if (!Carrying("trash")) { con.Write("You aren't carrying the trash.\n"); return; }
            if (_room != "garage") { con.Write("The bin is in the garage.\n"); return; }
            _where.Remove("trash");
            con.Write("You heave the bag into the bin. Thunk. From the kitchen you hear: \"Linda, I have to go, the kid actually took the trash out! I'll call you back.\" Click.\n");
            _mumOff = true;
            Award("trash", 15);
            return;
        }
        if (Has("modem") || Has("cord"))
        {
            if (_room != "bedroom") { con.Write("You'd need to be at your computer.\n"); return; }
            if (!Carrying("modem")) { con.Write("You don't have a modem to connect.\n"); return; }
            if (!Carrying("cord")) { con.Write("You plug the modem into the computer, but you have nothing to connect it to the phone socket.\n"); return; }
            if (_modemConnected) { con.Write("It's already connected.\n"); return; }
            _modemConnected = true;
            _where.Remove("modem");
            _where.Remove("cord");
            con.Write("You plug the modem into the serial port and run the phone cord to the wall socket. The little red lights blink to life.\n");
            Award("connected", 10);
            return;
        }
        if (Has("cd"))
        {
            if (_room != "bedroom") { con.Write("The CD-ROM drive is on your computer, in the bedroom.\n"); return; }
            if (!Carrying("cd")) { con.Write("You don't have it.\n"); return; }
            _cdInstalled = true;
            _where.Remove("cd");
            con.Write("The drive tray slides out. Whirr. Installing... 12%... 47%... 99%... 99%... 99%... 100%! A new icon appears: a running stick figure.\n");
            Award("installed", 10);
            return;
        }
        if (Has("flashlight")) { TurnOn("flashlight"); return; }
        if (Has("computer") || Has("phone")) { Dial(); return; }
        if (Has("quarter")) { con.Write("There's no arcade here. You save it for later.\n"); return; }
        con.Write("You can't see how to do that.\n");
    }

    private void Give(string noun)
    {
        if (_room != "kitchen" || _mumOff) { con.Write("There's nobody here who wants that.\n"); return; }
        con.Write(noun == "trash" && Carrying("trash")
            ? "Mum points at the garage without missing a word of the conversation.\n"
            : "Mum covers the mouthpiece. \"Not now, sweetie. And take the trash out, it's bin day tomorrow!\"\n");
    }

    private void Talk(string noun)
    {
        if (_room == "kitchen")
            con.Write(_mumOff ? "\"Thank you for taking the trash out. You can use the phone now, but only for an hour!\"\n"
                              : "Mum covers the mouthpiece. \"I'm talking to Linda. Take the trash out to the garage, would you?\"\n");
        else con.Write("You talk to yourself for a bit. You agree on most things.\n");
    }

    private void Dial()
    {
        if (_room != "bedroom") { con.Write("Your computer is in your bedroom.\n"); return; }
        if (!_modemConnected) { con.Write("You need a modem connected to the computer and the phone line.\n"); return; }
        if (!_cdInstalled) { con.Write("The modem is ready, but there's no internet software on the computer.\n"); return; }
        if (!_mumOff)
        {
            con.Write("ATDT 555-0196... The modem picks up and you hear, very loudly through the speaker:\n\"...AND THEN SHE SAID... Is someone on the line? GET OFF THE PHONE, I'M TALKING TO LINDA!\"\nNO CARRIER\n");
            return;
        }
        con.Write("ATDT 555-0196\n\n");
        con.Color(11, 0);
        con.Write("  *beeeeeeee* *kssshhhhhh* *bdoing bdoing* *eeeeeEEEEE* *kchhhhhhhhhhh*\n\n");
        con.Color(7, 0);
        con.Write("Dialing... Verifying password... Connecting at 28,800 bps...\n\n");
        con.Color(14, 0);
        con.Write("  Welcome! You have 1 new message.\n\n");
        con.Color(7, 0);
        con.Write("  From: avamux\n  Subject: you made it\n\n  Thanks for playing. Now go and make a homepage with a guestbook and a hit counter.\n\n");
        _online = true;
        Award("online", 15);
        con.Color(15, 0);
        con.Write($"*** You are online ***\n\nYou scored {Score} out of 100 in {_moves} moves, earning the rank of {(Score >= 100 ? "Webmaster" : Score >= 90 ? "Power User" : "Newbie")}.\n");
        con.Color(7, 0);
        Finished = true;
    }

    private void Hint()
    {
        var hint =
            !Carrying("flashlight") && !_modemConnected ? "The basement is dark. Kitchen junk drawers always have a flashlight in them." :
            !Carrying("cord") && !_modemConnected ? "You'll need a phone cord. Try the basement, with a light on." :
            !Carrying("modem") && !_modemConnected ? "Didn't you get a modem for your birthday? Check the garage." :
            !_modemConnected ? "Take the modem and the cord to your computer and CONNECT MODEM." :
            !_cdInstalled && _where.GetValueOrDefault("cd") != "inv" ? "Those free trial CDs turn up in every mailbox." :
            !_cdInstalled ? "INSERT CD at your computer." :
            !_mumOff ? "Mum won't get off the phone until you do a chore. She mentioned the trash." :
            "Everything's ready. Go to your computer and DIAL.";
        con.Write("Hint: " + hint + "\nWords I know: LOOK, GO NORTH (or N, S, E, W, UP, DOWN), TAKE, DROP, OPEN, SEARCH, TURN ON, CONNECT, INSERT, TALK, DIAL, INVENTORY, SCORE, QUIT.\n");
    }
}
