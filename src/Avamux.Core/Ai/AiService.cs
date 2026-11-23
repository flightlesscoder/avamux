using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Ai;

/// <summary>What the window does for AI: open a conversation in AI Chat (set by the app; nothing in tests).</summary>
public interface IAiChatHost
{
    Task<string> OpenChatAsync(AiChatRequest request);
}

/// <summary>
/// avamux's AI providers: the kinds it knows (Claude Code, pi, other command lines, and kinds plugins add), the
/// providers configured from them (ai-providers.json in the data folder, edited in Settings → AI Chat), one-off runs
/// for AI Actions, and the conversations of AI Chat (<see cref="Chats"/>).
/// </summary>
public sealed class AiService : IAiService, IAsyncDisposable
{
    private readonly AppServices _app;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly List<IAiProvider> _builtIn;
    private readonly List<IAiProvider> _extra = [];
    private ProviderFile _state;

    public AiService(AppServices app)
    {
        _app = app;
        _file = app.Paths.StateFile("ai-providers");
        Directory.CreateDirectory(Dir);
        PiExtensionPath = WritePiExtension();
        _builtIn = [new ClaudeCodeProvider(), new PiProvider(PiExtensionPath), new CommandProvider()];
        _state = Load();
        Chats = new ChatService(app, this);
        app.Plugins.Changed += () => KindsChanged?.Invoke();
    }

    /// <summary>The folder avamux keeps AI things in: chats, the pi extension, one-off runs.</summary>
    public string Dir => Path.Combine(_app.Paths.DataDir, "ai");
    public string PiExtensionPath { get; }

    /// <summary>AI Chat's conversations.</summary>
    public ChatService Chats { get; }

    /// <summary>Set by the window, to open AI Chat on a conversation.</summary>
    public IAiChatHost? ChatHost { get; set; }

    /// <summary>Raised when providers are added, changed or removed.</summary>
    public event Action? Changed;
    /// <summary>Raised when the kinds change (a plugin that adds one was switched on or off).</summary>
    public event Action? KindsChanged;

    // ---- kinds ----

    /// <summary>Every kind of provider: built in, then those plugins add (and tests).</summary>
    public IReadOnlyList<IAiProvider> Kinds
    {
        get
        {
            List<IAiProvider> extra;
            lock (_gate) extra = [.. _extra];
            return [.. _builtIn, .. _app.Plugins.AiProviders(), .. extra];
        }
    }

    public IAiProvider? Kind(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind);

    /// <summary>Adds a kind directly (tests, and code that is not a plugin).</summary>
    public void AddKind(IAiProvider kind) { lock (_gate) _extra.Add(kind); KindsChanged?.Invoke(); }

    // ---- configured providers ----

    private sealed class ProviderFile
    {
        public List<AiProviderConfig> Providers { get; set; } = [];
    }

    private ProviderFile Load()
    {
        if (File.Exists(_file)) return JsonUtil.LoadFile<ProviderFile>(_file);
        // First run: one of each tested kind, with nothing restricted (Settings → AI Chat restricts them).
        return new ProviderFile
        {
            Providers =
            [
                new AiProviderConfig { Id = "claude-code", Kind = ClaudeCodeProvider.KindId, Name = "Claude Code" },
                new AiProviderConfig { Id = "pi", Kind = PiProvider.KindId, Name = "pi" },
            ],
        };
    }

    private void Save()
    {
        try { JsonUtil.SaveFile(_file, _state); }
        catch (Exception ex) { Log.Warn("ai", "Could not save ai-providers.json: " + ex.Message); }
    }

    public IReadOnlyList<AiProviderConfig> Configs { get { lock (_gate) return _state.Providers.Select(p => p.Clone()).ToList(); } }

