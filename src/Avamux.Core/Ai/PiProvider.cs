using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Ai;

/// <summary>
/// The pi coding agent in RPC mode (<c>pi --mode rpc</c>): JSON commands on stdin, events on stdout, one process per
/// conversation. avamux's own pi extension adds an ask_user tool and, when set to, asks before tools run; both reach
/// avamux as pi's extension UI dialogs.
/// </summary>
public sealed partial class PiProvider(string extensionPath) : IAiProvider
{
    public const string KindId = "pi";
    public string Kind => KindId;
    public string DisplayName => "pi";
    public string Description => "The pi coding agent (pi), with whatever models and providers pi is set up for (local llama.cpp servers, cloud providers).";

    public AiCapabilities Capabilities { get; } = new()
    {
        Thinking = true, Images = true, Fork = true, Permissions = true, Questions = true, ContextUsage = true,
        EffortLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"],
    };

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();

    /// <summary>Runs <c>pi --list-models</c> and reads its table (provider, model, context, max-out, thinking, images).</summary>
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(AiProviderConfig config, CancellationToken ct = default)
    {
        var command = string.IsNullOrWhiteSpace(config.Command) ? "pi" : AiService.ExpandHome(config.Command);
        try
        {
            var r = await Shell.RunAsync(command, ["--list-models", "--offline"], env: config.Env, timeout: TimeSpan.FromSeconds(20), ct: ct);
            var models = new List<AiModel> { new("", "Default", Note: "pi's own default") };
            foreach (var line in r.Stdout.Split('\n').Skip(1))
            {
                var cells = Columns().Split(line.Trim());
                if (cells.Length < 2 || cells[0].Length == 0) continue;
                int? context = cells.Length > 2 ? ParseSize(cells[2]) : null;
                models.Add(new AiModel($"{cells[0]}/{cells[1]}", $"{cells[1]} ({cells[0]})", context,
                    Thinking: cells.Length > 4 && cells[4] == "yes", Images: cells.Length > 5 && cells[5] == "yes"));
            }
            return models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("ai", "pi --list-models failed: " + ex.Message);
            return [new("", "Default", Note: "pi's own default")];
        }
    }

    private static int? ParseSize(string text)
    {
        text = text.Trim();
        var mult = text.EndsWith('K') ? 1000 : text.EndsWith('M') ? 1_000_000 : 1;
        return double.TryParse(text.TrimEnd('K', 'M'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (int)(v * mult) : null;
    }

    public IAiSession StartSession(AiSessionOptions options) => new PiSession(options, extensionPath);
}

internal sealed class PiSession(AiSessionOptions options, string extensionPath) : IAiSession
{
    private const string QuestionMarker = "[avamux:question]", PermissionMarker = "[avamux:permission]";
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _responses = [];
    private readonly Dictionary<string, (string Method, string Kind, string? Tool)> _pending = [];
    private readonly HashSet<string> _allowedForSession = [];
    private JsonLinesProcess? _process;
    private TaskCompletionSource? _turn;
    private string? _sessionFile = options.Resume;
    private string? _model = options.Model, _effort = options.Effort;
    private bool _thinkingShown, _aborting, _text;
    private bool _forkPending = options.ForkOnResume && options.Resume is { Length: > 0 };
    private int _next;

    public string? ProviderSessionId => _sessionFile;
    public bool IsBusy => _turn is { Task.IsCompleted: false };
    public event Action<AiEvent>? Event;

    private void Emit(AiEvent e) { try { Event?.Invoke(e); } catch (Exception ex) { Log.Error("ai", "A chat event handler failed", ex); } }

    private List<string> Arguments()
    {
        var p = options.Provider;
        var args = new List<string> { "--mode", "rpc" };
        if (options.Ephemeral) args.Add("--no-session");
        else if (options.StateDirectory is { } state) args.AddRange(["--session-dir", Path.Combine(state, "pi")]);
        if (_sessionFile is { Length: > 0 } resume && !options.Ephemeral)
            args.AddRange(_forkPending ? ["--fork", resume] : ["--session", resume]);
        if (!string.IsNullOrWhiteSpace(_model)) args.AddRange(["--model", _model!]);
        if (!string.IsNullOrWhiteSpace(_effort)) args.AddRange(["--thinking", _effort!]);
        if (options.NoTools) args.Add("--no-tools");
        else
        {
            if (p.AllowedTools.Count > 0) args.AddRange(["--tools", string.Join(",", p.AllowedTools)]);
            if (p.DisallowedTools.Count > 0) args.AddRange(["--exclude-tools", string.Join(",", p.DisallowedTools)]);
        }
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt)) args.AddRange(["--append-system-prompt", options.SystemPrompt!]);
        if (File.Exists(extensionPath)) args.AddRange(["-e", extensionPath]);
        args.AddRange(p.Args);
        return args;
    }

