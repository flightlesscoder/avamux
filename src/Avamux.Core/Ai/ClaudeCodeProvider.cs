using System.Text.Json.Nodes;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Ai;

/// <summary>
/// Claude Code, run headless: <c>claude -p</c> with stream-json in and out, kept running for the whole conversation.
/// Permission prompts and AskUserQuestion come back over the same pipe (<c>--permission-prompt-tool stdio</c>), so
/// they are answered in avamux rather than refused.
/// </summary>
public sealed class ClaudeCodeProvider : IAiProvider
{
    public const string KindId = "claude-code";
    public string Kind => KindId;
    public string DisplayName => "Claude Code";
    public string Description => "Anthropic's Claude Code command line (claude), signed in with your account. Asks you before editing or running anything, unless set otherwise.";

    public AiCapabilities Capabilities { get; } = new()
    {
        Thinking = true, Images = true, Fork = true, Permissions = true, Questions = true, ContextUsage = true,
        EffortLevels = ["low", "medium", "high", "xhigh", "max"],
    };

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(AiProviderConfig config, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AiModel>>(
        [
            new("", "Default", Note: "Claude Code's own choice"),
            new("haiku", "Haiku", Thinking: true, Images: true, Note: "fastest"),
            new("sonnet", "Sonnet", Thinking: true, Images: true),
            new("opus", "Opus", Thinking: true, Images: true),
            new("fable", "Fable", Thinking: true, Images: true),
        ]);

    public IAiSession StartSession(AiSessionOptions options) => new ClaudeSession(options);
}

internal sealed class ClaudeSession(AiSessionOptions options) : IAiSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Tool, JsonObject Input)> _pending = [];
    private readonly Dictionary<string, TaskCompletionSource<JsonObject?>> _controls = [];
    private readonly HashSet<string> _allowedForSession = [];
    private readonly HashSet<int> _toolBlocks = [];
    private JsonLinesProcess? _process;
    private TaskCompletionSource? _turn;
    private string? _sessionId = options.Resume is { Length: > 0 } && !options.ForkOnResume ? options.Resume : null;
    private string? _resume = options.Resume;
    private bool _fork = options.ForkOnResume, _restart;
    private string? _forkAt = options.ForkAt;
    private string? _model = options.Model, _effort = options.Effort;
    private string? _lastAssistant;
    private bool _streamedText, _thinkingShown, _aborting;
    private long? _context;
    private int _controlId;

    public string? ProviderSessionId => _sessionId;
    public bool IsBusy => _turn is { Task.IsCompleted: false };
    public event Action<AiEvent>? Event;

    private void Emit(AiEvent e) { try { Event?.Invoke(e); } catch (Exception ex) { Log.Error("ai", "A chat event handler failed", ex); } }

    private List<string> Arguments()
    {
        var p = options.Provider;
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-prompt-tool", "stdio" };
        if (!string.IsNullOrWhiteSpace(_model)) args.AddRange(["--model", _model!]);
        if (!string.IsNullOrWhiteSpace(_effort)) args.AddRange(["--effort", _effort!]);
        if (_resume is { Length: > 0 } resume)
        {
            args.AddRange(["--resume", resume]);
            if (_fork) args.Add("--fork-session");
            if (_fork && _forkAt is { Length: > 0 } at) args.AddRange(["--resume-session-at", at]);
        }
        else
        {
            _sessionId ??= Guid.NewGuid().ToString();
            args.AddRange(["--session-id", _sessionId]);
        }
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt)) args.AddRange(["--append-system-prompt", options.SystemPrompt!]);
        if (options.NoTools) args.AddRange(["--tools", ""]);
        if (options.Ephemeral) args.Add("--no-session-persistence");
        if (p.FullAccess) args.Add("--dangerously-skip-permissions");
        else if (!string.IsNullOrWhiteSpace(p.PermissionMode)) args.AddRange(["--permission-mode", p.PermissionMode]);
        if (p.AllowedTools.Count > 0) args.AddRange(["--allowedTools", string.Join(",", p.AllowedTools)]);
        if (p.DisallowedTools.Count > 0) args.AddRange(["--disallowedTools", string.Join(",", p.DisallowedTools)]);
        foreach (var dir in p.ExtraFolders.Where(d => !string.IsNullOrWhiteSpace(d))) args.AddRange(["--add-dir", AiService.ExpandHome(dir)]);
        args.AddRange(p.Args);
        return args;
    }

    private async Task EnsureStartedAsync()
    {
        if (_process is { HasExited: false } && !_restart) return;
        if (_process is not null) { await _process.DisposeAsync(); _process = null; }
        if (_restart && _sessionId is { } id && !options.Ephemeral) { _resume = id; _fork = false; _forkAt = null; }
        _restart = false;
        var command = string.IsNullOrWhiteSpace(options.Provider.Command) ? "claude" : AiService.ExpandHome(options.Provider.Command);
        _process = new JsonLinesProcess("claude", command, Arguments(), options.WorkingDirectory, options.Provider.Env, OnLine, OnExit);
        // Once started, a later restart continues this conversation rather than branching again.
        _fork = false; _forkAt = null;
        await ControlAsync(new JsonObject { ["subtype"] = "initialize" }, TimeSpan.FromSeconds(30));
    }

    private void OnExit(int code)
    {
        TaskCompletionSource? turn;
        lock (_gate) { turn = _turn; foreach (var id in _pending.Keys) Emit(new AiRequestClosed(id)); _pending.Clear(); }
        if (turn is { Task.IsCompleted: false })
        {
            var why = _process?.Errors is { Length: > 0 } e ? JsonLinesProcess.LastLines(e) : $"Claude Code stopped (exit code {code}).";
            if (!_aborting) Emit(new AiError(why));
            Emit(new AiTurnFinished(false, _aborting ? "stopped" : "exited", _lastAssistant));
            turn.TrySetResult();
        }
        _aborting = false;
        if (_sessionId is not null) _restart = true; // the next message starts it again, continuing the conversation
    }

    private async Task<JsonObject?> ControlAsync(JsonObject request, TimeSpan wait)
    {
        var id = "avamux-" + Interlocked.Increment(ref _controlId);
        var tcs = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _controls[id] = tcs;
        await _process!.SendAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
        try { return await tcs.Task.WaitAsync(wait); }
        catch (TimeoutException) { return null; }
        finally { lock (_gate) _controls.Remove(id); }
    }

    public async Task SendAsync(AiUserMessage message, CancellationToken ct = default)
    {
        if (IsBusy) throw new InvalidOperationException("A reply is still being written.");
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _turn = turn;
        _streamedText = false; _thinkingShown = false;
        try { await EnsureStartedAsync(); }
        catch (Exception ex)
        {
            Emit(new AiError(ex.Message));
            Emit(new AiTurnFinished(false, "not started"));
            turn.TrySetResult();
            return;
        }
        Emit(new AiTurnStarted(_lastAssistant ?? (_resume is null ? "" : null)));
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = AiService.WithAttachmentNotes(message, images: true) } };
        foreach (var a in message.Attachments.Where(a => a.IsImage))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(a.Path, ct);
                if (bytes.Length > 5 * 1024 * 1024) continue; // too big to send as a picture: it is named in the text instead
                content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = a.MimeType, ["data"] = Convert.ToBase64String(bytes) } });
            }
            catch (Exception ex) { Log.Warn("ai", $"Could not attach {a.Path}: {ex.Message}"); }
        }
        await _process!.SendAsync(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } });
        using var reg = ct.Register(() => _ = AbortAsync());
        await turn.Task;
    }

    public async Task AbortAsync()
    {
        if (!IsBusy || _process is null) return;
        _aborting = true;
        List<string> open;
        lock (_gate) { open = [.. _pending.Keys]; }
        foreach (var id in open) await AnswerAsync(id, new AiAnswer { Decision = AiDecision.Deny, Reason = "The user stopped the reply." });
        var reply = await ControlAsync(new JsonObject { ["subtype"] = "interrupt" }, TimeSpan.FromSeconds(5));
        // No answer, or still writing after a while: end it; the next message continues the conversation.
        var turn = _turn;
        if (turn is not null && await Task.WhenAny(turn.Task, Task.Delay(reply is null ? 0 : 8000)) != turn.Task) _process.Kill();
    }

    public async Task SetModelAsync(string model)
    {
        _model = model;
        if (_process is { HasExited: false } && !IsBusy)
        {
            var reply = await ControlAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = string.IsNullOrWhiteSpace(model) ? null : model }, TimeSpan.FromSeconds(10));
            if (reply is null || reply["subtype"]?.GetValue<string>() == "error") _restart = true;
        }
    }

    public Task SetEffortAsync(string effort)
    {
        if (_effort == effort) return Task.CompletedTask;
        _effort = effort;
        if (_process is not null) _restart = true; // effort is chosen at start: the next message restarts, continuing the conversation
        return Task.CompletedTask;
    }

    public async Task AnswerAsync(string requestId, AiAnswer answer)
    {
        (string Tool, JsonObject Input) pending;
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out pending)) return;
        }
        JsonObject response;
        if (pending.Tool == "AskUserQuestion")
        {
            var input = (JsonObject)pending.Input.DeepClone();
            if (answer.Decision == AiDecision.Deny) response = new JsonObject { ["behavior"] = "deny", ["message"] = answer.Reason ?? "The user did not answer." };
            else
            {
                var answers = new JsonObject();
                foreach (var (q, a) in answer.Answers) answers[q] = a;
                input["answers"] = answers;
                response = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input };
            }
        }
        else if (answer.Decision == AiDecision.Deny)
            response = new JsonObject { ["behavior"] = "deny", ["message"] = string.IsNullOrWhiteSpace(answer.Reason) ? "The user declined this." : answer.Reason };
        else
        {
            if (answer.Decision == AiDecision.AllowForSession) lock (_gate) _allowedForSession.Add(pending.Tool);
            response = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = pending.Input.DeepClone() };
        }
        Emit(new AiRequestClosed(requestId));
        if (_process is { HasExited: false } p)
            await p.SendAsync(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response } });
    }

    // ---- what Claude Code says ----

    private void OnLine(JsonObject o)
    {
        switch (o["type"]?.GetValue<string>())
        {
            case "system": OnSystem(o); break;
            case "stream_event": if (o["parent_tool_use_id"] is null) OnStream(o["event"] as JsonObject); break;
            case "assistant": OnAssistant(o); break;
            case "user": OnUser(o); break;
            case "result": OnResult(o); break;
            case "control_request": OnControlRequest(o); break;
            case "control_cancel_request":
                if (o["request_id"]?.GetValue<string>() is { } cancelled) { lock (_gate) _pending.Remove(cancelled); Emit(new AiRequestClosed(cancelled)); }
                break;
            case "control_response":
                var r = o["response"] as JsonObject;
                if (r?["request_id"]?.GetValue<string>() is { } rid)
                {
                    TaskCompletionSource<JsonObject?>? tcs;
                    lock (_gate) _controls.TryGetValue(rid, out tcs);
                    tcs?.TrySetResult(r);
                }
                break;
        }
    }

    private void OnSystem(JsonObject o)
    {
        switch (o["subtype"]?.GetValue<string>())
        {
            case "init":
                if (o["session_id"]?.GetValue<string>() is { } id)
                {
                    _sessionId = id;
                    Emit(new AiSessionStarted(id, o["model"]?.GetValue<string>()));
                }
                break;
            case "thinking_tokens":
                if (!_thinkingShown) { _thinkingShown = true; Emit(new AiThinkingStarted()); }
                break;
            case "compact_boundary": Emit(new AiStatus("The conversation was compacted to fit the context.")); break;
            case "status" when o["status"]?.GetValue<string>() is { Length: > 0 } status: Emit(new AiStatus(status)); break;
        }
    }

    private void OnStream(JsonObject? e)
    {
        if (e is null) return;
        switch (e["type"]?.GetValue<string>())
        {
            case "message_start":
                _toolBlocks.Clear();
                break;
            case "content_block_start":
                var kind = e["content_block"]?["type"]?.GetValue<string>();
                if (kind is "thinking" or "redacted_thinking" && !_thinkingShown) { _thinkingShown = true; Emit(new AiThinkingStarted()); }
                if (kind == "text" && _streamedText) Emit(new AiTextDelta("\n\n")); // a new text block after a tool: keep paragraphs apart
                break;
            case "content_block_delta":
                var delta = e["delta"] as JsonObject;
                switch (delta?["type"]?.GetValue<string>())
                {
                    case "text_delta":
                        if (delta["text"]?.GetValue<string>() is { Length: > 0 } text) { _streamedText = true; Emit(new AiTextDelta(text)); }
                        break;
                    case "thinking_delta":
                        if (delta["thinking"]?.GetValue<string>() is { Length: > 0 } thought) Emit(new AiThinkingDelta(thought));
                        break;
                }
                break;
        }
    }

    private void OnAssistant(JsonObject o)
    {
        if (o["parent_tool_use_id"] is not null) return; // a subagent's
        if (o["uuid"]?.GetValue<string>() is { } uuid) _lastAssistant = uuid;
        var message = o["message"] as JsonObject;
        if (message?["content"] is JsonArray content)
            foreach (var block in content.OfType<JsonObject>())
            {
                switch (block["type"]?.GetValue<string>())
                {
                    case "tool_use":
                        var input = block["input"] as JsonObject ?? new JsonObject();
                        var name = block["name"]?.GetValue<string>() ?? "tool";
                        Emit(new AiToolStarted(block["id"]?.GetValue<string>() ?? "", name, AiService.ToolSummary(name, input), input.ToJsonString(JsonUtil.Compact)));
                        break;
                    case "text" when !_streamedText:
                        // Without partial messages (older versions), the text comes whole.
                        if (block["text"]?.GetValue<string>() is { Length: > 0 } text) Emit(new AiTextDelta(text));
                        break;
                }
            }
        if (message?["usage"] is JsonObject usage)
        {
            long N(string key) => usage[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            _context = N("input_tokens") + N("cache_read_input_tokens") + N("cache_creation_input_tokens") + N("output_tokens");
            Emit(new AiUsage(_context, null, null, null, null));
        }
    }

    private void OnUser(JsonObject o)
    {
        if (o["parent_tool_use_id"] is not null) return;
        if (o["message"]?["content"] is not JsonArray content) return;
        foreach (var block in content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_result"))
        {
            var output = block["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray a => string.Join("\n", a.OfType<JsonObject>().Select(x => x["text"]?.GetValue<string>()).Where(x => x is not null)),
                _ => null,
            };
            Emit(new AiToolFinished(block["tool_use_id"]?.GetValue<string>() ?? "", output, block["is_error"]?.GetValue<bool>() == true));
        }
    }

    private void OnResult(JsonObject o)
    {
        var subtype = o["subtype"]?.GetValue<string>() ?? "";
        var isError = o["is_error"]?.GetValue<bool>() == true || subtype.StartsWith("error", StringComparison.Ordinal);
        long? window = null;
        if (o["modelUsage"] is JsonObject models)
            foreach (var (_, m) in models)
                if (m?["contextWindow"] is JsonValue cw && cw.TryGetValue<long>(out var w)) window = Math.Max(window ?? 0, w);
        double? cost = o["total_cost_usd"] is JsonValue c && c.TryGetValue<double>(out var cv) ? cv : null;
        var usage = o["usage"] as JsonObject;
        long? Tokens(string key) => usage?[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
        Emit(new AiUsage(_context, window, Tokens("input_tokens"), Tokens("output_tokens"), cost));
        if (isError && !_aborting)
        {
            var message = o["result"]?.GetValue<string>() is { Length: > 0 } r ? r
                : o["errors"] is JsonArray errs && errs.Count > 0 ? string.Join("; ", errs.Select(x => x?.ToString()))
                : subtype.Replace('_', ' ');
            Emit(new AiError(message));
        }
        Emit(new AiTurnFinished(!isError || _aborting, _aborting ? "stopped" : subtype, _lastAssistant));
        _aborting = false;
        TaskCompletionSource? turn;
        lock (_gate) turn = _turn;
        turn?.TrySetResult();
    }

    private void OnControlRequest(JsonObject o)
    {
        var id = o["request_id"]?.GetValue<string>() ?? "";
        var request = o["request"] as JsonObject;
        if (request?["subtype"]?.GetValue<string>() != "can_use_tool")
        {
            // Nothing else is expected of avamux; say so rather than leave Claude Code waiting.
            _ = _process?.SendAsync(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = id, ["error"] = "Not supported by avamux." } });
            return;
        }
        var tool = request["tool_name"]?.GetValue<string>() ?? "tool";
        var input = request["input"] as JsonObject ?? new JsonObject();
        bool allowed;
        lock (_gate)
        {
            _pending[id] = (tool, input);
            allowed = tool != "AskUserQuestion" && _allowedForSession.Contains(tool);
        }
        if (allowed) { _ = AnswerAsync(id, new AiAnswer { Decision = AiDecision.Allow }); return; }
        if (tool == "AskUserQuestion")
        {
            var questions = (input["questions"] as JsonArray ?? []).OfType<JsonObject>().Select(q => new AiQuestion(
                q["question"]?.GetValue<string>() ?? "",
                q["header"]?.GetValue<string>(),
                (q["options"] as JsonArray ?? []).OfType<JsonObject>().Select(x => new AiOption(x["label"]?.GetValue<string>() ?? "", x["description"]?.GetValue<string>())).ToList(),
                q["multiSelect"]?.GetValue<bool>() == true)).ToList();
            Emit(new AiQuestionRequest(id, questions));
        }
        else Emit(new AiPermissionRequest(id, tool, AiService.ToolSummary(tool, input), input.ToJsonString(JsonUtil.Compact)));
    }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        _process = null;
        if (p is not null) await p.DisposeAsync();
        lock (_gate) _turn?.TrySetResult();
    }
}
