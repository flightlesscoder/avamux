using System.Text.RegularExpressions;

namespace Avamux.Core.GitActivity;

/// <summary>What a commit says about AI help, and who the human behind it is.</summary>
/// <param name="Tools">AI tools that helped (Claude Code, GitHub Copilot, Cursor…).</param>
/// <param name="Models">Models named by those tools (Claude Opus 4.5, GPT-5…).</param>
/// <param name="AgentAuthored">True when the commit's author is an AI agent rather than a person.</param>
/// <param name="HumanName">The person credited: the author, or for an agent's commit the person it worked with.</param>
/// <param name="Bot">True when the author is an automation account (dependabot, a build service) and not an AI.</param>
public sealed record AiFinding(string[] Tools, string[] Models, bool AgentAuthored, string HumanName, string HumanEmail, bool Bot);

/// <summary>
/// Recognises AI help in commits from the marks tools leave: Co-authored-by trailers, "Generated with" lines, agent
/// accounts and author suffixes. Model names are only taken from those marks, never from the rest of the message,
/// so "upgrade the gpt-4 client" is not mistaken for AI help.
/// </summary>
public sealed class AiDetector
{
    private sealed record Rule(string Tool, Regex Pattern, string Field, bool Agent);

    private static readonly (string Tool, string Pattern, string Field, bool Agent)[] BuiltIn =
    [
        ("Claude Code", @"generated with \[?claude code|claude\.(com|ai)/code|🤖 generated with claude", "message", false),
        ("Claude Code", @"noreply@anthropic\.com|\bclaude\b.*<.*anthropic", "coauthor", false),
        ("Claude Code", @"^claude(\[bot\])?\s*<|claude\[bot\]", "author", true),
        ("GitHub Copilot", @"copilot-swe-agent|\+copilot@users\.noreply\.github\.com|^copilot\s*<|github-copilot\[bot\]|copilot\[bot\]", "author", true),
        ("GitHub Copilot", @"\bcopilot\b", "coauthor", false),
        ("GitHub Copilot", @"generated (by|with) (github )?copilot", "message", false),
        ("Cursor", @"cursoragent@cursor\.com|^cursor( agent)?\s*<|cursor\[bot\]", "author", true),
        ("Cursor", @"\bcursor\b", "coauthor", false),
        ("Cursor", @"generated (by|with) cursor", "message", false),
        ("OpenAI Codex", @"chatgpt-codex-connector|codex\[bot\]|^codex\s*<|openai-codex", "author", true),
        ("OpenAI Codex", @"\bcodex\b|noreply@openai\.com|chatgpt", "coauthor", false),
        ("OpenAI Codex", @"generated (by|with) (openai )?codex|codex-generated", "message", false),
        ("Aider", @"\(aider\)\s*<|noreply@aider\.chat", "author", false),
        ("Aider", @"\baider\b", "coauthor", false),
        ("Aider", @"^aider: ", "subject", false),
        ("Devin", @"devin-ai-integration|devin\[bot\]|@cognition(-labs)?\.ai", "author", true),
        ("Devin", @"\bdevin\b", "coauthor", false),
        ("Gemini", @"gemini-code-assist\[bot\]|gemini-cli|google-labs-jules\[bot\]|jules\[bot\]", "author", true),
        ("Gemini", @"\bgemini\b|\bjules\b", "coauthor", false),
        ("Gemini", @"generated (by|with) gemini", "message", false),
        ("Amazon Q", @"amazon-q-developer\[bot\]|amazonq", "author", true),
        ("Amazon Q", @"amazon q|codewhisperer", "coauthor", false),
        ("Windsurf", @"windsurf|codeium", "coauthor", false),
        ("Windsurf", @"generated (by|with) (windsurf|codeium)", "message", false),
        ("Cline", @"\bcline\b|roo code|roo-code", "coauthor", false),
        ("Sweep", @"sweep-ai\[bot\]", "author", true),
        ("Tabnine", @"tabnine", "coauthor", false),
        ("JetBrains AI", @"junie|jetbrains ai", "coauthor", false),
        ("OpenHands", @"openhands|all-hands-ai", "author", true),
        ("AI (other)", @"^(assisted-by|ai-assisted|generated-by|ai-generated|co-developed-by|ai-model|ai-tool):\s*\S", "trailer", false),
    ];