    private async Task EnsureStartedAsync()
    {
        if (_process is { HasExited: false }) return;
        if (_process is not null) { await _process.DisposeAsync(); _process = null; }
        var env = new Dictionary<string, string>(options.Provider.Env);
        // Ask before tools run only when set to (Settings → AI Chat: "Ask before these tools run").
        if (!options.Provider.FullAccess && options.Provider.Option("confirmTools") is { Length: > 0 } confirm) env["AVAMUX_PI_CONFIRM"] = confirm;
        var command = string.IsNullOrWhiteSpace(options.Provider.Command) ? "pi" : AiService.ExpandHome(options.Provider.Command);
        var fork = _forkPending;
        _process = new JsonLinesProcess("pi", command, Arguments(), options.WorkingDirectory, env, OnLine, OnExit);
        _forkPending = false; // a later restart continues the branch
        var state = await CommandAsync(new JsonObject { ["type"] = "get_state" }, TimeSpan.FromSeconds(60));
        if (state?["data"]?["sessionFile"]?.GetValue<string>() is { Length: > 0 } file) _sessionFile = file;
        if (fork && options.ForkAt is { Length: > 1 } at && at[0] == '#' && int.TryParse(at[1..], out var index))
        {
            // Branch at the index-th message of the copy: the message being sent again goes in its place.
            var list = await CommandAsync(new JsonObject { ["type"] = "get_fork_messages" }, TimeSpan.FromSeconds(10));
            if (list?["data"]?["messages"] is JsonArray messages && index < messages.Count && messages[index]?["entryId"]?.GetValue<string>() is { } entry)
                await CommandAsync(new JsonObject { ["type"] = "fork", ["entryId"] = entry }, TimeSpan.FromSeconds(10));
        }
        if (_sessionFile is { } f) Emit(new AiSessionStarted(f, _model));
    }

    private void OnExit(int code)
    {
        lock (_gate)
        {
            foreach (var id in _pending.Keys) Emit(new AiRequestClosed(id));
            _pending.Clear();
            foreach (var r in _responses.Values) r.TrySetResult(new JsonObject { ["success"] = false, ["error"] = "pi stopped." });
        }
        if (_turn is { Task.IsCompleted: false } turn)
        {
            if (!_aborting) Emit(new AiError(_process?.Errors is { Length: > 0 } e ? JsonLinesProcess.LastLines(e) : $"pi stopped (exit code {code})."));
            Emit(new AiTurnFinished(false, _aborting ? "stopped" : "exited"));
            turn.TrySetResult();
        }
        _aborting = false;
    }

