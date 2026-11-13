namespace Avamux.Core.Nav;

/// <summary>A page the user added to the navigation: a terminal running one command, a web page, or a plugin.</summary>
public sealed class CustomPage
{
    public const string Terminal = "terminal", Web = "web", Plugin = "plugin";
    public static readonly string[] Kinds = [Terminal, Web, Plugin];

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>An icon name (the list is in Settings → Custom pages) or SVG path data drawn in a 24x24 box. Empty picks one by kind.</summary>
    public string Icon { get; set; } = "";
    /// <summary>terminal, web or plugin.</summary>
    public string Kind { get; set; } = Terminal;
    public bool Enabled { get; set; } = true;

    /// <summary>Terminal: the command line, run through the shell. Empty starts the shell itself.</summary>
    public string Command { get; set; } = "";
    /// <summary>Terminal: folder to start in. Empty is the home folder.</summary>
    public string Cwd { get; set; } = "";

    /// <summary>Web: the address to show.</summary>
    public string Url { get; set; } = "";
    /// <summary>Web: give the page the limited window.avamux object.</summary>
    public bool InjectApi { get; set; } = true;
    /// <summary>Web: JavaScript run in the page after each load (it can use window.avamux).</summary>
    public string Script { get; set; } = "";
    /// <summary>Web: send links that ask for a new window to the default browser instead of following them in place.</summary>
    public bool LinksInBrowser { get; set; }

    /// <summary>Plugin: path of the DLL. A relative path is looked up in the plugins folder.</summary>
    public string Assembly { get; set; } = "";
    /// <summary>Plugin: class to use when the DLL has more than one page. Empty takes the first.</summary>
    public string Type { get; set; } = "";

    /// <summary>The key the window knows this page by.</summary>
    public string Key => KeyPrefix + Id;
    public const string KeyPrefix = "custom:";

    /// <summary>Everything that decides what the page runs. When this changes the page is started afresh.</summary>
    public string Signature => Kind switch
    {
        Terminal => $"terminal|{Command}|{Cwd}",
        Web => $"web|{Url}|{InjectApi}|{Script}|{LinksInBrowser}",
        _ => $"plugin|{Assembly}|{Type}",
    };

    /// <summary>What the page points at, for lists.</summary>
    public string Target => Kind switch { Terminal => Command.Length > 0 ? Command : "(shell)", Web => Url, _ => Type.Length > 0 ? $"{Assembly} ({Type})" : Assembly };

    public CustomPage Clone() => (CustomPage)MemberwiseClone();
}

public sealed class NavState
{
    public List<CustomPage> Pages { get; set; } = [];
    public int NextId { get; set; } = 1;
}

/// <summary>
/// The pages the user added to the navigation, kept in nav.json in the data folder, and the marks shown on them
/// (a page asking for attention, a badge). Changes take effect at once: the window listens to <see cref="Changed"/>.
/// </summary>
public sealed class NavService
{
    private readonly string _file;
    private readonly object _gate = new();
    private readonly NavState _state;
    private readonly Dictionary<string, string> _attention = new();
    private readonly Dictionary<string, string> _badges = new();
    private readonly Dictionary<string, DateTime> _lastNotice = new();

    public NavService(AppPaths paths)
    {
        _file = paths.StateFile("nav");
        _state = JsonUtil.LoadFile<NavState>(_file);
    }

    /// <summary>Raised when pages are added, changed, removed or reordered.</summary>
    public event Action? Changed;
    /// <summary>Raised when a page's attention mark or badge changes.</summary>
    public event Action? MarksChanged;
    /// <summary>Raised to start a page afresh (restart its terminal, reload its web page or plugin).</summary>
    public event Action<CustomPage>? ReloadRequested;
    /// <summary>Raised when a page newly asks for attention, with its message (may be empty).</summary>
    public event Action<CustomPage, string>? AttentionRequested;

    public IReadOnlyList<CustomPage> Pages { get { lock (_gate) return _state.Pages.Select(p => p.Clone()).ToList(); } }