    /// <summary>Names of models, normalised to how people write them.</summary>
    private static readonly (Regex Pattern, Func<Match, string> Name)[] ModelPatterns =
    [
        (new(@"\bclaude[\s-]*(\d(?:\.\d)?)?[\s-]*(opus|sonnet|haiku|fable|instant)[\s-]*(\d(?:[.\-]\d)?)?", RegexOptions.IgnoreCase),
            m => $"Claude {Cap(m.Groups[2].Value)} {(m.Groups[3].Value.Length > 0 ? m.Groups[3].Value : m.Groups[1].Value).Replace('-', '.')}".Trim()),
        (new(@"\b(opus|sonnet|haiku|fable)[\s-]+(\d(?:\.\d)?)\b", RegexOptions.IgnoreCase), m => $"Claude {Cap(m.Groups[1].Value)} {m.Groups[2].Value}"),
        (new(@"\bgpt[\s-]?(\d(?:\.\d)?)(o)?(?:[\s-](mini|nano|turbo|pro|codex))?", RegexOptions.IgnoreCase),
            m => $"GPT-{m.Groups[1].Value}{m.Groups[2].Value.ToLowerInvariant()}{(m.Groups[3].Success ? "-" + m.Groups[3].Value.ToLowerInvariant() : "")}"),
        (new(@"\b(o[1345])(?:[\s-](mini|pro))?\b", RegexOptions.IgnoreCase), m => $"{m.Groups[1].Value.ToLowerInvariant()}{(m.Groups[2].Success ? "-" + m.Groups[2].Value.ToLowerInvariant() : "")}"),
        (new(@"\bgemini[\s-]*(\d(?:\.\d)?)?[\s-]*(pro|flash|ultra|nano)?\b", RegexOptions.IgnoreCase),
            m => $"Gemini {m.Groups[1].Value} {Cap(m.Groups[2].Value)}".Replace("  ", " ").Trim()),
        (new(@"\b(llama|codellama|mistral|mixtral|codestral|devstral|deepseek|qwen|grok|phi|kimi|glm)[\s-]?([\w.]*\d[\w.]*)?", RegexOptions.IgnoreCase),
            m => $"{Cap(m.Groups[1].Value)}{(m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? " " + m.Groups[2].Value : "")}"),
    ];

