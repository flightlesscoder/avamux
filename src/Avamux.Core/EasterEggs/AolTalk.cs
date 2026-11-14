namespace Avamux.Core.EasterEggs;

/// <summary>A chat room in AOL.EXE: who hangs out there and the sort of thing they say.</summary>
public sealed record AolRoom(string Name, string Topic, string[] People, string[] Lines, int Crowd);

/// <summary>A letter in the AOL.EXE mailbox.</summary>
public sealed record AolMail(string From, string Subject, string Body)
{
    public bool Read { get; set; }
}

/// <summary>
/// Someone on the buddy list. They answer instant messages from a few lists of things they might say, chosen
/// by what was said to them, and now and then have to go because someone needs the phone.
/// </summary>
public sealed class AolBuddy
{
    public required string Name { get; init; }
    public required string Opener { get; init; }
    public required string[] Hello { get; init; }
    public required string[] Status { get; init; }
    public required string[] Answers { get; init; }
    public required string[] Chatter { get; init; }
    public required string Bye { get; init; }
    public required string Brb { get; init; }
    public required string Profile { get; init; }
    public bool Online { get; set; }
    /// <summary>Messages sent to this buddy in the current conversation.</summary>
    public int Heard { get; set; }
    private readonly Dictionary<string[], int> _last = new();

    private string Pick(string[] from, Random rnd)
    {
        var last = _last.GetValueOrDefault(from, -1);
        var i = rnd.Next(from.Length);
        if (i == last && from.Length > 1) i = (i + 1) % from.Length;
        _last[from] = i;
        return from[i];
    }

    /// <summary>The answer to a message, and whether they sign off after it.</summary>
    public (string Text, bool Leaves) Reply(string said, Random rnd)
    {
        var words = AolTalk.Words(said);
        var s = said.ToLowerInvariant();
        Heard++;
        if (words.Overlaps(["bye", "cya", "g2g", "gtg", "ttyl", "goodbye", "later", "l8r", "bbl"]) || s.Contains("gotta go")) return (Bye, true);
        if (Heard >= 6 && rnd.NextDouble() < 0.2) return (Brb, true);
        if (s.Contains("how are") || s.Contains("how r u") || s.Contains("what's up") || s.Contains("whats up") || words.Overlaps(["sup", "wassup", "wazzup", "hows"]))
            return (Pick(Status, rnd), false);
        if (Heard <= 2 && words.Overlaps(["hi", "hey", "hello", "yo", "hiya", "heya", "howdy"])) return (Pick(Hello, rnd), false);
        if (s.Contains('?')) return (Pick(Answers, rnd), false);
        if (words.Overlaps(["lol", "haha", "hehe", "rofl", "heh"])) return (Pick(["lol", "hehe", "ROFL", "lol :)"], rnd), false);
        return (Pick(Chatter, rnd), false);
    }

    /// <summary>What they write back to a letter.</summary>
    public string MailReply(string subject, Random rnd) =>
        $"{Pick(Hello, rnd)}\n\ngot ur mail about \"{subject}\". {Pick(Chatter, rnd)}\n\n{Pick(Status, rnd)}\n\n- {Name}";
}

public static class AolTalk
{
    public const string Host = "OnlineHost";

