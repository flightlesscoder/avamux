using System.Reflection;
using System.Text.Json.Nodes;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Logging;
using Avamux.Core.Nav;
using Avamux.Plugins;

namespace Avamux.Core.Plugins;

public enum PluginSource { BuiltIn, Folder, Page }

/// <summary>A plugin as Settings → Plugins and <c>avamux plugin list</c> show it.</summary>
public sealed class PluginEntry
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public PluginSource Source { get; init; }
    /// <summary>The DLL (or for a folder plugin, the folder) it comes from; empty for built-in plugins.</summary>
    public string Location { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Why it could not be loaded or switched on, if it could not.</summary>
    public string? Error { get; set; }
    /// <summary>What it adds, in words: "menu items", "watch kinds: http-status", "a page in the navigation" …</summary>
    public List<string> Adds { get; } = [];
    public bool CanUninstall => Source != PluginSource.BuiltIn;
    public bool CanReload => Source != PluginSource.BuiltIn;
    /// <summary>Its code stays loaded until avamux restarts (.NET cannot unload it), though it is switched off or deleted.</summary>
    public bool RestartToFinish { get; set; }
    /// <summary>Panels of its own settings (Avalonia controls, made on the UI thread).</summary>
    public IReadOnlyList<Func<object>> SettingsViews => Context?.SettingsViews ?? [];

    internal Func<IAvamuxPlugin>? Factory { get; set; }
    internal IAvamuxPlugin? Instance { get; set; }
    internal PluginContext? Context { get; set; }
    internal List<IWatchProvider> WatchOnly { get; } = [];
    internal string? PageId { get; init; }
    internal bool WatchesRegistered { get; set; }
}

/// <summary>What plugins need from the window: theme, dialogs, windows. The default does nothing (tests, headless).</summary>
public interface IPluginUi
{
    string ThemeColor(string token);
    event Action? ThemeChanged;
    void Post(Action action);
    void Toast(string title, string message);
    Task<string?> PromptAsync(string title, string label, string initial);
    Task<bool> ConfirmAsync(string title, string message, string action);
    IPluginWindow ShowWindow(string title, object content, double width, double height);
}

public sealed class NullPluginUi : IPluginUi
{
    public string ThemeColor(string token) => "#888888";
    public event Action? ThemeChanged { add { } remove { } }
    public void Post(Action action) => action();
    public void Toast(string title, string message) { }
    public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);
    public Task<bool> ConfirmAsync(string title, string message, string action) => Task.FromResult(false);
    public IPluginWindow ShowWindow(string title, object content, double width, double height) => throw new NotSupportedException("There is no window to show it in.");
}

/// <summary>
/// Every plugin avamux knows: built-in ones (AI Actions), plugins in the plugins folder (each in a folder of its own,
/// or a single DLL; <see cref="IAvamuxPlugin"/>s and watch kinds), and page plugins added under Custom pages. Keeps
/// which are switched off in plugins.json, switches them on and off without a restart, installs and uninstalls them.
/// </summary>
public sealed class PluginService
{
    private readonly AppServices _app;
    private readonly string _stateFile;
    private readonly object _gate = new();
    private readonly List<PluginEntry> _entries = [];
    private readonly List<Func<IAvamuxPlugin>> _builtIn = [];
    private PluginState _state;
    private bool _started;

    public PluginService(AppServices app)
    {
        _app = app;
        _stateFile = app.Paths.StateFile("plugins");
        _state = JsonUtil.LoadFile<PluginState>(_stateFile);
        app.Nav.Changed += () => { if (_started) { SyncPages(); Changed?.Invoke(); } };
    }

    public IPluginUi Ui { get; set; } = new NullPluginUi();

    /// <summary>Raised when plugins are added, removed, switched on or off, or their menus or providers change.</summary>
    public event Action? Changed;

