using System.Globalization;
using System.Text;

namespace Avamux.Core.EasterEggs;

/// <summary>
/// AVATREK.BAS: a space strategy game of the 1970s kind, played one typed order at a time. The starship
/// PURRSEVERANCE and her crew of cats have a few dozen stardates to clear the galaxy of Barkon warships, armed with
/// a laser pointer, hairball torpedoes and fur shields, with a catnip starbase or two to refuel at. Brave cats,
/// going where no cat has gone before.
/// </summary>
public sealed class AvaTrek : ITextGame
{
    private enum Thing { Empty, Ship, Barkon, Base, Star }

    private sealed class Barkon(int x, int y, double energy)
    {
        public readonly int X = x, Y = y;
        public double Energy = energy;
    }

    public const int MaxEnergy = 3000, MaxHairballs = 10;
    private const int Drive = 0, Whiskers = 1, Ears = 2, Laser = 3, Tubes = 4, Control = 5, Fur = 6, Computer = 7;
    private static readonly string[] Devices = ["Zoomies drive", "Whisker sensors", "Long-range ears", "Laser pointer", "Hairball tubes", "Damage control", "Fur shields", "Litter computer"];
    private static readonly string[] West = ["Tabby", "Calico", "Siamese", "Persian", "Bengal", "Ragdoll", "Sphynx", "Manx"];
    private static readonly string[] East = ["Abyssinian", "Burmese", "Chartreux", "Korat", "Savannah", "Bombay", "Havana", "Somali"];
    private static readonly string[] Numerals = ["I", "II", "III", "IV"];
    private static readonly string[] Barks = ["\"WOOF!\"", "\"Arf! Arf! Arf!\"", "\"Grrrrr...\"", "\"BORK!\"", "\"Awooooo!\""];
    private static readonly string[] Gone = ["It whimpers off into hyperspace.", "Its last bark echoes across the quadrant.", "Nothing is left but a chewed tennis ball.", "It rolls over and plays dead. For good."];
    private static readonly string[] Mishaps = ["A kitten got into the wiring.", "Someone knocked a mug off the console.", "It was sat on. For hours.", "It was used as a scratching post."];

    private readonly IDosConsole _con;
    private readonly Random _rnd;
    private readonly int[,] _barkons = new int[8, 8], _bases = new int[8, 8], _stars = new int[8, 8];
    private readonly bool[,] _charted = new bool[8, 8];
    private readonly Thing[,] _map = new Thing[8, 8];
    private readonly List<Barkon> _here = [];
    private readonly double[] _damage = new double[Devices.Length];
    private int _qx, _qy, _sx, _sy, _hairballs = MaxHairballs, _barkonsLeft, _basesLeft;
    private readonly int _barkonsAtStart;
    private double _energy = MaxEnergy, _shields, _stardate;
    private readonly double _startDate, _days;
    private bool _docked;
    private Action<string>? _pending;
    private string? _question;

    public AvaTrek(IDosConsole con, int? seed = null)
    {
        _con = con;
        _rnd = seed is { } s ? new Random(s) : new Random();
        _stardate = _startDate = 100 * (20 + _rnd.Next(20));
        _days = 25 + _rnd.Next(10);
        for (var x = 0; x < 8; x++)
        for (var y = 0; y < 8; y++)
        {
            var r = _rnd.NextDouble();
            _barkons[x, y] = r > 0.98 ? 3 : r > 0.95 ? 2 : r > 0.80 ? 1 : 0;
            _bases[x, y] = _rnd.NextDouble() > 0.96 ? 1 : 0;
            _stars[x, y] = 1 + _rnd.Next(8);
            _barkonsLeft += _barkons[x, y];
            _basesLeft += _bases[x, y];
        }
        if (_basesLeft == 0) { _bases[_rnd.Next(8), _rnd.Next(8)] = 1; _basesLeft = 1; }
        if (_barkonsLeft == 0) { _barkons[_rnd.Next(8), _rnd.Next(8)] = 1; _barkonsLeft = 1; }
        if (_barkonsLeft >= _days) _days = _barkonsLeft + 1;
        _barkonsAtStart = _barkonsLeft;
        (_qx, _qy, _sx, _sy) = (_rnd.Next(8), _rnd.Next(8), _rnd.Next(8), _rnd.Next(8));
    }