    internal static HashSet<string> Words(string s) =>
        s.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ':', ';', '"', '(', ')', '*', '~'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static readonly AolRoom[] Rooms =
    [
        new("Town Square - Lobby 42", "Hang out, meet people, say hi!",
            ["ZipDisk99", "CoolBreeze77", "MrsPacMan", "BeanieBabe", "GrungeKid", "SurfinSafari", "CDROMguy", "HappyMeal4U", "Doogie_H", "PogSlammer", "RollerBlade94", "MrModem"],
            [
                "hi all", "any1 from ohio??", "brb pizza", "lol", "ROFL", "who likes Hanson", "my tamagotchi died again :(",
                "i just got a 33.6 modem!!!!", "omg it's sooo fast", "my mom says 10 more min", "{me} hi!", "TOS!!! TOS!!!",
                "who wants to trade pogs", "check out my homepage!! it has a guestbook AND a hit counter",
                "it's under construction tho", "X-Files is on in 10 min", "the truth is out there...", "lag!!!!",
                "*waves*", "{me} where r u from?", "brb", "back", "wb", "my sister picked up the phone and i got kicked off lol",
                "does anyone have the cheat codes for level 3", "{me} do u like the spice girls", "SPACE JAM was the best movie ever",
                "my computer has 8 megs of RAM", "anyone want to start a club", "type /shrug lol jk that doesnt work",
                "*** i'm bored ***", "who's watching Friends tonight", "{me} lol", "BRB my mom needs the phone",
            ], 23),
        new("Computing Help Desk", "Ask the experts! (They are other members.)",
            ["ModemMaster", "Bit_Fiddler", "SysOp_Steve", "QBasicQueen", "IRQ7", "HimemHero", "Turbo_Btn", "DiskDoubler"],
            [
                "how do i get more conventional memory for my games", "put DOS=HIGH,UMB in CONFIG.SYS", "my CD-ROM is 2x is that good",
                "IRQ conflict with my sound card HELP", "windows 95 or stay on 3.1?", "turn it off and on again",
                "just defrag it lol", "what does General Protection Fault mean", "it means a general protection fault",
                "i'm getting a Zip drive. 100 MB on one disk!!", "who could ever fill a gigabyte", "{me} what's your question?",
                "my 486 has a turbo button. it makes it slower", "real programmers use QBasic", "has anyone tried this World Wide Web thing",
                "you need a 14.4 at least", "try LOADHIGH", "{me} did you check the jumpers?", "is your printer on LPT1",
                "my mouse ball is full of fluff", "you can buy 8 more megs of RAM for only $300!", "never trust a floppy",
                "make a boot disk first!!!", "I typed FORMAT C: by accident once. once.", "Y2K is going to be a big deal, watch",
            ], 17),
        new("Trek Fans", "Live long and chat.",
            ["TrekFan1701", "Ensign_Ro", "Tribble_Trouble", "WarpNine", "Q_Continuum", "Holodeck3", "RedShirt42"],
            [
                "Picard > Kirk", "Kirk > Picard", "DS9 is underrated", "new Voyager tonight!", "make it so", "live long and prosper",
                "has anyone been to a convention", "resistance is futile lol", "Data is my favorite", "Q is so annoying lol",
                "{me} who's your favorite captain?", "i have the whole TNG on VHS", "taped over half of season 3 :(",
                "if i had a holodeck i'd never leave", "never wear a red shirt", "tea. earl grey. hot.", "engage!",
                "beam me up, my mom says dinner", "{me} live long and prosper", "the episode with the tribbles!!!",
            ], 11),
        new("Grunge Lounge", "Flannel optional.",
            ["GrungeKid", "Flannel4Life", "SeattleRain", "BeanieKid", "MoshPit", "WhateverGrrl", "CassetteTape", "Lolla96"],
            [
                "flannel 4 life", "my parents hate my music", "concert next week!!", "anyone going to Lollapalooza",
                "whatever.", "{me} what are you listening to?", "i made a mixtape for the bus", "my walkman ate my tape",
                "unplugged albums are the best", "the radio played my song 3 times today", "{me} nice",
                "doc martens or chucks?", "i dyed my hair with kool-aid", "MTV only plays the same 5 videos",
                "it's not a phase mom", "{me} have u heard the new album", "i'm going to start a band", "we need a drummer",
            ], 14),
    ];

    /// <summary>Someone in the room says something, about one line every couple of seconds.</summary>
    public static (string Who, string Text) Ambient(AolRoom room, string me, Random rnd) =>
        (room.People[rnd.Next(room.People.Length)], room.Lines[rnd.Next(room.Lines.Length)].Replace("{me}", me));

    /// <summary>How the room answers something the member typed: delays in seconds, who, and what.</summary>
    public static List<(double Delay, string Who, string Text)> Replies(AolRoom room, string me, string said, Random rnd)
    {
        var words = Words(said);
        var replies = new List<(double, string, string)>();
        string Someone() => room.People[rnd.Next(room.People.Length)];
        if (words.Overlaps(["hi", "hey", "hello", "yo", "hiya", "howdy", "sup"]))
        {
            replies.Add((R(rnd, 1, 2.5), Someone(), Pick(rnd, $"hi {me}!", $"hey {me}", $"{me}!!! hi", $"hiya {me} :)", "wb")));
            if (rnd.Next(2) == 0) replies.Add((R(rnd, 2.5, 4.5), Someone(), Pick(rnd, $"{me} where r u from?", $"hi {me}", "*waves*")));
        }
        else if (said.Contains('?'))
            replies.Add((R(rnd, 1.5, 3.5), Someone(), Pick(rnd, "idk", "good question", $"{me} i think so", "no way", "ask the OnlineHost lol", $"{me} totally", "maybe?")));
        else if (words.Overlaps(["lol", "rofl", "haha"]))
            replies.Add((R(rnd, 1, 2.5), Someone(), Pick(rnd, "lol", "ROFL", "hehe")));
        else if (rnd.NextDouble() < 0.5)
            replies.Add((R(rnd, 1.5, 4), Someone(), Pick(rnd, $"{me} lol", "word", $"{me} i agree", "totally", "same", $"{me} that's so cool", "*nods*")));
        return replies;
    }