    public IReadOnlyList<PluginEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    public PluginEntry? Find(string idOrName)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.Id == idOrName) ?? _entries.FirstOrDefault(e => string.Equals(e.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public PluginEntry Get(string idOrName) => Find(idOrName) ?? throw new KeyNotFoundException($"No plugin '{idOrName}'. See `avamux plugin list`.");

    /// <summary>A plugin that comes with avamux (registered before <see cref="Start"/>).</summary>
    public void AddBuiltIn(Func<IAvamuxPlugin> factory) { lock (_gate) _builtIn.Add(factory); }

    /// <summary>Finds every plugin and switches on those not switched off.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var factory in _builtIn)
        {
            try
            {
                var probe = factory();
                Add(new PluginEntry { Id = probe.Id, Name = probe.Name, Description = probe.Description, Version = VersionOf(probe.GetType().Assembly), Source = PluginSource.BuiltIn, Factory = factory, Instance = probe });
            }
            catch (Exception ex) { Log.Error("plugin", "A built-in plugin could not be created", ex); }
        }
        ScanFolder();
        SyncPages();
        foreach (var e in Entries.Where(e => e.Enabled)) Activate(e);
        Diagnostics.Timeline.Note("watch plugins loaded", Entries.Count(e => e.WatchOnly.Count > 0).ToString());
        Diagnostics.Timeline.Note("plugins loaded", Entries.Count.ToString());
        Changed?.Invoke();
    }

    private void Add(PluginEntry entry)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Id == entry.Id)) { Log.Warn("plugin", $"Two plugins have the id '{entry.Id}'; only the first is used ({entry.Location})."); return; }
            entry.Enabled = !_state.Disabled.Contains(entry.Id);
            _entries.Add(entry);
        }
    }

    private static string VersionOf(Assembly a) => a.GetName().Version?.ToString(3) ?? "";

    // ---- the plugins folder ----

    private void ScanFolder()
    {
        var dir = _app.Paths.PluginsDir;
        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll")) LoadDll(dll, dll);
        foreach (var sub in Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')))
            foreach (var dll in Candidates(sub)) LoadDll(dll, sub);
    }

    /// <summary>
    /// The DLLs of a plugin folder that may hold plugins: the one named in plugin.json ({"assembly": "x.dll"}), else
    /// the one named after the folder, else every DLL in it (and below, as watch plugins were found before) that
    /// avamux does not have itself.
    /// </summary>
    private static IEnumerable<string> Candidates(string folder)
    {
        var manifest = Path.Combine(folder, "plugin.json");
        if (File.Exists(manifest))
        {
            try
            {
                if (JsonUtil.Parse(File.ReadAllText(manifest))?["assembly"]?.GetValue<string>() is { Length: > 0 } name) return [Path.Combine(folder, name)];
            }
            catch (Exception ex) { Log.Warn("plugin", $"{manifest}: {ex.Message}"); }
        }
        var named = Path.Combine(folder, Path.GetFileName(folder) + ".dll");
        if (File.Exists(named)) return [named];
        return Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Where(f => !ProvidedByAvamux(Path.GetFileNameWithoutExtension(f))).ToList();
    }

    private static bool ProvidedByAvamux(string assemblyName) =>
        System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == assemblyName)
        || File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));

    /// <summary>Loads one DLL and adds an entry for each plugin in it (and one for its watch kinds, if any stand alone).</summary>
    private List<PluginEntry> LoadDll(string dll, string location, bool quiet = true)
    {
        var added = new List<PluginEntry>();
        Assembly assembly;
        Type[] types;
        try { assembly = PluginLoader.Load(dll); types = PluginLoader.Types(assembly); }
        catch (Exception ex)
        {
            if (!quiet) throw;
            Log.Warn("plugin", $"{dll} not loaded: {ex.Message}");
            return added;
        }
        foreach (var type in PluginLoader.Implementing<IAvamuxPlugin>(types))
        {
            var t = type;
            IAvamuxPlugin probe;
            try { probe = (IAvamuxPlugin)Activator.CreateInstance(t)!; }
            catch (Exception ex) { Log.Error("plugin", $"{t.FullName} in {dll} could not be created", ex); if (!quiet) throw; continue; }
            var entry = new PluginEntry
            {
                Id = probe.Id, Name = probe.Name, Description = probe.Description, Version = VersionOf(assembly), Source = PluginSource.Folder,
                Location = location, Factory = () => (IAvamuxPlugin)Activator.CreateInstance(t)!, Instance = probe,
            };
            Add(entry);
            added.Add(entry);
        }
        // Watch kinds on their own (a watch plugin). In a DLL that has an IAvamuxPlugin, the plugin registers its own.
        var watches = added.Count == 0 ? PluginLoader.Implementing<IWatchProvider>(types) : [];
        if (watches.Count > 0)
        {
            var entry = new PluginEntry
            {
                Id = "watch:" + Path.GetFileNameWithoutExtension(dll).ToLowerInvariant(), Name = Path.GetFileNameWithoutExtension(dll), Version = VersionOf(assembly),
                Source = PluginSource.Folder, Location = location,
            };
            foreach (var w in watches)
            {
                try { entry.WatchOnly.Add((IWatchProvider)Activator.CreateInstance(w)!); }
                catch (Exception ex) { Log.Error("plugin", $"{w.FullName} in {dll} could not be created", ex); }
            }
            entry.Description = "Watch kinds: " + string.Join(", ", entry.WatchOnly.Select(p => $"{p.Kind} ({p.Description})"));
            entry.Adds.Add("watch kinds: " + string.Join(", ", entry.WatchOnly.Select(p => p.Kind)));
            Add(entry);
            added.Add(entry);
        }
        if (added.Count > 0) Log.Info("plugin", $"Found {string.Join(", ", added.Select(a => a.Id))} in {dll}");
        return added;
    }

    // ---- page plugins (Custom pages of kind "plugin") ----

    private void SyncPages()
    {
        var pages = _app.Nav.Pages.Where(p => p.Kind == CustomPage.Plugin).ToList();
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Source == PluginSource.Page && pages.All(p => "page:" + p.Id != e.Id));
            foreach (var p in pages)
            {
                var id = "page:" + p.Id;
                var entry = _entries.FirstOrDefault(e => e.Id == id);
                if (entry is null)
                {
                    entry = new PluginEntry { Id = id, Source = PluginSource.Page, PageId = p.Id };
                    entry.Adds.Add("a page in the navigation");
                    _entries.Add(entry);
                }
                entry.Name = p.Name;
                entry.Location = p.Assembly;
                entry.Enabled = p.Enabled;
                entry.Description = $"A page plugin, shown in the navigation as \"{p.Name}\" (Settings → Custom pages)." + (p.Type.Length > 0 ? $" Class {p.Type}." : "");
            }
        }
    }

    // ---- on and off ----

    private void Activate(PluginEntry e)
    {
        e.Error = null;
        if (e.Source == PluginSource.Page) return;
        if (e.WatchOnly.Count > 0)
        {
            foreach (var w in e.WatchOnly) _app.Watches.AddProvider(w);
            e.WatchesRegistered = true;
            return;
        }
        try
        {
            e.Instance ??= e.Factory!();
            e.Context = new PluginContext(_app, this, e);
            e.Instance.Activate(e.Context);
            e.Adds.Clear();
            e.Adds.AddRange(e.Context.Describe());
            Log.Info("plugin", $"Switched on {e.Name} ({e.Id})");
        }
        catch (Exception ex)
        {
            var problem = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            e.Error = problem.Message;
            Log.Error("plugin", $"{e.Name} could not be switched on", problem);
            e.Context?.RemoveAll();
            e.Context = null;
        }
    }

    private void Deactivate(PluginEntry e)
    {
        if (e.WatchesRegistered)
        {
            foreach (var w in e.WatchOnly) _app.Watches.RemoveProvider(w);
            e.WatchesRegistered = false;
        }
        if (e.Instance is null || e.Context is null) return;
        try { e.Instance.Deactivate(); }
        catch (Exception ex) { Log.Warn("plugin", $"{e.Name} failed while switching off: {ex.Message}"); }
        e.Context.RemoveAll();
        e.Context = null;
        e.Instance = null; // a new instance next time it is switched on
        if (e.Source == PluginSource.Folder) e.RestartToFinish = true;
    }

    public void SetEnabled(string idOrName, bool enabled)
    {
        var e = Get(idOrName);
        if (e.Source == PluginSource.Page) { _app.Nav.SetEnabled(e.PageId!, enabled); return; }
        if (e.Enabled == enabled && (enabled ? e.Context is not null || e.WatchesRegistered : true)) return;
        e.Enabled = enabled;
        lock (_gate)
        {
            _state.Disabled.Remove(e.Id);
            if (!enabled) _state.Disabled.Add(e.Id);
            Save();
        }
        if (enabled) Activate(e); else Deactivate(e);
        Changed?.Invoke();
    }

    /// <summary>Loads a folder plugin's DLL again from disk (after a rebuild) and switches it on again.</summary>
    public void Reload(string idOrName)
    {
        var e = Get(idOrName);
        if (e.Source == PluginSource.Page) { _app.Nav.Reload(e.PageId!); return; }
        if (e.Source == PluginSource.BuiltIn) throw new InvalidOperationException($"{e.Name} comes with avamux and cannot be reloaded.");
        var siblings = Entries.Where(x => x.Location == e.Location).ToList();
        foreach (var s in siblings) Deactivate(s);
        lock (_gate) _entries.RemoveAll(x => x.Location == e.Location);
        var dlls = File.Exists(e.Location) ? [e.Location] : Candidates(e.Location).ToList();
        foreach (var dll in dlls) foreach (var added in LoadDll(dll, e.Location, quiet: false)) if (added.Enabled) Activate(added);
        Changed?.Invoke();
    }

    /// <summary>
    /// Copies a plugin (its DLL and what it needs, or a whole folder) into the plugins folder and switches it on.
    /// Returns the plugins it holds.
    /// </summary>
    public List<PluginEntry> Install(string path)
    {
        var full = Path.GetFullPath(CustomPagesPath(path));
        string dll;
        string sourceDir;
        if (Directory.Exists(full))
        {
            sourceDir = full;
            dll = Candidates(full).FirstOrDefault() ?? throw new InvalidOperationException($"{full} holds no plugin DLL.");
        }
        else if (File.Exists(full) && full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { dll = full; sourceDir = Path.GetDirectoryName(full)!; }
        else throw new FileNotFoundException($"No plugin DLL or folder at {full}.");

        // Look inside before copying anything: a DLL without plugins is refused.
        var types = PluginLoader.Types(PluginLoader.Load(dll));
        var plugins = PluginLoader.Implementing<IAvamuxPlugin>(types);
        if (plugins.Count == 0 && PluginLoader.Implementing<IWatchProvider>(types).Count == 0)
            throw new InvalidOperationException($"{Path.GetFileName(dll)} has no class that implements Avamux.Plugins.IAvamuxPlugin or IWatchProvider. (Page plugins are added under Settings → Custom pages.)");
        var id = plugins.Count > 0 ? ((IAvamuxPlugin)Activator.CreateInstance(plugins[0])!).Id : Path.GetFileNameWithoutExtension(dll);
        var folderName = string.Concat(id.Split(Path.GetInvalidFileNameChars())).Replace(':', '-');
        var target = Path.Combine(_app.Paths.PluginsDir, folderName);
        if (Entries.Any(e => e.Source == PluginSource.Folder && (e.Location == target || plugins.Count > 0 && e.Id == id)))
            throw new InvalidOperationException($"A plugin '{id}' is installed already. Uninstall it first, or reload it after copying the new build over it.");
        Directory.CreateDirectory(target);
        if (Directory.Exists(full)) CopyFolder(full, target);
        else
        {
            // The DLL, its symbols and dependency list, and the DLLs beside it that avamux does not have itself.
            foreach (var f in Directory.EnumerateFiles(sourceDir))
            {
                var name = Path.GetFileName(f);
                var stem = Path.GetFileNameWithoutExtension(dll);
                var keep = name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                           || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !ProvidedByAvamux(Path.GetFileNameWithoutExtension(f));
                if (keep) File.Copy(f, Path.Combine(target, name), true);
            }
            File.WriteAllText(Path.Combine(target, "plugin.json"), new JsonObject { ["assembly"] = Path.GetFileName(dll) }.ToJsonString());
        }
        var added = new List<PluginEntry>();
        foreach (var candidate in Candidates(target)) added.AddRange(LoadDll(candidate, target, quiet: false));
        foreach (var e in added) if (e.Enabled) Activate(e);
        Changed?.Invoke();
        return added;
    }

    private static string CustomPagesPath(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    /// <summary>
    /// Switches a plugin off and deletes it from the plugins folder (a page plugin: removes the page). Its code stays
    /// in memory until avamux restarts.
    /// </summary>
    public void Uninstall(string idOrName)
    {
        var e = Get(idOrName);
        switch (e.Source)
        {
            case PluginSource.BuiltIn: throw new InvalidOperationException($"{e.Name} comes with avamux; switch it off instead.");
            case PluginSource.Page: _app.Nav.Remove(e.PageId!); return;
        }
        var siblings = Entries.Where(x => x.Location == e.Location).ToList();
        foreach (var s in siblings) Deactivate(s);
        var root = Path.GetFullPath(_app.Paths.PluginsDir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(e.Location);
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException($"{full} is outside the plugins folder; it was not deleted.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
        else
        {
            foreach (var f in new[] { full, Path.ChangeExtension(full, ".pdb"), Path.ChangeExtension(full, ".deps.json") })
                if (File.Exists(f)) File.Delete(f);
        }
        lock (_gate)
        {
            _entries.RemoveAll(x => x.Location == e.Location);
            foreach (var s in siblings) _state.Disabled.Remove(s.Id);
            Save();
        }
        Log.Info("plugin", $"Uninstalled {e.Name} ({full})");
        Changed?.Invoke();
    }

    /// <summary>Switches every plugin off (avamux is quitting).</summary>
    public void Stop()
    {
        foreach (var e in Entries) if (e.Context is not null || e.WatchesRegistered) Deactivate(e);
    }

    // ---- what plugins add ----

    /// <summary>The items plugins add to a menu, in plugin order, with a separator between plugins.</summary>
    public List<PluginMenuItem> MenuItems(MenuContext context)
    {
        var items = new List<PluginMenuItem>();
        foreach (var e in Entries)
        {
            if (e.Context is not { } c) continue;
            foreach (var contributor in c.MenuContributors)
            {
                List<PluginMenuItem> part;
                try { part = contributor.Items(context)?.ToList() ?? []; }
                catch (Exception ex) { Log.Warn("plugin", $"{e.Name} failed to add menu items: {ex.Message}"); continue; }
                if (part.Count == 0) continue;
                if (items.Count > 0) items.Add(PluginMenuItem.Separator());
                items.AddRange(part);
            }
        }
        return items;
    }

    /// <summary>AI provider kinds added by switched-on plugins.</summary>
    public List<IAiProvider> AiProviders() => Entries.Where(e => e.Context is not null).SelectMany(e => e.Context!.AiProviders).ToList();

    internal void ContributionsChanged() => Changed?.Invoke();

    private void Save()
    {
        try { JsonUtil.SaveFile(_stateFile, _state); }
        catch (Exception ex) { Log.Warn("plugin", "Could not save plugins.json: " + ex.Message); }
    }

    private sealed class PluginState
    {
        public List<string> Disabled { get; set; } = [];
    }
}

/// <summary>What one plugin sees of avamux, and what it registered (taken back when it is switched off).</summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly AppServices _app;
    private readonly PluginService _owner;
    private readonly PluginEntry _entry;
    private readonly Action _settingsChanged, _themeChanged;

    public PluginContext(AppServices app, PluginService owner, PluginEntry entry)
    {
        _app = app; _owner = owner; _entry = entry;
        DataDirectory = Path.Combine(app.Paths.DataDir, "plugin-data", string.Concat(entry.Id.Split(Path.GetInvalidFileNameChars())).Replace(':', '-'));
        Directory.CreateDirectory(DataDirectory);
        _settingsChanged = () => owner.Ui.Post(() => SettingsChanged?.Invoke());
        _themeChanged = () => ThemeChanged?.Invoke();
        app.Config.Changed += _settingsChanged;
        owner.Ui.ThemeChanged += _themeChanged;
    }

    public List<IMenuContributor> MenuContributors { get; } = [];
    public List<IAiProvider> AiProviders { get; } = [];
    public List<IWatchProvider> WatchProviders { get; } = [];
    public List<Func<object>> SettingsViews { get; } = [];

    public IEnumerable<string> Describe()
    {
        if (MenuContributors.Count > 0) yield return "menu items";
        if (WatchProviders.Count > 0) yield return "watch kinds: " + string.Join(", ", WatchProviders.Select(w => w.Kind));
        if (AiProviders.Count > 0) yield return "AI providers: " + string.Join(", ", AiProviders.Select(p => p.DisplayName));
        if (SettingsViews.Count > 0) yield return "settings";
    }

    public void RemoveAll()
    {
        foreach (var w in WatchProviders) _app.Watches.RemoveProvider(w);
        MenuContributors.Clear(); AiProviders.Clear(); WatchProviders.Clear(); SettingsViews.Clear();
        _app.Config.Changed -= _settingsChanged;
        _owner.Ui.ThemeChanged -= _themeChanged;
        SettingsChanged = null; ThemeChanged = null;
        _owner.ContributionsChanged();
    }

    public string PluginId => _entry.Id;
    public string Version => typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "";
    public string DataDirectory { get; }
    public string WikiDirectory => _app.Wiki.Root;
    public object Services => _app;
    public IAiService Ai => _app.Ai;

    public void AddMenuItems(IMenuContributor contributor) { MenuContributors.Add(contributor); _owner.ContributionsChanged(); }
    public void AddWatchProvider(IWatchProvider provider) { WatchProviders.Add(provider); _app.Watches.AddProvider(provider); }
    public void AddAiProvider(IAiProvider provider) { AiProviders.Add(provider); _owner.ContributionsChanged(); }
    public void AddSettingsView(Func<object> createView) => SettingsViews.Add(createView);

    public async Task<JsonNode?> CommandAsync(string name, JsonObject? args = null)
    {
        var response = await _app.Commands.ExecuteAsync(name, args ?? new JsonObject());
        if (response["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? $"'{name}' failed.");
        return response["result"]?.DeepClone();
    }

    public Task<JsonNode?> RunAsync(params string[] argv)
    {
        var parsed = CliParser.Parse(_app.Commands, argv);
        if (parsed.Error is not null) throw new InvalidOperationException(parsed.Error);
        if (parsed.Command is null) throw new InvalidOperationException("No command given.");
        return CommandAsync(parsed.Command.Name, parsed.Args);
    }

    public void Notify(string title, string message = "") => _app.Host.Notify(title, message);
    public void Toast(string title, string message = "") => _owner.Ui.Toast(title, message);
    public void Navigate(string page) => _app.Host.Navigate(page);

    public string? GetSetting(string path)
    {
        if (ConfigService.Canonical(path) is not { } key) return null;
        var value = _app.Config.Get(key);
        return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString();
    }

    public void SetSetting(string path, string value)
    {
        var key = ConfigService.Canonical(path) ?? throw new ArgumentException($"Unknown setting '{path}'.");
        _app.Config.Set(key, ConfigService.Coerce(key, value));
    }

    public event Action? SettingsChanged;
    public event Action? ThemeChanged;
    public string ThemeColor(string token) => _owner.Ui.ThemeColor(token);
    public void Log(string message) => Logging.Log.Info("plugin", $"{_entry.Name}: {message}");
    public void Post(Action action) => _owner.Ui.Post(action);
    public Task<string?> PromptAsync(string title, string label, string initial = "") => _owner.Ui.PromptAsync(title, label, initial);
    public Task<bool> ConfirmAsync(string title, string message, string action = "OK") => _owner.Ui.ConfirmAsync(title, message, action);
    public IPluginWindow ShowWindow(string title, object content, double width = 760, double height = 560) => _owner.Ui.ShowWindow(title, content, width, height);
}