    public AiProviderConfig? Config(string idOrName)
    {
        lock (_gate)
            return (_state.Providers.FirstOrDefault(p => p.Id == idOrName) ?? _state.Providers.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase)))?.Clone();
    }

    public AiProviderConfig GetConfig(string idOrName) => Config(idOrName) ?? throw new KeyNotFoundException($"No AI provider '{idOrName}'. See `avamux ai providers`.");

    /// <summary>Adds a provider, or replaces the one with the same id.</summary>
    public AiProviderConfig Put(AiProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name)) throw new ArgumentException("A provider needs a name.");
        if (Kind(config.Kind) is null) throw new ArgumentException($"Unknown provider kind '{config.Kind}'. Known: {string.Join(", ", Kinds.Select(k => k.Kind))}.");
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(config.Id))
            {
                var stem = string.Concat(config.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
                if (stem.Length == 0) stem = config.Kind;
                var id = stem;
                for (var n = 2; _state.Providers.Any(p => p.Id == id); n++) id = $"{stem}-{n}";
                config.Id = id;
            }
            var i = _state.Providers.FindIndex(p => p.Id == config.Id);
            if (i >= 0) _state.Providers[i] = config.Clone(); else _state.Providers.Add(config.Clone());
            Save();
        }
        Changed?.Invoke();
        return config;
    }

    public void Remove(string idOrName)
    {
        var c = GetConfig(idOrName);
        lock (_gate) { _state.Providers.RemoveAll(p => p.Id == c.Id); Save(); }
        Changed?.Invoke();
    }

    // ---- IAiService ----

    public IReadOnlyList<AiProviderInfo> Providers =>
        Configs.Where(c => c.Enabled).Select(c => (c, k: Kind(c.Kind))).Where(x => x.k is not null)
            .Select(x => new AiProviderInfo(x.c.Id, x.c.Name, x.c.Kind, x.c.Model, x.c.Effort, x.k!.Capabilities)).ToList();

    public string? DefaultProviderId
    {
        get
        {
            var wanted = _app.Config.Settings.Ai.ActionsProvider;
            var list = Providers;
            return list.FirstOrDefault(p => p.Id == wanted)?.Id ?? list.FirstOrDefault()?.Id;
        }
    }

    /// <summary>The working folder for AI: the setting, else the wiki.</summary>
    public string DefaultFolder => _app.Config.Settings.Ai.ChatFolder is { Length: > 0 } f ? ExpandHome(f) : _app.Wiki.Root;

    /// <summary>Starts a conversation with a configured provider.</summary>
    public IAiSession Start(string providerId, string? model, string? effort, string? folder, string? systemPrompt = null,
        string? resume = null, bool fork = false, string? forkAt = null, bool noTools = false, bool ephemeral = false, string? stateDirectory = null)
    {
        var config = GetConfig(providerId);
        if (!config.Enabled) throw new InvalidOperationException($"{config.Name} is switched off (Settings → AI Chat).");
        var kind = Kind(config.Kind) ?? throw new InvalidOperationException($"{config.Name} is a '{config.Kind}' provider, and nothing provides that kind (was its plugin switched off?).");
        return kind.StartSession(new AiSessionOptions
        {
            Provider = config, Model = string.IsNullOrWhiteSpace(model) ? config.Model : model, Effort = string.IsNullOrWhiteSpace(effort) ? config.Effort : effort,
            WorkingDirectory = folder ?? DefaultFolder, SystemPrompt = systemPrompt, Resume = resume, ForkOnResume = fork, ForkAt = forkAt,
            NoTools = noTools, Ephemeral = ephemeral, StateDirectory = stateDirectory,
        });
    }

    public async Task<AiRunResult> RunAsync(AiRunRequest request, Action<AiEvent>? onEvent = null, CancellationToken ct = default)
    {
        var providerId = request.ProviderId ?? DefaultProviderId;
        if (providerId is null) return new AiRunResult { Ok = false, Error = "No AI provider is set up. Add one under Settings → AI Chat." };
        var actions = _app.Config.Settings.Ai;
        var usingDefault = request.ProviderId is null || request.ProviderId == actions.ActionsProvider;
        var model = request.Model ?? (usingDefault && actions.ActionsModel.Length > 0 ? actions.ActionsModel : null);
        var effort = request.Effort ?? (usingDefault && actions.ActionsEffort.Length > 0 ? actions.ActionsEffort : null);
        var folder = request.WorkingDirectory ?? DefaultFolder;
        var state = Path.Combine(Dir, "runs");
        Directory.CreateDirectory(state);
        IAiSession session;
        try { session = Start(providerId, model, effort, folder, request.SystemPrompt, noTools: !request.AllowTools, stateDirectory: state); }
        catch (Exception ex) { return new AiRunResult { Ok = false, Error = ex.Message, ProviderId = providerId }; }
        var text = new StringBuilder();
        string? error = null;
        var ok = false;
        session.Event += e =>
        {
            switch (e)
            {
                case AiTextDelta d: lock (text) text.Append(d.Text); break;
                case AiError err: error = error is null ? err.Message : error + "\n" + err.Message; break;
                case AiTurnFinished f: ok = f.Ok; break;
                // Nobody is there to answer in a one-off run.
                case AiPermissionRequest p: _ = session.AnswerAsync(p.Id, new AiAnswer { Decision = AiDecision.Deny, Reason = "Not available in this action; answer in AI Chat instead." }); break;
                case AiQuestionRequest q: _ = session.AnswerAsync(q.Id, new AiAnswer { Decision = AiDecision.Deny, Reason = "Nobody can answer here; make your best judgement." }); break;
            }
            onEvent?.Invoke(e);
        };
        await using (session)
        {
            try { await session.SendAsync(new AiUserMessage { Text = request.Prompt, Attachments = request.Attachments }, ct); }
            catch (Exception ex) { error ??= ex.Message; }
            string reply;
            lock (text) reply = text.ToString().Trim();
            return new AiRunResult
            {
                Ok = ok && error is null, Text = reply, Error = error, ProviderId = providerId, Model = model, Effort = effort,
                ProviderSessionId = session.ProviderSessionId, WorkingDirectory = folder,
            };
        }
    }

    public Task<string> OpenChatAsync(AiChatRequest request) =>
        ChatHost?.OpenChatAsync(request) ?? Task.FromResult(Chats.Create(request).Id);

    // ---- helpers for providers ----

    public static string ExpandHome(string path) =>
        path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

    /// <summary>A short description of a tool call: its name and the argument that says what it does.</summary>
    public static string ToolSummary(string tool, JsonObject input)
    {
        foreach (var key in new[] { "file_path", "path", "command", "pattern", "url", "query", "description", "prompt", "notebook_path", "skill" })
            if (input[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
            {
                s = s.Replace('\n', ' ');
                return $"{tool} {(s.Length > 140 ? s[..140] + "…" : s)}";
            }
        return tool;
    }

    /// <summary>The message text, with a line naming each attached file the provider does not receive as a picture.</summary>
    public static string WithAttachmentNotes(AiUserMessage message, bool images)
    {
        var notes = message.Attachments.Where(a => !images || !a.IsImage).Select(a => $"[Attached file: {a.Path}]").ToList();
        return notes.Count == 0 ? message.Text : message.Text + "\n\n" + string.Join("\n", notes);
    }

    private string WritePiExtension()
    {
        var path = Path.Combine(Dir, "pi-avamux-extension.ts");
        try
        {
            using var stream = typeof(AiService).Assembly.GetManifestResourceStream("Avamux.Core.Ai.pi-extension.ts");
            if (stream is null) return path;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        }
        catch (Exception ex) { Log.Warn("ai", "Could not write the pi extension: " + ex.Message); }
        return path;
    }

    public async ValueTask DisposeAsync() => await Chats.DisposeAsync();
}