    private static double R(Random rnd, double a, double b) => a + rnd.NextDouble() * (b - a);
    private static string Pick(Random rnd, params string[] from) => from[rnd.Next(from.Length)];

    public static List<AolBuddy> NewBuddies() =>
    [
        new()
        {
            Name = "xXSk8rXx", Opener = "sup", Profile = "Location: the mall\nHobbies: skating, video games, not doing homework\nQuote: \"whatever\"",
            Hello = ["sup", "hey whats up", "yo!!"], Status = ["chillin. u?", "bored. my brother is hogging the TV", "just got home from skating"],
            Answers = ["idk lol", "maybe", "prob not", "ya totally", "ask me later"],
            Chatter = ["word", "thats rad", "my new sneakers already have a hole lol", "going to the mall saturday, u in?", "my brother taped the cartoons for me", "i almost landed a kickflip today", "did u finish the math homework", "cool"],
            Bye = "later!!", Brb = "brb my mom is yelling... g2g actually",
        },
        new()
        {
            Name = "SpiceGrl97", Opener = "hey!!! :)", Profile = "Location: my room\nHobbies: dancing, sleepovers, collecting\nQuote: \"Girl power!\"",
            Hello = ["hiiii!!", "hey!! :)", "OMG hi"], Status = ["good!! listening to the radio", "kinda tired, sleepover last nite", "sooo bored"],
            Answers = ["omg yes", "no way!!", "hmm idk", "maybe!!", "totally"],
            Chatter = ["omg", "did u see Friends last nite", "this song is stuck in my head", "lol", "i have 37 beanie babies now", "my best friend is coming over later", "we're making a dance routine", ":) :) :)"],
            Bye = "byeee!! *hugs*", Brb = "my mom needs the phone :( bye!!",
        },
        new()
        {
            Name = "Tamagotchi4Life", Opener = "brb, feeding my tamagotchi... ok back. hi!", Profile = "Location: wherever my pet is\nHobbies: virtual pet care\nPets: 3 (1 alive)",
            Hello = ["hi!! *beep beep*", "hello!", "hey"], Status = ["stressed. it pooped again", "good, mine evolved!!", "sad, it ran away :("],
            Answers = ["i think so?", "only if it's fed", "no", "ask me after i clean up after it"],
            Chatter = ["it beeped in class and the teacher took it", "you have to feed them EVERY hour", "mine is so cute", "do u have one", "i named mine Pixel", "brb it's hungry. ok back", "it's sleeping now so i have to be quiet lol"],
            Bye = "bye! *beep*", Brb = "OH NO it's beeping. gotta go!!",
        },
        new()
        {
            Name = "ModemMaster", Opener = "hey, are you on 28.8 or 33.6?", Profile = "Location: in front of the computer\nHobbies: BBSes, modems, upgrading things\nComputer: 486 DX4/100, 16 MB RAM (!)",
            Hello = ["greetings", "hey", "hi, connected at 33600 bps btw"], Status = ["good, downloading a 2 MB file. eta 20 min", "great, just upgraded my RAM", "annoyed, someone picked up the phone mid-download"],
            Answers = ["read the manual lol", "depends on your IRQ", "yes, if you have enough RAM", "try ATZ", "check your CONFIG.SYS"],
            Chatter = ["i'm running a BBS on the weekends", "real programmers use QBasic", "i overclocked my 486", "have u tried the World Wide Web yet", "my hard drive is 540 MB. never filling that", "ATDT ATDT ATDT", "the trick is a dedicated phone line", "i defragged twice today"],
            Bye = "+++ATH0", Brb = "someone picked up the extension... NO CARRIER",
        },
        new()
        {
            Name = "Grandma1932", Opener = "HELLO DEAR IS THIS THE INSTANT MESSAGE", Profile = "Location: Florida\nHobbies: bridge, knitting, forwarding emails\nQuote: \"Wear a sweater.\"",
            Hello = ["Hello sweetie!", "HELLO! Oops the caps thing is on", "Hello dear, it's Grandma."], Status = ["I'm fine dear, my knee is acting up.", "Wonderful, I learned to make a smiley :-)", "Busy, the bridge club is here."],
            Answers = ["Ask your mother dear.", "I don't know dear, I'm new to the computer.", "Yes, and wear a sweater.", "Did you get my email about the virus?"],
            Chatter = ["Are you eating enough?", "Your mother says hello.", "Did you get the card I sent?", "Please forward my email to everyone you know.", "How do I make the letters smaller", "I'm so proud of you.", "This computer is very clever.", "Love, Grandma"],
            Bye = "Goodbye dear, love you! Wear a sweater!", Brb = "Grandpa wants the phone line back. Love you!",
        },
        new()
        {
            Name = "NetSurferDude", Opener = "dude!! did you see the new site with the dancing hamsters", Profile = "Location: cyberspace\nHobbies: surfing the net (not the ocean)\nHomepage: under construction",
            Hello = ["dude!", "hey dude", "whoa, hey"], Status = ["surfing the information superhighway", "my page has 47 hits now", "waiting for a picture to load. 6 min so far"],
            Answers = ["totally dude", "search for it on a search engine", "there's a webring for that", "no clue dude"],
            Chatter = ["i added a spinning email gif to my page", "my homepage has MIDI music now", "sign my guestbook!!", "under construction gifs are the best", "cyberspace is so cool", "i'm in 4 webrings", "the info superhighway is so fast", "radical"],
            Bye = "surf's up! later dude", Brb = "dude my sister needs the phone, later",
        },
        new()
        {
            Name = "PogSlammer", Opener = "wanna trade pogs?", Profile = "Location: the playground\nHobbies: pogs, slammers, more pogs\nCollection: 412 pogs",
            Hello = ["hi! wanna trade", "hey", "slam!"], Status = ["good, won 6 pogs today", "bad, lost my best slammer", "ok"],
            Answers = ["only for a holographic one", "maybe", "no way", "for 3 of yours"],
            Chatter = ["i have a metal slammer", "pogs are still cool right", "i got a holographic one", "my school banned pogs", "playing for keeps is risky", "trade u 2 for 1"],
            Bye = "bye! keep slammin", Brb = "recess is over lol jk dinner. bye",
        },
    ];