    private static readonly Regex Bots = new(
        @"\[bot\]|^dependabot|^renovate|^github-actions|^greenkeeper|^snyk|^mergify|^semantic-release|^azure pipelines|project collection build service|build service \(|^jenkins|^gitlab-ci|^buildkite|^circleci|^allcontributors|^imgbot|^pre-commit-ci|^codecov|^sonarcloud|^whitesource|^mend|^deepsource|^web-flow|^svc[-_]|^svc\.|^azure devops|^tfs service|^vsts",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CoAuthor = new(@"^co-authored-by:\s*(.+?)\s*<([^>]*)>\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Trailer = new(@"^[A-Za-z][\w-]*:\s*.+$", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly List<Rule> _rules;

    public AiDetector(IEnumerable<AiRule>? extra = null)
    {
        _rules = BuiltIn.Select(r => new Rule(r.Tool, new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant), r.Field, r.Agent)).ToList();
        foreach (var r in extra ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Tool) || string.IsNullOrWhiteSpace(r.Pattern)) continue;
            try { _rules.Insert(0, new Rule(r.Tool.Trim(), new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline), r.Field.Trim().ToLowerInvariant(), r.Agent)); }
            catch (ArgumentException ex) { Logging.Log.Warn("activity", $"Ignoring the AI rule for {r.Tool}: {ex.Message}"); }
        }
    }

    public static bool IsBot(string name, string email) => Bots.IsMatch(name) || Bots.IsMatch(email) && !email.Contains("copilot", StringComparison.OrdinalIgnoreCase);

    /// <summary>Looks at one commit. <paramref name="body"/> is the message after the subject line.</summary>
    public AiFinding Detect(string authorName, string authorEmail, string committerName, string committerEmail, string subject, string body)
    {
        var tools = new List<string>();
        var marks = new List<string>();   // the text that showed AI help: models are looked for only here
        var author = $"{authorName} <{authorEmail}>";
        var coAuthors = CoAuthor.Matches(body).Select(m => (Name: m.Groups[1].Value.Trim(), Email: m.Groups[2].Value.Trim())).ToList();
        var agent = false;
        var aiCoAuthors = new HashSet<int>();

        foreach (var rule in _rules)
        {
            switch (rule.Field)
            {
                case "author":
                    if (rule.Pattern.IsMatch(author)) { Credit(rule.Tool, author); agent |= rule.Agent; }
                    break;
                case "committer":
                    if (rule.Pattern.IsMatch($"{committerName} <{committerEmail}>")) Credit(rule.Tool, committerName);
                    break;
                case "coauthor":
                    for (var i = 0; i < coAuthors.Count; i++)
                    {
                        var line = $"{coAuthors[i].Name} <{coAuthors[i].Email}>";
                        if (rule.Pattern.IsMatch(line)) { Credit(rule.Tool, line); aiCoAuthors.Add(i); }
                    }
                    break;
                case "subject":
                    if (rule.Pattern.IsMatch(subject)) Credit(rule.Tool, subject);
                    break;
                case "trailer":
                    foreach (Match t in Trailer.Matches(body)) if (rule.Pattern.IsMatch(t.Value)) Credit(rule.Tool, t.Value);
                    break;
                case "message":
                    foreach (var line in (subject + "\n" + body).Split('\n')) if (rule.Pattern.IsMatch(line)) Credit(rule.Tool, line.Trim());
                    break;
                default:
                    if (rule.Pattern.IsMatch(author) || rule.Pattern.IsMatch(subject) || rule.Pattern.IsMatch(body)) { Credit(rule.Tool, ""); agent |= rule.Agent; }
                    break;
            }
        }
        // A co-author that names a model is AI help even when no tool is recognised.
        for (var i = 0; i < coAuthors.Count; i++)
        {
            if (aiCoAuthors.Contains(i)) continue;
            var line = $"{coAuthors[i].Name} <{coAuthors[i].Email}>";
            if (Models(line).Count > 0) { Credit("AI (other)", line); aiCoAuthors.Add(i); }
        }
        // "AI (other)" says less than a named tool: drop it when there is one.
        if (tools.Count > 1) tools.Remove("AI (other)");

        var models = marks.SelectMany(Models).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // The person: an agent's commit belongs to the human co-author, else to whoever committed it (when that is
        // a person and not the hosting service).
        string humanName = authorName, humanEmail = authorEmail;
        if (agent)
        {
            var human = coAuthors.Where((c, i) => !aiCoAuthors.Contains(i) && !IsBot(c.Name, c.Email)).Select(c => ((string, string)?)c).FirstOrDefault();
            if (human is { } h) (humanName, humanEmail) = h;
            else if (!IsService(committerName, committerEmail) && !IsBot(committerName, committerEmail) && !_rules.Any(r => r.Field == "author" && r.Pattern.IsMatch($"{committerName} <{committerEmail}>")))
                (humanName, humanEmail) = (committerName, committerEmail);
        }
        // Aider adds " (aider)" to the author's own name.
        if (humanName.EndsWith(" (aider)", StringComparison.OrdinalIgnoreCase)) humanName = humanName[..^8];

        var bot = !agent && tools.Count == 0 && IsBot(authorName, authorEmail);
        return new AiFinding(tools.Distinct().ToArray(), models, agent, humanName, humanEmail, bot);

        void Credit(string tool, string mark)
        {
            if (!tools.Contains(tool)) tools.Add(tool);
            if (mark.Length > 0) marks.Add(mark);
        }
    }

    private static bool IsService(string name, string email) =>
        email.Equals("noreply@github.com", StringComparison.OrdinalIgnoreCase) || name.Equals("GitHub", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GitHub Enterprise", StringComparison.OrdinalIgnoreCase) || email.EndsWith("@users.noreply.github.com", StringComparison.OrdinalIgnoreCase) && name.Contains("[bot]");

    /// <summary>The model names in a piece of text.</summary>
    public static List<string> Models(string text)
    {
        var found = new List<string>();
        foreach (var (pattern, name) in ModelPatterns)
            foreach (Match m in pattern.Matches(text))
            {
                // "Claude Opus" with no version still counts; a lone "o1" inside a word does not (the \b handles that).
                var n = name(m).Trim();
                if (n.Length > 2 && !found.Contains(n, StringComparer.OrdinalIgnoreCase)) found.Add(n);
            }
        // "Claude Opus 4.5" also matches the shorter "opus 4.5" pattern: keep the longest form.
        return found.Where(f => !found.Any(o => o.Length > f.Length && o.EndsWith(f[f.IndexOf(' ')..], StringComparison.OrdinalIgnoreCase) && f.StartsWith("Claude"))).ToList();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}

/// <summary>Who a contributor is: identities merged by alias, by name, and GitHub's no-reply addresses.</summary>
public sealed class Identities
{
    private readonly Dictionary<string, string> _alias = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _byName;

    public Identities(GitActivityConfig config)
    {
        _byName = config.MergeByName;
        foreach (var (person, names) in config.Aliases)
        {
            _alias[person] = person;
            foreach (var n in names) if (n.Trim().Length > 0) _alias[n.Trim()] = person;
        }
    }

    /// <summary>The key a person is counted under (also their shown name when merged by name or alias).</summary>
    public string Key(string name, string email)
    {
        if (_alias.TryGetValue(email, out var a) || _alias.TryGetValue(name, out a) || _alias.TryGetValue(NormalEmail(email), out a)) return a;
        if (_byName && name.Trim().Length > 0) return name.Trim();
        return NormalEmail(email) is { Length: > 0 } e ? e : name.Trim();
    }

    /// <summary>12345+login@users.noreply.github.com and login@users.noreply.github.com are both "login@github".</summary>
    public static string NormalEmail(string email)
    {
        email = email.Trim().ToLowerInvariant();
        const string gh = "@users.noreply.github.com";
        if (!email.EndsWith(gh, StringComparison.Ordinal)) return email;
        var user = email[..^gh.Length];
        var plus = user.IndexOf('+');
        return (plus >= 0 ? user[(plus + 1)..] : user) + "@github";
    }
}
