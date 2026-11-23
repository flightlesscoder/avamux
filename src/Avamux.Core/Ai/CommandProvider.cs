using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Ai;

/// <summary>
/// Any other coding agent with a one-shot command line (best effort; these are not tested here). Each message runs the
/// configured template once, with the conversation so far given in the prompt, and reads the reply from stdout: plain
/// text, or JSON lines whose text is picked out of the usual shapes. Presets cover Codex, Gemini CLI, opencode and aider.
/// </summary>
public sealed class CommandProvider : IAiProvider
{
    public const string KindId = "command";
    public string Kind => KindId;
    public string DisplayName => "Other command line";
    public string Description => "Another coding agent's one-shot command line (Codex, Gemini CLI, opencode, aider …): best effort, not tested by avamux.";
    public AiCapabilities Capabilities { get; } = new();

    /// <summary>Ready-made templates: {prompt} is the message, {model} the model (dropped with its flag when empty).</summary>
    public static readonly IReadOnlyList<(string Name, string Command, string Template, string Output)> Presets =
    [
        ("Codex", "codex", "exec --json --skip-git-repo-check [--model {model}] {prompt}", "jsonl"),
        ("Gemini CLI", "gemini", "-p {prompt} --output-format stream-json [--model {model}]", "jsonl"),
        ("opencode", "opencode", "run [--model {model}] {prompt}", "text"),
        ("aider", "aider", "--message {prompt} --no-stream --yes-always [--model {model}]", "text"),
    ];

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(AiProviderConfig config, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AiModel>>([new("", "Default")]);

    public IAiSession StartSession(AiSessionOptions options) => new CommandSession(options);

    /// <summary>
    /// The arguments of a template: words split on spaces (quotes keep spaces), {prompt}/{model}/{cwd} put in as
    /// whole arguments (never through a shell), and a [bracketed group] dropped when a placeholder in it is empty.
    /// </summary>
    public static List<string> Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = new List<string>();
        var group = new List<string>();
        var inGroup = false;
        var groupEmpty = false;
        foreach (var raw in Words(template))
        {
            var word = raw;
            var opens = word.StartsWith('[');
            if (opens) { inGroup = true; groupEmpty = false; group.Clear(); word = word[1..]; }
            var closes = inGroup && word.EndsWith(']');
            if (closes) word = word[..^1];
            foreach (var (k, v) in values)
            {
                if (!word.Contains("{" + k + "}")) continue;
                if (string.IsNullOrEmpty(v)) groupEmpty = true;
                word = word.Replace("{" + k + "}", v);
            }
            if (inGroup) { if (word.Length > 0) group.Add(word); }
            else if (word.Length > 0) result.Add(word);
            if (closes) { if (!groupEmpty) result.AddRange(group); inGroup = false; }
        }
        return result;
    }

    private static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is { } q) { if (c == q) quote = null; else sb.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>The reply text in a JSON line of the common agent formats, or null.</summary>
    public static string? TextOf(JsonObject o)
    {
        // Codex: {"type":"item.completed","item":{"type":"agent_message","text":"…"}}
        if (o["item"] is JsonObject item && item["type"]?.GetValue<string>() is "agent_message" or "assistant_message" && item["text"]?.GetValue<string>() is { } t1) return t1;
        // Gemini CLI stream-json: {"type":"message","role":"assistant","content":"…","delta":true}
        if (o["role"]?.GetValue<string>() == "assistant" && o["content"] is JsonValue c && c.TryGetValue<string>(out var t2)) return t2;
        // Generic shapes: {"delta":"…"}, {"text":"…"}, {"result":"…"}
        foreach (var key in new[] { "delta", "text", "result" })
            if (o[key] is JsonValue v && v.TryGetValue<string>(out var t3) && o["type"]?.GetValue<string>() is not ("user" or "system")) return t3;
        return null;
    }
}

internal sealed class CommandSession(AiSessionOptions options) : IAiSession
{
    private readonly List<(string Role, string Text)> _history = [];
    private Process? _running;
    private TaskCompletionSource? _turn;
    private string? _model = options.Model;

    public string? ProviderSessionId => null;
    public bool IsBusy => _turn is { Task.IsCompleted: false };
    public event Action<AiEvent>? Event;
    private void Emit(AiEvent e) { try { Event?.Invoke(e); } catch (Exception ex) { Log.Error("ai", "A chat event handler failed", ex); } }

    public async Task SendAsync(AiUserMessage message, CancellationToken ct = default)
    {
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;
        Emit(new AiTurnStarted(null));
        var text = AiService.WithAttachmentNotes(message, images: false);
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt)) prompt.AppendLine(options.SystemPrompt).AppendLine();
        if (_history.Count > 0)
        {
            prompt.AppendLine("The conversation so far:");
            foreach (var (role, t) in _history) prompt.AppendLine($"{(role == "user" ? "User" : "You")}: {t}").AppendLine();
            prompt.AppendLine("The user's new message:");
        }
        prompt.Append(text);
        var p = options.Provider;
        var template = p.Option("template", "{prompt}");
        var args = CommandProvider.Expand(template, new Dictionary<string, string> { ["prompt"] = prompt.ToString(), ["model"] = _model ?? "", ["cwd"] = options.WorkingDirectory });
        args.AddRange(p.Args);
        var jsonl = p.Option("output", "text") == "jsonl";
        var reply = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(AiService.ExpandHome(string.IsNullOrWhiteSpace(p.Command) ? "sh" : p.Command))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
                WorkingDirectory = Directory.Exists(options.WorkingDirectory) ? options.WorkingDirectory : Environment.CurrentDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in p.Env) psi.Environment[k] = v;
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
            _running = proc;
            proc.StandardInput.Close();
            var errors = proc.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                string? piece = line + "\n";
                if (jsonl)
                {
                    piece = null;
                    try { if (JsonNode.Parse(line) is JsonObject o) piece = CommandProvider.TextOf(o); } catch { piece = line + "\n"; }
                }
                if (string.IsNullOrEmpty(piece)) continue;
                reply.Append(piece);
                Emit(new AiTextDelta(piece));
            }
            await proc.WaitForExitAsync(ct);
            var err = await errors;
            if (proc.ExitCode != 0) Emit(new AiError(JsonLinesProcess.LastLines(err.Length > 0 ? err : $"exit code {proc.ExitCode}")));
            _history.Add(("user", text));
            _history.Add(("assistant", reply.ToString().Trim()));
            Emit(new AiTurnFinished(proc.ExitCode == 0, "end"));
        }
        catch (Exception ex)
        {
            Emit(new AiError(ex is OperationCanceledException ? "Stopped." : ex.Message));
            Emit(new AiTurnFinished(false, ex is OperationCanceledException ? "stopped" : "failed"));
        }
        finally { _running = null; turn.TrySetResult(); }
    }

    public Task AbortAsync() { try { _running?.Kill(true); } catch { } return Task.CompletedTask; }
    public Task SetModelAsync(string model) { _model = model; return Task.CompletedTask; }
    public Task SetEffortAsync(string effort) => Task.CompletedTask;
    public Task AnswerAsync(string requestId, AiAnswer answer) => Task.CompletedTask;
    public ValueTask DisposeAsync() { _ = AbortAsync(); return ValueTask.CompletedTask; }
}
