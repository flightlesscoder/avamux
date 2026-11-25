using System.Text;
using System.Text.Json.Serialization;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Ai;

/// <summary>A conversation in AI Chat. Messages form a tree (an edited message starts a branch); <see cref="Selected"/> picks the branch shown.</summary>
public sealed class AiChat
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    /// <summary>The agent's working folder.</summary>
    public string Folder { get; set; } = "";
    public string? SystemPrompt { get; set; }
    /// <summary>Text waiting in the message box.</summary>
    public string? Draft { get; set; }
    public bool Pinned { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public long? ContextTokens { get; set; }
    public long? ContextWindow { get; set; }
    public double? CostUsd { get; set; }
    public List<AiChatMessage> Messages { get; set; } = [];
    /// <summary>For each parent ("" for the first message), the child on the branch shown.</summary>
    public Dictionary<string, string> Selected { get; set; } = [];
}

public sealed class AiChatMessage
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    /// <summary>user or assistant.</summary>
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string? Thinking { get; set; }
    /// <summary>How long the model thought, for "Thought for 12 s".</summary>
    public double? ThinkingSeconds { get; set; }
    public List<AiChatTool> Tools { get; set; } = [];
    public List<AiChatAttachment> Attachments { get; set; } = [];
    /// <summary>User messages: where to branch to send it again (the provider's <see cref="AiTurnStarted.ForkToken"/>).</summary>
    public string? ForkToken { get; set; }
    /// <summary>Assistant messages: the provider's conversation id once written (it changes when a branch is made).</summary>
    public string? ProviderSession { get; set; }
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    /// <summary>The text was changed by hand after it was written.</summary>
    public bool Edited { get; set; }
    public string? Error { get; set; }
    /// <summary>Status lines the provider sent while writing (retries, compaction).</summary>
    public List<string> Notes { get; set; } = [];
    public bool Stopped { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    /// <summary>Assistant messages: still being written.</summary>
    [JsonIgnore] public bool IsWriting { get; set; }
    /// <summary>Assistant messages: thinking now (no text yet).</summary>
    [JsonIgnore] public bool IsThinking { get; set; }
}

public sealed class AiChatTool
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Input { get; set; }
    public string? Output { get; set; }
    public bool IsError { get; set; }
    public bool Done { get; set; }
}

public sealed record AiChatAttachment(string Path, string MimeType, string Name)
{
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public AiAttachment ToAi() => new(Path, MimeType, Name);
}

/// <summary>A question or permission request waiting for the user in a chat.</summary>
public sealed record PendingRequest(string ChatId, AiEvent Request)
{
    public string Id => Request switch { AiPermissionRequest p => p.Id, AiQuestionRequest q => q.Id, _ => "" };
}