    private async Task<JsonObject?> CommandAsync(JsonObject command, TimeSpan wait)
    {
        var id = "avamux-" + Interlocked.Increment(ref _next);
        command["id"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _responses[id] = tcs;
        try
        {
            await _process!.SendAsync(command);
            return await tcs.Task.WaitAsync(wait);
        }
        catch (TimeoutException) { return null; }
        finally { lock (_gate) _responses.Remove(id); }
    }

    public async Task SendAsync(AiUserMessage message, CancellationToken ct = default)
    {
        if (IsBusy) throw new InvalidOperationException("A reply is still being written.");
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;
        _thinkingShown = false; _text = false;
        try { await EnsureStartedAsync(); }
        catch (Exception ex)
        {
            Emit(new AiError(ex.Message));
            Emit(new AiTurnFinished(false, "not started"));
            turn.TrySetResult();
            return;
        }
        string? token = null;
        if (!options.Ephemeral)
        {
            var list = await CommandAsync(new JsonObject { ["type"] = "get_fork_messages" }, TimeSpan.FromSeconds(10));
            if (list?["data"]?["messages"] is JsonArray messages) token = "#" + messages.Count;
        }
        Emit(new AiTurnStarted(token));
        var prompt = new JsonObject { ["type"] = "prompt", ["message"] = AiService.WithAttachmentNotes(message, images: false) };
        var reply = await CommandAsync(prompt, TimeSpan.FromSeconds(60));
        if (reply?["success"]?.GetValue<bool>() == false)
        {
            Emit(new AiError(reply["error"]?.GetValue<string>() ?? "pi did not take the message."));
            Emit(new AiTurnFinished(false, "refused"));
            turn.TrySetResult();
        }
        using var reg = ct.Register(() => _ = AbortAsync());
        await turn.Task;
    }

    public async Task AbortAsync()
    {
        if (!IsBusy || _process is null) return;
        _aborting = true;
        List<string> open;
        lock (_gate) open = [.. _pending.Keys];
        foreach (var id in open) await AnswerAsync(id, new AiAnswer { Decision = AiDecision.Deny, Reason = "Stopped." });
        await CommandAsync(new JsonObject { ["type"] = "abort" }, TimeSpan.FromSeconds(5));
        var turn = _turn;
        if (turn is not null && await Task.WhenAny(turn.Task, Task.Delay(8000)) != turn.Task) _process.Kill();
    }

    public async Task SetModelAsync(string model)
    {
        _model = model;
        if (_process is not { HasExited: false } || string.IsNullOrWhiteSpace(model)) return;
        var list = await CommandAsync(new JsonObject { ["type"] = "get_available_models" }, TimeSpan.FromSeconds(10));
        var models = list?["data"]?["models"] as JsonArray ?? list?["data"] as JsonArray ?? [];
        foreach (var m in models.OfType<JsonObject>())
        {
            var provider = m["provider"]?.GetValue<string>() ?? "";
            var id = m["id"]?.GetValue<string>() ?? "";
            if ($"{provider}/{id}" != model && id != model) continue;
            await CommandAsync(new JsonObject { ["type"] = "set_model", ["provider"] = provider, ["modelId"] = id }, TimeSpan.FromSeconds(10));
            return;
        }
    }

    public async Task SetEffortAsync(string effort)
    {
        _effort = effort;
        if (_process is { HasExited: false } && !string.IsNullOrWhiteSpace(effort))
            await CommandAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = effort }, TimeSpan.FromSeconds(10));
    }

    public async Task AnswerAsync(string requestId, AiAnswer answer)
    {
        (string Method, string Kind, string? Tool) pending;
        lock (_gate) if (!_pending.Remove(requestId, out pending)) return;
        var response = new JsonObject { ["type"] = "extension_ui_response", ["id"] = requestId };
        var text = answer.Answers.Values.FirstOrDefault();
        if (pending.Kind == "permission")
        {
            if (answer.Decision == AiDecision.AllowForSession && pending.Tool is { } tool) lock (_gate) _allowedForSession.Add(tool);
            response["value"] = answer.Decision switch { AiDecision.Allow => "Allow", AiDecision.AllowForSession => "Allow for this session", _ => "Deny" };
        }
        else if (answer.Decision == AiDecision.Deny || text is null) response["cancelled"] = true;
        else if (pending.Method == "confirm") response["confirmed"] = text is "Yes" or "yes" or "true";
        else response["value"] = text;
        Emit(new AiRequestClosed(requestId));
        if (_process is { HasExited: false } p) await p.SendAsync(response);
    }

    // ---- what pi says ----

    private void OnLine(JsonObject o)
    {
        switch (o["type"]?.GetValue<string>())
        {
            case "response":
                if (o["id"]?.GetValue<string>() is { } id)
                {
                    TaskCompletionSource<JsonObject>? tcs;
                    lock (_gate) _responses.TryGetValue(id, out tcs);
                    tcs?.TrySetResult(o);
                }
                break;
            case "message_update":
                var e = o["assistantMessageEvent"] as JsonObject;
                switch (e?["type"]?.GetValue<string>())
                {
                    case "text_start":
                        if (_text) Emit(new AiTextDelta("\n\n"));
                        break;
                    case "text_delta":
                        if (e["delta"]?.GetValue<string>() is { Length: > 0 } t) { _text = true; Emit(new AiTextDelta(t)); }
                        break;
                    case "thinking_start":
                        if (!_thinkingShown) { _thinkingShown = true; Emit(new AiThinkingStarted()); }
                        break;
                    case "thinking_delta":
                        if (e["delta"]?.GetValue<string>() is { Length: > 0 } th) Emit(new AiThinkingDelta(th));
                        break;
                }
                break;
            case "message_end":
                if (o["message"] is JsonObject m && m["role"]?.GetValue<string>() == "assistant" && m["stopReason"]?.GetValue<string>() == "error" && !_aborting)
                    Emit(new AiError(m["errorMessage"]?.GetValue<string>() ?? "The model reported an error."));
                break;
            case "tool_execution_start":
                var name = o["toolName"]?.GetValue<string>() ?? "tool";
                var args = o["args"] as JsonObject ?? new JsonObject();
                if (name != "ask_user") Emit(new AiToolStarted(o["toolCallId"]?.GetValue<string>() ?? "", name, AiService.ToolSummary(name, args), args.ToJsonString(JsonUtil.Compact)));
                break;
            case "tool_execution_end":
                if (o["toolName"]?.GetValue<string>() == "ask_user") break;
                var content = o["result"]?["content"] as JsonArray;
                var output = content is null ? null : string.Join("\n", content.OfType<JsonObject>().Select(c => c["text"]?.GetValue<string>()).Where(x => x is not null));
                Emit(new AiToolFinished(o["toolCallId"]?.GetValue<string>() ?? "", output, o["isError"]?.GetValue<bool>() == true));
                break;
            case "auto_retry_start": Emit(new AiStatus("The model failed; trying again…")); break;
            case "compaction_start": Emit(new AiStatus("Compacting the conversation to fit the context…")); break;
            case "agent_end":
                if (o["willRetry"]?.GetValue<bool>() == true) break;
                _ = FinishTurnAsync();
                break;
            case "extension_ui_request": OnUiRequest(o); break;
            case "extension_error": Log.Warn("ai", "pi extension: " + o.ToJsonString()); break;
        }
    }

    private async Task FinishTurnAsync()
    {
        var turn = _turn;
        if (turn is null || turn.Task.IsCompleted) return;
        try
        {
            var stats = await CommandAsync(new JsonObject { ["type"] = "get_session_stats" }, TimeSpan.FromSeconds(5));
            if (stats?["data"] is JsonObject d)
            {
                long? L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : n is JsonValue v2 && v2.TryGetValue<double>(out var y) ? (long)y : null;
                double? cost = d["cost"] is JsonValue c && c.TryGetValue<double>(out var cv) ? cv : null;
                Emit(new AiUsage(L(d["contextUsage"]?["tokens"]), L(d["contextUsage"]?["contextWindow"]), L(d["tokens"]?["input"]), L(d["tokens"]?["output"]), cost));
            }
        }
        catch { }
        Emit(new AiTurnFinished(true, _aborting ? "stopped" : "end"));
        _aborting = false;
        turn.TrySetResult();
    }

    private void OnUiRequest(JsonObject o)
    {
        var id = o["id"]?.GetValue<string>() ?? "";
        var method = o["method"]?.GetValue<string>() ?? "";
        var title = o["title"]?.GetValue<string>() ?? "";
        switch (method)
        {
            case "notify":
                if (o["message"]?.GetValue<string>() is { Length: > 0 } note) Emit(new AiStatus(note));
                return;
            case not ("select" or "confirm" or "input" or "editor"):
                return; // status, widgets, titles: nothing to show here
        }
        if (title.StartsWith(PermissionMarker, StringComparison.Ordinal))
        {
            var payload = Parse(title[PermissionMarker.Length..]);
            var tool = payload?["tool"]?.GetValue<string>() ?? "tool";
            var input = payload?["input"] as JsonObject ?? new JsonObject();
            bool allowed;
            lock (_gate) { _pending[id] = (method, "permission", tool); allowed = _allowedForSession.Contains(tool); }
            if (allowed) { _ = AnswerAsync(id, new AiAnswer { Decision = AiDecision.Allow }); return; }
            Emit(new AiPermissionRequest(id, tool, AiService.ToolSummary(tool, input), input.ToJsonString(JsonUtil.Compact)));
            return;
        }
        string question;
        List<string> choices;
        var multi = false;
        if (title.StartsWith(QuestionMarker, StringComparison.Ordinal) && Parse(title[QuestionMarker.Length..]) is { } q)
        {
            question = q["question"]?.GetValue<string>() ?? "";
            choices = (q["options"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
            multi = q["multiSelect"]?.GetValue<bool>() == true;
        }
        else
        {
            question = title + (o["message"]?.GetValue<string>() is { Length: > 0 } msg ? "\n" + msg : "");
            choices = method switch
            {
                "select" => (o["options"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToList(),
                "confirm" => ["Yes", "No"],
                _ => [],
            };
        }
        lock (_gate) _pending[id] = (method, "question", null);
        Emit(new AiQuestionRequest(id, [new AiQuestion(question, null, choices.Select(c => new AiOption(c, null)).ToList(), multi)]));
    }

    private static JsonObject? Parse(string text) { try { return JsonNode.Parse(text) as JsonObject; } catch { return null; } }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        _process = null;
        if (p is not null) await p.DisposeAsync();
        _turn?.TrySetResult();
    }
}