    public static List<AolMail> NewMail(string me) =>
    [
        new("AvamuxHost", "Welcome to Avamux On-Line!",
            $"Dear {me},\n\nWelcome to Avamux On-Line, the friendliest place in cyberspace!\n\nHere are some things to try:\n\n" +
            "  * Click CHAT to meet people in the Town Square.\n  * Your BUDDY LIST shows when your friends sign on.\n" +
            "  * Use KEYWORD (Ctrl+K) to go anywhere: try NEWS, CHAT or GAMES.\n\nYour first 50 hours are FREE. After that, $2.95 an hour.\n\nSee you on-line!\n\nThe Avamux On-Line Team"),
        new("Grandma1932", "FW: FW: FW: FW: FW: VIRUS WARNING!!! READ THIS",
            "Dear,\n\n>>>> IF YOU GET AN EMAIL CALLED \"GOOD TIMES\" DO NOT OPEN IT!!!\n>>>> IT WILL ERASE YOUR HARD DRIVE AND MAKE YOUR\n>>>> MONITOR EXPLODE. THIS IS NOT A HOAX. MY FRIEND'S\n>>>> COUSIN WORKS AT A COMPUTER COMPANY.\n>>>> FORWARD THIS TO EVERYONE YOU KNOW!!!\n\nLove, Grandma\n\nP.S. How do I turn off the capitals"),
        new("xXSk8rXx", "u going to the mall saturday??", "sup\n\nme and the guys are going to the mall saturday. food court then the arcade. my mom can drive.\n\nu in? IM me\n\nlater"),
        new("FreeTrialCDs", "Your FREE trial disk is on its way (again)!",
            "Congratulations!\n\nOur records show that you have not yet received a FREE Avamux On-Line trial disk this week.\n\n" +
            "A new disk is on its way to you now, along with 4 more for your family, friends and coasters.\n\nYour new disks offer 50, 100, 250 and 1,000 FREE hours.\n\nThank you for choosing Avamux On-Line!"),
    ];

    /// <summary>What happens to a letter to someone who isn't on the buddy list.</summary>
    public static AolMail Bounce(string to, string subject) => new("MAILER-DAEMON", "Returned mail: User unknown",
        $"The original message was received at {DateTime.Now:h:mm tt} from your computer.\n\n   ----- The following addresses had permanent fatal errors -----\n<{to}>\n\n" +
        $"   ----- Transcript of session follows -----\n550 <{to}>... User unknown (or is busy using the phone)\n\nSubject: {subject}");
}