/// <summary>
/// The conversations of AI Chat: kept in the data folder (one folder per chat, with its attachments), run against
/// their providers (one live session per chat, started when needed and continued across restarts), branched when a
/// message is edited, and stopped, answered and renamed. Shared by the AI Chat page and <c>avamux ai chat</c>.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
    private readonly AppServices _app;
    private readonly AiService _ai;
    private readonly object _gate = new();
    private readonly Dictionary<string, AiChat> _chats = [];
    private readonly Dictionary<string, Live> _live = [];
    private readonly Dictionary<string, Timer> _saves = [];
    private bool _loaded;

    private sealed class Live
    {
        public required IAiSession Session { get; init; }
        /// <summary>Which provider conversation the session is on, and with what, so a change of branch or provider starts another.</summary>
        public required string Key { get; set; }
        public AiChatMessage? Writing { get; set; }
        public DateTime? ThinkingSince { get; set; }
        public List<PendingRequest> Requests { get; } = [];
    }

    public ChatService(AppServices app, AiService ai) { _app = app; _ai = ai; }

    public string Dir => Path.Combine(_ai.Dir, "chats");

    /// <summary>Raised (on a background thread) when a chat changed: its messages, title, settings, or what it waits for. Null: the list changed.</summary>
    public event Action<string?>? Changed;

    private void Raise(string? id) { try { Changed?.Invoke(id); } catch (Exception ex) { Log.Error("ai", "A chat change handler failed", ex); } }

    // ---- storage ----

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (!Directory.Exists(Dir)) return;
            foreach (var folder in Directory.EnumerateDirectories(Dir))
            {
                var file = Path.Combine(folder, "chat.json");
                if (!File.Exists(file)) continue;
                var chat = JsonUtil.LoadFile<AiChat>(file);
                if (chat.Id.Length == 0) chat.Id = Path.GetFileName(folder);
                _chats[chat.Id] = chat;
            }
        }
    }

    public string FolderOf(string id) => Path.Combine(Dir, id);

    private void SaveNow(string id)
    {
        AiChat? chat;
        lock (_gate) _chats.TryGetValue(id, out chat);
        if (chat is null) return;
        try
        {
            Directory.CreateDirectory(FolderOf(id));
            string json;
            lock (chat) json = System.Text.Json.JsonSerializer.Serialize(chat, JsonUtil.Options);
            var file = Path.Combine(FolderOf(id), "chat.json");
            File.WriteAllText(file + ".tmp", json);
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception ex) { Log.Warn("ai", $"Could not save chat {id}: {ex.Message}"); }
    }

    /// <summary>Saves within a second or so; many changes in a row (a reply streaming in) are written once.</summary>
    private void SaveSoon(string id)
    {
        lock (_gate)
        {
            if (!_saves.TryGetValue(id, out var t)) _saves[id] = t = new Timer(_ => SaveNow(id));
            t.Change(1000, Timeout.Infinite);
        }
    }

    public IReadOnlyList<AiChat> List()
    {
        EnsureLoaded();
        lock (_gate) return _chats.Values.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.Updated).ToList();
    }

    public AiChat? Find(string id)
    {
        EnsureLoaded();
        lock (_gate)
            return _chats.GetValueOrDefault(id) ?? _chats.Values.FirstOrDefault(c => string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase));
    }

    public AiChat Get(string id) => Find(id) ?? throw new KeyNotFoundException($"No chat '{id}'. See `avamux ai chat list`.");

    public AiChat Create(AiChatRequest request)
    {
        EnsureLoaded();
        var preferred = _app.Config.Settings.Ai.ChatProvider;
        var provider = request.ProviderId
            ?? (preferred.Length > 0 && _ai.Providers.Any(x => x.Id == preferred) ? preferred : _ai.Providers.FirstOrDefault()?.Id) ?? "";
        var config = provider.Length > 0 ? _ai.Config(provider) : null;
        var chat = new AiChat
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Title = request.Title ?? "New chat", ProviderId = provider,
            Model = request.Model ?? config?.Model ?? "", Effort = request.Effort ?? config?.Effort ?? "",
            Folder = request.WorkingDirectory ?? _ai.DefaultFolder, SystemPrompt = request.SystemPrompt, Draft = request.Draft,
        };
        string? parent = null;
        foreach (var (role, text) in request.History)
        {
            var m = new AiChatMessage { Id = NewId(), ParentId = parent, Role = role == "assistant" ? "assistant" : "user", Text = text, ProviderId = provider, Model = chat.Model };
            if (m.Role == "assistant") m.ProviderSession = request.Resume;
            else m.ForkToken = parent is null ? "" : null;
            chat.Messages.Add(m);
            chat.Selected[parent ?? ""] = m.Id;
            parent = m.Id;
        }
        if (request.Title is null && request.History.FirstOrDefault(h => h.Role == "user").Text is { } first) chat.Title = TitleFrom(first);
        lock (_gate) _chats[chat.Id] = chat;
        SaveNow(chat.Id);
        Raise(null);
        if (request.Send is { Length: > 0 } send) _ = SendAsync(chat.Id, send);
        return chat;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string TitleFrom(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim().TrimStart('#', ' ');
        return line.Length == 0 ? "New chat" : line.Length > 60 ? line[..57].TrimEnd() + "…" : line;
    }

    public void Rename(string id, string title) { var c = Get(id); lock (c) c.Title = title.Trim().Length > 0 ? title.Trim() : c.Title; Touched(c, list: true); }
    public void Pin(string id, bool pinned) { var c = Get(id); lock (c) c.Pinned = pinned; Touched(c, list: true); }
    public void SetDraft(string id, string? draft) { var c = Get(id); lock (c) c.Draft = draft; SaveSoon(c.Id); }

    public async Task DeleteAsync(string id)
    {
        var c = Get(id);
        await DropLiveAsync(c.Id);
        lock (_gate) { _chats.Remove(c.Id); if (_saves.Remove(c.Id, out var t)) t.Dispose(); }
        try { if (Directory.Exists(FolderOf(c.Id))) Directory.Delete(FolderOf(c.Id), true); } catch (Exception ex) { Log.Warn("ai", $"Could not delete chat {id}: {ex.Message}"); }
        Raise(null);
    }

    private void Touched(AiChat c, bool list = false)
    {
        SaveSoon(c.Id);
        Raise(c.Id);
        if (list) Raise(null);
    }

    // ---- the branch shown ----

    /// <summary>The messages of the branch shown, first to last.</summary>
    public static List<AiChatMessage> Thread(AiChat chat)
    {
        lock (chat)
        {
            var result = new List<AiChatMessage>();
            var byParent = chat.Messages.GroupBy(m => m.ParentId ?? "").ToDictionary(g => g.Key, g => g.ToList());
            var parent = "";
            while (byParent.TryGetValue(parent, out var children) && children.Count > 0)
            {
                var next = chat.Selected.TryGetValue(parent, out var sel) ? children.FirstOrDefault(c => c.Id == sel) ?? children[^1] : children[^1];
                result.Add(next);
                parent = next.Id;
            }
            return result;
        }
    }

    /// <summary>A message's siblings (itself among them): the versions of it, oldest first.</summary>
    public static List<AiChatMessage> Versions(AiChat chat, AiChatMessage message)
    {
        lock (chat) return chat.Messages.Where(m => m.ParentId == message.ParentId && m.Role == message.Role).ToList();
    }

    /// <summary>Shows another version of a message (and what followed it).</summary>
    public void SelectVersion(string chatId, string messageId)
    {
        var c = Get(chatId);
        lock (c)
        {
            var m = c.Messages.FirstOrDefault(x => x.Id == messageId) ?? throw new KeyNotFoundException("No such message.");
            c.Selected[m.ParentId ?? ""] = m.Id;
        }
        Touched(c);
    }

    // ---- running ----

    public bool IsBusy(string chatId) { lock (_gate) return _live.TryGetValue(chatId, out var l) && l.Session.IsBusy; }

    /// <summary>A chat writing a reply now.</summary>
    /// <param name="Waiting">Questions or permission requests it waits on.</param>
    public sealed record BusyChat(AiChat Chat, AiChatMessage? Writing, DateTime? ThinkingSince, int Waiting);

    /// <summary>The chats writing a reply now (for AI Workloads).</summary>
    public IReadOnlyList<BusyChat> Busy()
    {
        lock (_gate)
            return _live.Where(kv => kv.Value.Session.IsBusy && _chats.ContainsKey(kv.Key))
                .Select(kv => new BusyChat(_chats[kv.Key], kv.Value.Writing, kv.Value.ThinkingSince, kv.Value.Requests.Count)).ToList();
    }

    /// <summary>The questions and permission requests a chat waits on.</summary>
    public IReadOnlyList<PendingRequest> Requests(string chatId)
    {
        lock (_gate) return _live.TryGetValue(chatId, out var l) ? [.. l.Requests] : [];
    }

    public IReadOnlyList<PendingRequest> AllRequests()
    {
        lock (_gate) return _live.Values.SelectMany(l => l.Requests).ToList();
    }

    /// <summary>Copies a file into the chat's attachments folder.</summary>
    public AiChatAttachment Attach(string chatId, string file) => AttachBytes(chatId, File.ReadAllBytes(file), Path.GetFileName(file));

    public AiChatAttachment AttachBytes(string chatId, byte[] data, string name)
    {
        var folder = Path.Combine(FolderOf(chatId), "attachments");
        Directory.CreateDirectory(folder);
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) name = "file";
        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}");
        File.WriteAllBytes(path, data);
        return new AiChatAttachment(path, Web.WebServer.MimeOf(path), Path.GetFileName(path));
    }

    /// <summary>Sends a message at the end of the branch shown, and completes when the reply is written.</summary>
    public Task SendAsync(string chatId, string text, IReadOnlyList<AiChatAttachment>? attachments = null)
    {
        var c = Get(chatId);
        AiChatMessage? last;
        lock (c) last = Thread(c).LastOrDefault();
        return RunAsync(c, last?.Id, text, attachments ?? [], fork: false, forkToken: null);
    }

    /// <summary>Sends a new version of a message of the user's: a branch from just before it.</summary>
    public Task EditAndResendAsync(string chatId, string messageId, string text)
    {
        var c = Get(chatId);
        AiChatMessage original;
        lock (c) original = c.Messages.First(m => m.Id == messageId && m.Role == "user");
        return RunAsync(c, original.ParentId, text, original.Attachments, fork: true, forkToken: original.ForkToken);
    }

    /// <summary>Writes a reply again: the message before it, sent again as a new version.</summary>
    public Task RetryAsync(string chatId, string assistantMessageId)
    {
        var c = Get(chatId);
        AiChatMessage user;
        lock (c)
        {
            var reply = c.Messages.First(m => m.Id == assistantMessageId);
            user = c.Messages.First(m => m.Id == reply.ParentId);
        }
        return EditAndResendAsync(chatId, user.Id, user.Text);
    }

    /// <summary>Changes the saved text of a reply (nothing is sent again).</summary>
    public void EditReply(string chatId, string messageId, string text)
    {
        var c = Get(chatId);
        lock (c)
        {
            var m = c.Messages.First(x => x.Id == messageId);
            if (m.Text == text) return;
            m.Text = text;
            m.Edited = true;
        }
        Touched(c);
    }

    public async Task StopAsync(string chatId)
    {
        Live? live;
        lock (_gate) _live.TryGetValue(chatId, out live);
        if (live is not null) await live.Session.AbortAsync();
    }

    public async Task AnswerAsync(string chatId, string requestId, AiAnswer answer)
    {
        Live? live;
        lock (_gate) _live.TryGetValue(chatId, out live);
        if (live is null) return;
        lock (_gate) live.Requests.RemoveAll(r => r.Id == requestId);
        Raise(chatId);
        await live.Session.AnswerAsync(requestId, answer);
    }

    /// <summary>Changes a chat's provider, model, effort or folder. Model and effort apply to the live conversation; the others start a new one (given what was said so far).</summary>
    public async Task ConfigureAsync(string chatId, string? provider = null, string? model = null, string? effort = null, string? folder = null)
    {
        var c = Get(chatId);
        var restart = false;
        lock (c)
        {
            if (provider is not null && provider != c.ProviderId)
            {
                var config = _ai.GetConfig(provider);
                c.ProviderId = config.Id;
                c.Model = model ?? config.Model;
                c.Effort = effort ?? config.Effort;
                restart = true;
            }
            if (folder is not null && folder != c.Folder) { c.Folder = folder; restart = true; }
            if (model is not null) c.Model = model;
            if (effort is not null) c.Effort = effort;
        }
        Live? live;
        lock (_gate) _live.TryGetValue(c.Id, out live);
        if (live is not null)
        {
            if (restart) await DropLiveAsync(c.Id);
            else
            {
                if (model is not null) await live.Session.SetModelAsync(model);
                if (effort is not null) await live.Session.SetEffortAsync(effort);
                live.Key = KeyFor(c, live.Key.Split('|')[0]);
            }
        }
        Touched(c);
    }

    private string KeyFor(AiChat c, string providerSession) => $"{providerSession}|{c.ProviderId}|{c.Folder}";

    private async Task DropLiveAsync(string chatId)
    {
        Live? live;
        lock (_gate) { if (_live.Remove(chatId, out live)) { } }
        if (live is not null) await live.Session.DisposeAsync();
    }

    /// <summary>The transcript of a branch, for a provider that cannot continue the conversation itself.</summary>
    private static string Transcript(IEnumerable<AiChatMessage> messages)
    {
        var sb = new StringBuilder("Earlier in this conversation (continue it; do not repeat it):\n\n");
        foreach (var m in messages) sb.Append(m.Role == "user" ? "User: " : "Assistant: ").AppendLine(m.Text.Trim()).AppendLine();
        return sb.ToString();
    }

    private async Task RunAsync(AiChat c, string? parentId, string text, IReadOnlyList<AiChatAttachment> attachments, bool fork, string? forkToken)
    {
        if (IsBusy(c.Id)) throw new InvalidOperationException("This chat is still writing a reply. Stop it first.");
        if (string.IsNullOrWhiteSpace(c.ProviderId) || _ai.Config(c.ProviderId) is null)
            throw new InvalidOperationException("Choose a provider for this chat first (Settings → AI Chat sets them up).");
        AiChatMessage user, reply;
        List<AiChatMessage> before;
        lock (c)
        {
            // What came before, on this branch: the conversation the provider continues.
            var path = new List<AiChatMessage>();
            var byId = c.Messages.ToDictionary(m => m.Id);
            for (var at = parentId; at is not null && byId.TryGetValue(at, out var m); at = m.ParentId) path.Insert(0, m);
            before = path;
            user = new AiChatMessage { Id = NewId(), ParentId = parentId, Role = "user", Text = text, Attachments = [.. attachments], ProviderId = c.ProviderId, Model = c.Model };
            reply = new AiChatMessage { Id = NewId(), ParentId = user.Id, Role = "assistant", ProviderId = c.ProviderId, Model = c.Model, IsWriting = true };
            c.Messages.Add(user);
            c.Messages.Add(reply);
            c.Selected[parentId ?? ""] = user.Id;
            c.Selected[user.Id] = reply.Id;
            c.Updated = DateTime.Now;
            c.Draft = null;
            if (c.Title is "New chat" or "" && parentId is null) c.Title = TitleFrom(text);
        }
        Touched(c, list: true);

        // Which provider conversation to continue: the one the last reply on this branch was written in.
        var lastReply = before.LastOrDefault(m => m.Role == "assistant" && m.ProviderId == c.ProviderId && m.ProviderSession is not null);
        var resume = lastReply?.ProviderSession;
        var mixed = before.Any(m => m.Role == "assistant" && (m.ProviderId != c.ProviderId || m.ProviderSession is null));
        var key = KeyFor(c, resume ?? "");
        Live? live;
        lock (_gate) _live.TryGetValue(c.Id, out live);
        if (live is not null && (fork || live.Key != key)) { await DropLiveAsync(c.Id); live = null; }
        if (live is null)
        {
            // A branch: from the conversation before the edited message. Its first message has nothing before it: start afresh.
            var forkHere = fork && resume is not null && forkToken is { Length: > 0 };
            var system = c.SystemPrompt;
            if (before.Count > 0 && (resume is null || mixed || fork && !forkHere))
                system = (system is null ? "" : system + "\n\n") + Transcript(before);
            if (fork && !forkHere) resume = null;
            IAiSession session;
            try
            {
                session = _ai.Start(c.ProviderId, c.Model, c.Effort, string.IsNullOrWhiteSpace(c.Folder) ? null : c.Folder, system,
                    resume: resume, fork: forkHere, forkAt: forkHere ? forkToken : null, stateDirectory: FolderOf(c.Id));
            }
            catch (Exception ex)
            {
                lock (c) { reply.Error = ex.Message; reply.IsWriting = false; }
                Touched(c);
                return;
            }
            live = new Live { Session = session, Key = key };
            var l = live;
            session.Event += e => OnEvent(c, l, e);
            lock (_gate) _live[c.Id] = live;
        }
        live.Writing = reply;
        try { await live.Session.SendAsync(new AiUserMessage { Text = text, Attachments = attachments.Select(a => a.ToAi()).ToList() }); }
        catch (Exception ex) { lock (c) reply.Error ??= ex.Message; }
        finally
        {
            lock (c)
            {
                reply.IsWriting = false;
                reply.IsThinking = false;
                reply.ProviderSession = live.Session.ProviderSessionId ?? reply.ProviderSession;
                c.Updated = DateTime.Now;
            }
            live.Key = KeyFor(c, reply.ProviderSession ?? "");
            lock (_gate) live.Requests.Clear();
            Touched(c, list: true);
        }
    }

    private void OnEvent(AiChat c, Live live, AiEvent e)
    {
        var m = live.Writing;
        if (m is null) return;
        var listChanged = false;
        lock (c)
        {
            switch (e)
            {
                case AiTurnStarted t:
                    var user = c.Messages.FirstOrDefault(x => x.Id == m.ParentId);
                    if (user is not null && t.ForkToken is not null) user.ForkToken = t.ForkToken;
                    break;
                case AiSessionStarted s: m.ProviderSession = s.ProviderSessionId; break;
                case AiThinkingStarted:
                    m.IsThinking = true;
                    live.ThinkingSince ??= DateTime.Now;
                    break;
                case AiThinkingDelta d:
                    m.IsThinking = true;
                    live.ThinkingSince ??= DateTime.Now;
                    m.Thinking = (m.Thinking ?? "") + d.Text;
                    break;
                case AiTextDelta d:
                    if (live.ThinkingSince is { } since) { m.ThinkingSeconds = (m.ThinkingSeconds ?? 0) + (DateTime.Now - since).TotalSeconds; live.ThinkingSince = null; }
                    m.IsThinking = false;
                    m.Text += d.Text;
                    break;
                case AiToolStarted t:
                    if (live.ThinkingSince is { } s2) { m.ThinkingSeconds = (m.ThinkingSeconds ?? 0) + (DateTime.Now - s2).TotalSeconds; live.ThinkingSince = null; }
                    m.IsThinking = false;
                    if (m.Tools.All(x => x.Id != t.Id || t.Id.Length == 0)) m.Tools.Add(new AiChatTool { Id = t.Id, Name = t.Name, Summary = t.Summary, Input = t.InputJson });
                    break;
                case AiToolFinished f:
                    if (m.Tools.LastOrDefault(x => x.Id == f.Id) is { } tool)
                    {
                        tool.Done = true;
                        tool.IsError = f.IsError;
                        tool.Output = f.Output is { Length: > 20000 } o ? o[..20000] + "\n…" : f.Output;
                    }
                    break;
                case AiUsage u:
                    if (u.ContextTokens is { } ctx) c.ContextTokens = ctx;
                    if (u.ContextWindow is { } w) c.ContextWindow = w;
                    if (u.CostUsd is { } cost) c.CostUsd = cost;
                    break;
                case AiStatus s: m.Notes.Add(s.Text); break;
                case AiError err: m.Error = m.Error is null ? err.Message : m.Error + "\n" + err.Message; break;
                case AiTurnFinished f:
                    if (live.ThinkingSince is { } s3) { m.ThinkingSeconds = (m.ThinkingSeconds ?? 0) + (DateTime.Now - s3).TotalSeconds; live.ThinkingSince = null; }
                    m.Stopped = f.StopReason == "stopped";
                    m.IsWriting = false;
                    m.IsThinking = false;
                    listChanged = true;
                    break;
            }
        }
        switch (e)
        {
            case AiPermissionRequest or AiQuestionRequest:
                lock (_gate) live.Requests.Add(new PendingRequest(c.Id, e));
                _app.Host.Notify(e is AiPermissionRequest p ? $"{c.Title}: allow {p.Tool}?" : $"{c.Title} has a question", e is AiPermissionRequest p2 ? p2.Summary : ((AiQuestionRequest)e).Questions.FirstOrDefault()?.Question ?? "");
                RequestRaised?.Invoke(c.Id);
                break;
            case AiRequestClosed closed:
                lock (_gate) live.Requests.RemoveAll(r => r.Id == closed.Id);
                break;
        }
        if (e is AiTextDelta or AiThinkingDelta) { SaveSoon(c.Id); Raise(c.Id); }
        else Touched(c, listChanged);
    }

    /// <summary>Raised when a chat starts waiting for an answer from the user.</summary>
    public event Action<string>? RequestRaised;

    public async ValueTask DisposeAsync()
    {
        List<Live> all;
        List<Timer> saves;
        List<string> ids;
        lock (_gate) { all = [.. _live.Values]; _live.Clear(); saves = [.. _saves.Values]; ids = [.. _saves.Keys]; _saves.Clear(); }
        foreach (var t in saves) t.Dispose();
        foreach (var id in ids) SaveNow(id);
        await Task.WhenAll(all.Select(l => l.Session.DisposeAsync().AsTask()));
    }
}