    public string Prompt => Finished ? "" : _question ?? "Command? ";
    public bool Finished { get; private set; }
    public bool Won { get; private set; }

    internal int BarkonsLeft => _barkonsLeft;
    internal int BarkonsHere => _here.Count;
    internal int Hairballs => _hairballs;
    internal double Energy => _energy;
    internal double Shields => _shields;
    internal bool Docked => _docked;
    internal (int X, int Y) Quadrant => (_qx, _qy);
    internal (int X, int Y) Sector => (_sx, _sy);
    internal bool Broken(int device) => _damage[device] < 0;
    internal int BarkonsIn(int x, int y) => _barkons[x, y];

    // ----- Talking to the console -----

    private static string I(FormattableString f) => FormattableString.Invariant(f);

    private void Say(string text, int fg = 7)
    {
        _con.Color(fg, 0);
        _con.Write(text);
        _con.Color(7, 0);
    }

    private void Ask(string question, Action<string> answer)
    {
        _question = question;
        _pending = answer;
    }

    /// <summary>A number from the order line (NAV 3 1) or, when it was not given, asked for.</summary>
    private void Number(string[] args, int i, string question, Action<double> then)
    {
        if (i < args.Length)
        {
            if (TryNumber(args[i], out var v)) then(v);
            else Say("That is not a number, Captain.\n");
            return;
        }
        Ask(question, a =>
        {
            if (a.Length == 0) return;
            if (TryNumber(a, out var v)) then(v);
            else Say("That is not a number, Captain.\n");
        });
    }