    public CustomPage? Find(string idOrName)
    {
        var id = idOrName.StartsWith(CustomPage.KeyPrefix, StringComparison.Ordinal) ? idOrName[CustomPage.KeyPrefix.Length..] : idOrName;
        lock (_gate)
            return (_state.Pages.FirstOrDefault(p => p.Id == id)
                ?? _state.Pages.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase)))?.Clone();
    }

    public CustomPage Get(string idOrName) => Find(idOrName) ?? throw new KeyNotFoundException($"No custom page '{idOrName}'.");

    /// <summary>Checks a page and tidies its values. Throws with a message fit to show the user.</summary>
    public static void Validate(CustomPage page)
    {
        page.Name = page.Name.Trim();
        page.Kind = page.Kind.Trim().ToLowerInvariant();
        page.Icon = page.Icon.Trim();
        page.Command = page.Command.Trim(); page.Cwd = page.Cwd.Trim(); page.Url = page.Url.Trim();
        page.Assembly = page.Assembly.Trim(); page.Type = page.Type.Trim();
        if (page.Name.Length == 0) throw new ArgumentException("The page needs a name.");
        if (!CustomPage.Kinds.Contains(page.Kind)) throw new ArgumentException($"Unknown kind '{page.Kind}'. Kinds: {string.Join(", ", CustomPage.Kinds)}.");
        switch (page.Kind)
        {
            case CustomPage.Web:
                if (page.Url.Length == 0) throw new ArgumentException("A web page needs an address.");
                if (!page.Url.Contains("://", StringComparison.Ordinal)) page.Url = "https://" + page.Url;
                if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
                    throw new ArgumentException($"'{page.Url}' is not an address avamux can show (http, https or file).");
                break;
            case CustomPage.Plugin:
                if (page.Assembly.Length == 0) throw new ArgumentException("A plugin page needs the path of its DLL.");
                break;
        }
    }

    public CustomPage Add(CustomPage page)
    {
        Validate(page);
        lock (_gate)
        {
            if (_state.Pages.Any(p => string.Equals(p.Name, page.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There is already a custom page called '{page.Name}'.");
            page = page.Clone();
            page.Id = (_state.NextId++).ToString();
            _state.Pages.Add(page);
            Save();
        }
        Changed?.Invoke();
        return page.Clone();
    }

    public void Update(CustomPage page)
    {
        Validate(page);
        lock (_gate)
        {
            var i = _state.Pages.FindIndex(p => p.Id == page.Id);
            if (i < 0) throw new KeyNotFoundException($"No custom page '{page.Id}'.");
            if (_state.Pages.Any(p => p.Id != page.Id && string.Equals(p.Name, page.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There is already a custom page called '{page.Name}'.");
            _state.Pages[i] = page.Clone();
            Save();
        }
        Changed?.Invoke();
    }

    public void SetEnabled(string idOrName, bool enabled)
    {
        var page = Get(idOrName);
        if (page.Enabled == enabled) return;
        page.Enabled = enabled;
        Update(page);
    }

    public void Remove(string idOrName)
    {
        var page = Get(idOrName);
        lock (_gate)
        {
            _state.Pages.RemoveAll(p => p.Id == page.Id);
            _attention.Remove(page.Id); _badges.Remove(page.Id);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Moves a page up or down the list by <paramref name="delta"/> places.</summary>
    public void Move(string idOrName, int delta)
    {
        var page = Get(idOrName);
        lock (_gate)
        {
            var i = _state.Pages.FindIndex(p => p.Id == page.Id);
            var to = Math.Clamp(i + delta, 0, _state.Pages.Count - 1);
            if (to == i) return;
            var item = _state.Pages[i];
            _state.Pages.RemoveAt(i);
            _state.Pages.Insert(to, item);
            Save();
        }
        Changed?.Invoke();
    }

    public void Reload(string idOrName) => ReloadRequested?.Invoke(Get(idOrName));

    private void Save() => JsonUtil.SaveFile(_file, _state);

    /// <summary>The message a page asked for attention with (empty when it gave none), or null when it is not asking.</summary>
    public string? Attention(string id) { lock (_gate) return _attention.GetValueOrDefault(id); }

    public string? Badge(string id) { lock (_gate) return _badges.GetValueOrDefault(id); }

    /// <summary>
    /// Marks a page as wanting the user. Asking again while already marked changes nothing, and a page that keeps
    /// asking and being looked at is announced at most once every few seconds, so a page cannot flood the user.
    /// </summary>
    public void RequestAttention(string id, string? message)
    {
        if (Find(id) is not { Enabled: true } page) return;
        var text = Clip(message, 200);
        lock (_gate)
        {
            if (_attention.ContainsKey(page.Id)) return;
            _attention[page.Id] = text;
            var now = DateTime.UtcNow;
            var quiet = _lastNotice.TryGetValue(page.Id, out var last) && now - last < TimeSpan.FromSeconds(5);
            _lastNotice[page.Id] = now;
            if (quiet) page = null;
        }
        MarksChanged?.Invoke();
        if (page is not null) AttentionRequested?.Invoke(page, text);
    }

    public void ClearAttention(string id)
    {
        id = Find(id)?.Id ?? id;
        lock (_gate) if (!_attention.Remove(id)) return;
        MarksChanged?.Invoke();
    }

    /// <summary>A few characters shown on the page's navigation entry (an unread count, say). Empty removes it.</summary>
    public void SetBadge(string id, string? text)
    {
        if (Find(id) is not { } page) return;
        var badge = Clip(text, 4);
        lock (_gate)
        {
            if ((_badges.GetValueOrDefault(page.Id) ?? "") == badge) return;
            if (badge.Length == 0) _badges.Remove(page.Id); else _badges[page.Id] = badge;
        }
        MarksChanged?.Invoke();
    }

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