    private static bool TryNumber(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    // ----- The game -----

    public void Start()
    {
        _con.Clear();
        Say("""

                                       /\_____/\
                                      /  o   o  \
                             ________(  =  ^  =  )________
                            /________ \_________/ ________\
                                     `---.   .---'
                                          `-'

            """, 14);
        Say("                         A V A T R E K\n", 15);
        Say("                 Where no cat has gone before\n", 11);
        Say("                Copyright (C) avamux soft 1996\n\n", 8);
        Say(I($"You are the captain of the starship PURRSEVERANCE. {_barkonsLeft} Barkon warships have\n"));
        Say(I($"invaded the galaxy, and they will not stop barking. They must all be gone by\n"));
        Say(I($"stardate {_startDate + _days:0}, which gives you {_days:0} stardates. There {(_basesLeft == 1 ? "is 1 catnip starbase" : $"are {_basesLeft} catnip starbases")}\n"));
        Say("in the galaxy to refuel at.\n\n");
        Say("Type HELP for the list of orders.\n\n");
        Ask("Press ENTER to take command. ", _ =>
        {
            _con.Clear();
            Say(I($"Your mission begins in the {Name(_qx, _qy)} quadrant.\n"));
            Arrive();
        });
    }

    public void Enter(string line)
    {
        if (_pending is { } answer)
        {
            _pending = null;
            _question = null;
            answer(line.Trim());
            return;
        }
        var words = line.Trim().ToUpperInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        var order = words[0].Length > 3 ? words[0][..3] : words[0];
        var args = words[1..];
        switch (order)
        {
            case "NAV" or "WAR" or "GO":
                Number(args, 0, "Course (1-9)? ", c =>
                {
                    if (!Heading(c, out _, out _)) Say("Navigator Socks reports: 'Incorrect course data, Captain!'\n");
                    else Number(args, 1, I($"Warp factor (0-{MaxWarp:0.#})? "), w => Navigate(c, w));
                });
                break;
            case "SRS" or "S": ShortScan(); break;
            case "LRS" or "L": LongScan(); break;
            case "LAS" or "PHA" or "DOT": FireLaser(args); break;
            case "HAI" or "TOR" or "COU": FireHairball(args); break;
            case "SHI" or "FLU": RaiseShields(args); break;
            case "DAM": DamageReport(); break;
            case "COM": if (Working(Computer, "The litter computer is disabled.")) Number(args, 0, "Computer active and awaiting command (0-3)? ", v => Compute((int)v)); break;
            case "MAP": if (Working(Computer, "The litter computer is disabled.")) Compute(0); break;
            case "NAP" or "RES" or "SLE": Number(args, 0, "How many stardates will the crew nap? ", Nap); break;
            case "HEL" or "?" or "H": Help(); break;
            case "PUR" or "MEO" or "MRR":
                Say("The crew purrs back. Morale is excellent.\n"); break;
            case "WOO" or "BAR" or "ARF":
                Say("Every cat on the bridge turns to stare at you. Nobody says anything.\n"); break;
            case "QUI" or "XXX" or "EXI" or "Q":
                Say("\nYou hand the bridge to the first officer,\nwho falls asleep in your chair at once.\n");
                End();
                break;
            default:
                Say("Orders are: NAV SRS LRS LAS HAI SHI DAM COM NAP HELP QUIT\n");
                break;
        }
    }

    private bool Working(int device, string message)
    {
        if (_damage[device] >= 0) return true;
        Say(message + "\n", 12);
        return false;
    }

    private string Name(int qx, int qy) => (qx < 4 ? West[qy] : East[qy]) + " " + Numerals[qx % 4];

    private double MaxWarp => _damage[Drive] < 0 ? 0.2 : 8;

    private void Help()
    {
        Say("""

            Orders (the first three letters will do, and numbers can follow: NAV 3 1)
              NAV  Set a course and warp factor on the zoomies drive
              SRS  Short-range scan with the whisker sensors
              LRS  Long-range scan of the quadrants around you, by ear
              LAS  Fire the laser pointer at every Barkon here (costs energy)
              HAI  Cough up a hairball torpedo along a course
              SHI  Fluff up the fur shields (move energy into them)
              DAM  Damage report; docked at a starbase, get repairs
              COM  Litter computer: 0 map, 1 status, 2 hairball data, 3 starbase data
              NAP  The crew naps for a while, and mends the ship
              QUIT Give up the mission

              Course:   4  3  2      1 is east, 3 is north, 2.5 is in between.
                         \ | /       Warp 1 crosses one quadrant (8 sectors).
                      5 - =^= - 1    Dock by stopping next to a starbase (#).
                         / | \       Barkons are >B<, stars are *.
                        6  7  8      Sectors and quadrants are column,row.

            """);
    }

    // ----- Arriving in a quadrant -----

    private void Arrive()
    {
        _charted[_qx, _qy] = true;
        Array.Clear(_map);
        _here.Clear();
        _map[_sx, _sy] = Thing.Ship;
        for (var i = 0; i < _barkons[_qx, _qy]; i++)
        {
            var (x, y) = EmptySector();
            _map[x, y] = Thing.Barkon;
            _here.Add(new Barkon(x, y, 200 * (0.5 + _rnd.NextDouble())));
        }
        for (var i = 0; i < _bases[_qx, _qy]; i++) { var (x, y) = EmptySector(); _map[x, y] = Thing.Base; }
        for (var i = 0; i < _stars[_qx, _qy]; i++) { var (x, y) = EmptySector(); _map[x, y] = Thing.Star; }
        if (_here.Count > 0)
        {
            Say("COMBAT AREA      CONDITION RED\n", 12);
            if (_shields <= 200) Say("   FUR SHIELDS DANGEROUSLY FLAT\n", 12);
        }
        CheckDocked();
        ShortScan();
    }

    private (int X, int Y) EmptySector()
    {
        while (true)
        {
            var (x, y) = (_rnd.Next(8), _rnd.Next(8));
            if (_map[x, y] == Thing.Empty) return (x, y);
        }
    }

    private void CheckDocked()
    {
        var was = _docked;
        _docked = false;
        for (var x = _sx - 1; x <= _sx + 1; x++)
        for (var y = _sy - 1; y <= _sy + 1; y++)
            if (x is >= 0 and < 8 && y is >= 0 and < 8 && _map[x, y] == Thing.Base) _docked = true;
        if (!_docked || was) return;
        _energy = MaxEnergy;
        _hairballs = MaxHairballs;
        if (_shields > 0) Say("Fur shields smoothed down for docking.\n");
        _shields = 0;
        Say("Docked at a catnip starbase. Energy and hairballs restocked. Treats all round.\n", 11);
    }

    private string Condition =>
        _docked ? "DOCKED" : _here.Count > 0 ? "*RED*" : _energy + _shields < MaxEnergy * 0.1 ? "YELLOW" : "GREEN";

    // ----- Scans -----

    private void ShortScan()
    {
        if (!Working(Whiskers, "*** Whisker sensors are out ***")) return;
        var cond = Condition;
        string[] status =
        [
            I($"Stardate          {_stardate:0.0}"),
            "Condition         " + cond,
            I($"Quadrant          {_qx + 1},{_qy + 1}"),
            I($"Sector            {_sx + 1},{_sy + 1}"),
            I($"Hairballs         {_hairballs}"),
            I($"Total energy      {Math.Floor(_energy + _shields):0}"),
            I($"Fur shields       {Math.Floor(_shields):0}"),
            I($"Barkons remaining {_barkonsLeft}"),
        ];
        const string rule = "---------------------------------\n";
        Say(rule);
        for (var y = 0; y < 8; y++)
        {
            _con.Write(" ");
            for (var x = 0; x < 8; x++)
            {
                var (text, fg) = _map[x, y] switch
                {
                    Thing.Ship => ("=^=", 14), Thing.Barkon => (">B<", 12), Thing.Base => ("(#)", 11), Thing.Star => (" * ", 15), _ => (" . ", 8),
                };
                Say(text, fg);
            }
            _con.Write("        ");
            Say(status[y] + "\n", y == 1 ? cond switch { "*RED*" => 12, "YELLOW" => 14, "DOCKED" => 11, _ => 10 } : 7);
        }
        Say(rule);
    }

    private string Code(int x, int y) => I($"{_barkons[x, y]}{_bases[x, y]}{_stars[x, y]}");

    private void LongScan()
    {
        if (!Working(Ears, "The long-range ears are flat back. They will not listen to anything.")) return;
        Say(I($"Long-range scan for quadrant {_qx + 1},{_qy + 1}\n"));
        const string rule = "-------------------\n";
        Say(rule);
        for (var y = _qy - 1; y <= _qy + 1; y++)
        {
            for (var x = _qx - 1; x <= _qx + 1; x++)
            {
                _con.Write(": ");
                if (x is < 0 or > 7 || y is < 0 or > 7) { Say("***", 8); }
                else
                {
                    _charted[x, y] = true;
                    Say(Code(x, y), x == _qx && y == _qy ? 14 : _barkons[x, y] > 0 ? 12 : 7);
                }
                _con.Write(" ");
            }
            _con.Write(":\n");
            Say(rule);
        }
        Say("(Barkons, starbases, stars)\n", 8);
    }

    // ----- Moving -----

    /// <summary>The step for a course: one sector along the main direction per step.</summary>
    private static bool Heading(double course, out double dx, out double dy)
    {
        dx = dy = 0;
        if (course is < 1 or > 9) return false;
        var a = (course - 1) * Math.PI / 4;
        dx = Math.Cos(a);
        dy = -Math.Sin(a);
        var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        dx /= m;
        dy /= m;
        if (Math.Abs(dx) < 1e-9) dx = 0;
        if (Math.Abs(dy) < 1e-9) dy = 0;
        return true;
    }

    private static int Round(double v) => (int)Math.Floor(v + 0.5);

    private void Navigate(double course, double warp)
    {
        if (!Heading(course, out var dx, out var dy)) { Say("Navigator Socks reports: 'Incorrect course data, Captain!'\n"); return; }
        if (warp <= 0) return;
        if (warp > MaxWarp)
        {
            Say(_damage[Drive] < 0
                ? "The zoomies drive is damaged. Maximum speed is warp 0.2.\n"
                : I($"Chief Engineer Purrkins reports: 'The engines won't take warp {warp}!'\n"));
            return;
        }
        var steps = Round(warp * 8);
        if (steps == 0) { Say("At that warp factor the ship would not get anywhere.\n"); return; }
        if (_energy < steps + 10)
        {
            Say(I($"Engineering reports: 'Not enough energy for warp {warp}!'\n"));
            if (_shields >= steps + 10 - _energy && _damage[Fur] >= 0)
                Say(I($"(The fur shields are holding {Math.Floor(_shields):0} units. SHI moves energy out of them.)\n"));
            return;
        }

        // The Barkons fire as the ship gets going.
        BarkonsFire();
        if (Finished) return;
        RepairOnTheWay(warp);
        if (Finished) return;

        _map[_sx, _sy] = Thing.Empty;
        int nx = _sx, ny = _sy;
        var left = false;
        for (var i = 1; i <= steps; i++)
        {
            int rx = Round(_sx + dx * i), ry = Round(_sy + dy * i);
            if (rx is < 0 or > 7 || ry is < 0 or > 7) { left = true; break; }
            if (_map[rx, ry] != Thing.Empty)
            {
                var what = _map[rx, ry] switch { Thing.Star => "a star", Thing.Base => "a starbase", _ => "a Barkon" };
                Say(I($"The zoomies drive shut down at sector {nx + 1},{ny + 1}: {what} is in the way.\n"));
                break;
            }
            (nx, ny) = (rx, ry);
        }

        _energy -= steps + 10;
        if (_energy < 0)
        {
            Say("Shield control gives up energy to finish the manoeuvre.\n");
            _shields = Math.Max(0, _shields + _energy);
            _energy = 0;
        }

        if (left)
        {
            int gx = Round(_qx * 8 + _sx + dx * steps), gy = Round(_qy * 8 + _sy + dy * steps);
            var edge = gx is < 0 or > 63 || gy is < 0 or > 63;
            gx = Math.Clamp(gx, 0, 63);
            gy = Math.Clamp(gy, 0, 63);
            if (edge)
                Say("Feline Space Command reports: 'Permission to leave the galaxy is denied.\nThere is nothing out there but vacuum cleaners.'\nThe navigator stops at the edge.\n");
            if (gx / 8 != _qx || gy / 8 != _qy)
            {
                (_qx, _qy, _sx, _sy) = (gx / 8, gy / 8, gx % 8, gy % 8);
                _stardate += 1;
                if (OutOfTime()) return;
                Say(I($"Now entering the {Name(_qx, _qy)} quadrant . . .\n"));
                Arrive();
                Stranded();
                return;
            }
        }

        (_sx, _sy) = (nx, ny);
        _map[_sx, _sy] = Thing.Ship;
        _stardate += warp < 1 ? Math.Max(0.1, Math.Floor(warp * 10) / 10) : 1;
        if (OutOfTime()) return;
        CheckDocked();
        ShortScan();
        Stranded();
    }

    private void RepairOnTheWay(double warp)
    {
        var mend = Math.Min(1, warp);
        var reported = false;
        for (var d = 0; d < Devices.Length; d++)
        {
            if (_damage[d] >= 0) continue;
            _damage[d] += mend;
            if (_damage[d] is > -0.1 and < 0) _damage[d] = -0.1;
            else if (_damage[d] >= 0)
            {
                if (!reported) Say("Damage control report:\n");
                reported = true;
                _damage[d] = 0;
                Say($"        {Devices[d]} repair completed.\n", 10);
            }
        }
        var r = _rnd.NextDouble();
        if (r <= 0.2)
        {
            var d = _rnd.Next(Devices.Length);
            if (_rnd.NextDouble() < 0.6)
            {
                _damage[d] -= _rnd.NextDouble() * 5 + 1;
                Say($"Damage control report: {Devices[d]} damaged.\n   {Mishaps[_rnd.Next(Mishaps.Length)]}\n", 14);
            }
            else if (_damage[d] < 0)
            {
                _damage[d] = Math.Min(0, _damage[d] + _rnd.NextDouble() * 3 + 1);
                Say($"Damage control report: {Devices[d]} state of repair improved.\n", 10);
            }
        }
        else if (r <= 0.25)
        {
            _stardate += 0.2;
            Say("A red dot appeared on the bridge wall. The whole crew chased it.\n   0.2 stardates lost.\n", 12);
            OutOfTime();
        }
    }

    private void Nap(double stardates)
    {
        if (stardates <= 0) return;
        if (_here.Count > 0) { Say("Nobody could nap through that barking. Deal with the Barkons first.\n"); return; }
        if (stardates >= _startDate + _days - _stardate) { Say("Captain, that is longer than the time the mission has left.\n"); return; }
        _stardate += stardates;
        Say(I($"The crew curls up in a warm patch of starlight.\n{stardates:0.#} stardate{(stardates == 1 ? "" : "s")} later, everyone stretches.\n"));
        for (var d = 0; d < Devices.Length; d++)
        {
            if (_damage[d] >= 0) continue;
            _damage[d] += stardates;
            if (_damage[d] >= 0) { _damage[d] = 0; Say($"        {Devices[d]} repair completed.\n", 10); }
        }
    }

    // ----- Fighting -----

    private static double Distance(int x0, int y0, int x1, int y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    private void FireLaser(string[] args)
    {
        if (!Working(Laser, "The laser pointer is broken. The crew is heartbroken.")) return;
        if (_here.Count == 0) { Say("Science officer Whiskerton: 'Sensors show no Barkons in this quadrant.'\n"); return; }
        if (_damage[Computer] < 0) Say("Litter computer failure hampers accuracy.\n", 14);
        Number(args, 0, I($"Laser pointer locked on target. Energy available = {Math.Floor(_energy):0}. Units to fire? "), amount =>
        {
            if (amount <= 0) return;
            if (amount > _energy) { Say(I($"Only {Math.Floor(_energy):0} units are available, Captain.\n")); return; }
            _energy -= amount;
            if (_damage[Computer] < 0) amount *= _rnd.NextDouble();
            var share = amount / _here.Count;
            Say("The red dot dances across the Barkon hulls...\n");
            foreach (var b in _here.ToList())
            {
                var hit = share / Distance(_sx, _sy, b.X, b.Y) * (_rnd.NextDouble() + 2);
                if (hit <= 0.15 * b.Energy) { Say(I($"Sensors show no damage to the Barkon at {b.X + 1},{b.Y + 1}.\n")); continue; }
                b.Energy -= hit;
                Say(I($"{hit:0} unit hit on the Barkon at sector {b.X + 1},{b.Y + 1}.\n"));
                if (b.Energy <= 0) { Destroyed(b); if (Finished) return; }
                else Say(I($"   (sensors show {b.Energy:0} units remaining)\n"));
            }
            BarkonsFire();
            Stranded();
        });
    }

    private void FireHairball(string[] args)
    {
        if (!Working(Tubes, "The hairball tubes are blocked.")) return;
        if (_hairballs <= 0) { Say("The crew has coughed up every last hairball. Dock at a starbase for more.\n"); return; }
        Number(args, 0, "Hairball course (1-9)? ", course =>
        {
            if (!Heading(course, out var dx, out var dy)) { Say("Ensign Paws reports: 'Incorrect course data, Captain!'\n"); return; }
            _hairballs--;
            _energy = Math.Max(0, _energy - 2);
            Say("*HACK* *HACK* ... Hairball track:\n");
            for (var i = 1; ; i++)
            {
                int x = Round(_sx + dx * i), y = Round(_sy + dy * i);
                if (x is < 0 or > 7 || y is < 0 or > 7) { Say("Hairball missed.\n"); break; }
                Say(I($"               {x + 1},{y + 1}\n"), 8);
                var thing = _map[x, y];
                if (thing == Thing.Empty) continue;
                if (thing == Thing.Barkon)
                {
                    Destroyed(_here.First(b => b.X == x && b.Y == y));
                    if (Finished) return;
                }
                else if (thing == Thing.Star) Say(I($"The star at {x + 1},{y + 1} absorbs the hairball. Ew.\n"));
                else if (thing == Thing.Base)
                {
                    _map[x, y] = Thing.Empty;
                    _bases[_qx, _qy]--;
                    _basesLeft--;
                    _docked = false;
                    Say("*** CATNIP STARBASE DESTROYED ***\n", 12);
                    Say("Feline Space Command is reviewing your record. It is not looking good.\n");
                }
                break;
            }
            BarkonsFire();
            Stranded();
        });
    }

    private void Destroyed(Barkon b)
    {
        _map[b.X, b.Y] = Thing.Empty;
        _here.Remove(b);
        _barkons[_qx, _qy]--;
        _barkonsLeft--;
        Say(I($"*** BARKON DESTROYED *** at sector {b.X + 1},{b.Y + 1}\n"), 12);
        Say("   " + Gone[_rnd.Next(Gone.Length)] + "\n");
        if (_barkonsLeft == 0) Win();
    }

    private void BarkonsFire()
    {
        if (_here.Count == 0 || Finished) return;
        if (_docked) { Say("The starbase shields protect the PURRSEVERANCE. The Barkons bark anyway.\n"); return; }
        Say($"The Barkons open fire. {Barks[_rnd.Next(Barks.Length)]}\n", 12);
        foreach (var b in _here)
        {
            if (b.Energy <= 0) continue;
            var hit = b.Energy / Distance(_sx, _sy, b.X, b.Y) * (2 + _rnd.NextDouble());
            _shields -= hit;
            b.Energy /= 3 + _rnd.NextDouble();
            Say(I($"{hit:0} unit hit on the PURRSEVERANCE from sector {b.X + 1},{b.Y + 1}.\n"));
            if (_shields < 0)
            {
                Say("\nThe PURRSEVERANCE has been destroyed.\nAcross the galaxy, cats are chased up trees.\n", 12);
                End();
                return;
            }
            Say(I($"      <fur shields down to {Math.Floor(_shields):0} units>\n"));
            if (hit >= 20 && (_rnd.NextDouble() <= 0.6 || hit / Math.Max(1, _shields) > 0.02))
            {
                var d = _rnd.Next(Devices.Length);
                _damage[d] -= hit / Math.Max(1, _shields) + 0.5 * _rnd.NextDouble();
                Say($"Damage control reports: {Devices[d]} damaged by the hit.\n", 14);
            }
        }
    }

    private void RaiseShields(string[] args)
    {
        if (!Working(Fur, "The fur shields will not fluff.")) return;
        Number(args, 0, I($"Energy available = {Math.Floor(_energy + _shields):0}. Units to put in the fur shields? "), units =>
        {
            if (units < 0 || units == _shields) { Say("<Fur shields unchanged>\n"); return; }
            if (units > _energy + _shields) { Say("Shield control reports: 'We are not made of catnip, Captain.'\n<Fur shields unchanged>\n"); return; }
            _energy += _shields - units;
            _shields = units;
            Say(I($"Fur shields fluffed up to {Math.Floor(_shields):0} units.\n"));
        });
    }

    // ----- Reports -----

    private void DamageReport()
    {
        if (_docked && _damage.Any(d => d < 0))
        {
            var time = Math.Round(Math.Min(0.9, _damage.Where(d => d < 0).Sum(d => -d) * 0.1 + 0.5 * _rnd.NextDouble()), 2);
            Say(I($"Technician cats are standing by to repair your ship.\nEstimated time to repair: {time:0.00} stardates.\n"));
            Ask("Will you authorize the repair order (Y/N)? ", a =>
            {
                if (!a.StartsWith('Y') && !a.StartsWith('y')) return;
                Array.Clear(_damage);
                _stardate += time + 0.1;
                Say("Repairs complete. Everything is purring.\n", 10);
                if (!OutOfTime()) DamageTable();
            });
            return;
        }
        if (Working(Control, "The damage control report is not available.")) DamageTable();
    }

    private void DamageTable()
    {
        Say("\nDevice              State of repair\n");
        for (var d = 0; d < Devices.Length; d++)
            Say(I($"{Devices[d],-20}{Math.Floor(_damage[d] * 100) / 100,8:0.00}\n"), _damage[d] < 0 ? 12 : 7);
    }

    private void Compute(int option)
    {
        switch (option)
        {
            case 0:
            {
                Say(I($"\n        Computer record of the galaxy, from quadrant {_qx + 1},{_qy + 1}\n"));
                var head = new StringBuilder("      ");
                for (var x = 0; x < 8; x++) head.Append(I($"  {x + 1}   "));
                Say(head + "\n");
                Say("      " + string.Concat(Enumerable.Repeat("----- ", 8)) + "\n");
                for (var y = 0; y < 8; y++)
                {
                    Say(I($"   {y + 1}  "));
                    for (var x = 0; x < 8; x++)
                    {
                        var here = x == _qx && y == _qy;
                        Say(" " + (_charted[x, y] ? Code(x, y) : "***") + "  ", here ? 14 : !_charted[x, y] ? 8 : _barkons[x, y] > 0 ? 12 : 7);
                    }
                    _con.Write("\n");
                }
                Say("(Barkons, starbases, stars; *** is not charted yet)\n", 8);
                break;
            }
            case 1:
                Say("\nStatus report:\n");
                Say(I($"  {_barkonsLeft} Barkon{(_barkonsLeft == 1 ? "" : "s")} left.\n"));
                Say(I($"  The mission must be finished in {_startDate + _days - _stardate:0.0} stardates (by stardate {_startDate + _days:0}).\n"));
                Say(_basesLeft > 0
                    ? I($"  There {(_basesLeft == 1 ? "is 1 catnip starbase" : $"are {_basesLeft} catnip starbases")} in the galaxy.\n")
                    : "  There are no starbases left. You are on your own, Captain.\n");
                if (_damage[Control] >= 0) DamageTable();
                break;
            case 2:
                if (_here.Count == 0) { Say("Sensors show no Barkons in this quadrant.\n"); break; }
                Say("Hairball data:\n");
                foreach (var b in _here)
                    Say(I($"  Barkon at sector {b.X + 1},{b.Y + 1}:  course {CourseTo(b.X, b.Y):0.00}, distance {Distance(_sx, _sy, b.X, b.Y):0.00}\n"));
                break;
            case 3:
            {
                var found = false;
                for (var x = 0; x < 8; x++)
                for (var y = 0; y < 8; y++)
                    if (_map[x, y] == Thing.Base)
                    {
                        found = true;
                        Say(I($"Starbase at sector {x + 1},{y + 1}:  course {CourseTo(x, y):0.00}, distance {Distance(_sx, _sy, x, y):0.00}\n"));
                    }
                if (!found) Say("Sensors show no starbases in this quadrant.\n");
                break;
            }
            default:
                Say("The litter computer knows:\n  0 galactic map, 1 status report, 2 hairball data, 3 starbase data\n");
                break;
        }
    }

    /// <summary>The course from the ship to a sector in this quadrant.</summary>
    internal double CourseTo(int x, int y)
    {
        var angle = Math.Atan2(_sy - y, x - _sx) * 180 / Math.PI;
        if (angle < 0) angle += 360;
        return 1 + angle / 45;
    }

    internal IEnumerable<(int X, int Y)> BarkonSectors => _here.Select(b => (b.X, b.Y));

    // ----- Endings -----

    private bool OutOfTime()
    {
        if (_stardate < _startDate + _days) return false;
        Say("\nTime is up, and the Barkons are still barking.\n", 12);
        End();
        return true;
    }

    private void Stranded()
    {
        // The smallest hop costs 11 units; shield energy only helps while the shields can hand it back.
        if (Finished || _energy >= 11 || (_energy + _shields >= 11 && _damage[Fur] >= 0)) return;
        Say("\n** FATAL ERROR ** The PURRSEVERANCE is drifting with no energy left.\nSomebody fetch a ball of yarn.\n", 12);
        End();
    }

    private void Win()
    {
        Won = true;
        var used = Math.Max(0.1, _stardate - _startDate);
        var rating = 1000 * Math.Pow(_barkonsAtStart / used, 2);
        var rank = rating < 100 ? "Kitten" : rating < 500 ? "House Cat" : rating < 1500 ? "Alley Cat" : rating < 3000 ? "Tomcat" : "Lion of the Galaxy";
        Say("\nCongratulations, Captain!\nThe last Barkon warship in the galaxy has been destroyed.\n", 15);
        Say("The galaxy is quiet again, apart from the purring.\n", 11);
        Say(I($"Your efficiency rating is {rating:0}.\nFeline Space Command names you: {rank}.\n"), 14);
        Finished = true;
    }

    private void End()
    {
        Say(I($"It is stardate {_stardate:0.0}. There {(_barkonsLeft == 1 ? "was 1 Barkon warship" : $"were {_barkonsLeft} Barkon warships")} left\nat the end of your mission.\n"));
        Finished = true;
    }
}
