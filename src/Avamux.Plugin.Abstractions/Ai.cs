namespace Avamux.Plugins;

/// <summary>
/// A kind of AI provider: something that runs conversations with a model, such as a coding agent's command line
/// (Claude Code, pi). avamux has some built in; a plugin adds more with <see cref="IPluginContext"/> registrations
/// (see <c>IPluginContext.AddAiProvider</c>). The user configures instances of a kind under Settings → AI Chat
/// (<see cref="AiProviderConfig"/>); each conversation is an <see cref="IAiSession"/>.
/// </summary>
public interface IAiProvider
{
    /// <summary>The kind's id as stored in settings ("claude-code", "pi", "command" …).</summary>
    string Kind { get; }
    string DisplayName { get; }
    /// <summary>A line for Settings → AI Chat.</summary>
    string Description { get; }
    AiCapabilities Capabilities { get; }
    /// <summary>The models an instance offers. May be a fixed list, or asked of the tool; never throws for an empty list.</summary>
    Task<IReadOnlyList<AiModel>> ListModelsAsync(AiProviderConfig config, CancellationToken ct = default);
    /// <summary>Starts a conversation. Nothing runs until the first <see cref="IAiSession.SendAsync"/>.</summary>
    IAiSession StartSession(AiSessionOptions options);
}

public sealed class AiCapabilities
{
    public bool Thinking { get; init; }
    public bool Images { get; init; }
    /// <summary>Can branch a conversation at an earlier message (for editing a message and re-running).</summary>
    public bool Fork { get; init; }
    /// <summary>Asks before using tools, through <see cref="AiPermissionRequest"/>.</summary>
    public bool Permissions { get; init; }
    /// <summary>Can ask the user questions, through <see cref="AiQuestionRequest"/>.</summary>
    public bool Questions { get; init; }
    public bool ContextUsage { get; init; }
    /// <summary>
    /// avamux holds the conversation and gives the model all of it each turn, so a reply edited in AI Chat is what the
    /// model sees next. Off for agents that keep their own copy (pi, Claude Code): there an edited reply would only
    /// change avamux's copy, so replies are not editable (editing the user's message branches instead).
    /// </summary>
    public bool EditReplies { get; init; }
    /// <summary>Effort or thinking levels, lowest first; empty when there is no such choice.</summary>
    public IReadOnlyList<string> EffortLevels { get; init; } = [];
}

public sealed record AiModel(string Id, string Name, int? ContextWindow = null, bool Thinking = false, bool Images = false, string? Note = null);

/// <summary>One configured provider (settings key <c>ai.providers</c>). Permission settings are all off unless set.</summary>
public sealed class AiProviderConfig
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>The program to run; empty uses the kind's usual name ("claude", "pi") from PATH.</summary>
    public string Command { get; set; } = "";
    /// <summary>Extra arguments, added after avamux's own.</summary>
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    /// <summary>Skip every permission prompt (Claude Code: --dangerously-skip-permissions).</summary>
    public bool FullAccess { get; set; }
    /// <summary>Claude Code's --permission-mode (default, acceptEdits, plan …); empty leaves the tool's own default.</summary>
    public string PermissionMode { get; set; } = "";
    public List<string> AllowedTools { get; set; } = [];
    public List<string> DisallowedTools { get; set; } = [];
    /// <summary>Folders the agent may use besides its working folder (Claude Code: --add-dir).</summary>
    public List<string> ExtraFolders { get; set; } = [];
    /// <summary>Settings of the kind's own ("template", "output" for the command kind …).</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    public string Option(string key, string fallback = "") => Options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    public AiProviderConfig Clone() => new()
    {
        Id = Id, Kind = Kind, Name = Name, Enabled = Enabled, Command = Command, Args = [.. Args], Env = new(Env), Model = Model, Effort = Effort,
        FullAccess = FullAccess, PermissionMode = PermissionMode, AllowedTools = [.. AllowedTools], DisallowedTools = [.. DisallowedTools],
        ExtraFolders = [.. ExtraFolders], Options = new(Options),
    };
}

public sealed class AiSessionOptions
{
    public required AiProviderConfig Provider { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public required string WorkingDirectory { get; init; }
    /// <summary>Added to the provider's own system prompt.</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>The provider's id for an earlier conversation to continue.</summary>
    public string? Resume { get; init; }
    /// <summary>With <see cref="Resume"/>: continue as a new branch, leaving the original as it was.</summary>
    public bool ForkOnResume { get; init; }
    /// <summary>With <see cref="Resume"/> and <see cref="ForkOnResume"/>: branch where <see cref="AiTurnStarted.ForkToken"/> of the message being sent again said.</summary>
    public string? ForkAt { get; init; }
    /// <summary>Only text: no tools (for actions whose result avamux applies itself).</summary>
    public bool NoTools { get; init; }
    /// <summary>Keep nothing of the conversation on the provider's side.</summary>
    public bool Ephemeral { get; init; }
    /// <summary>A folder avamux keeps for this conversation (attachments, the provider's own session files).</summary>
    public string? StateDirectory { get; init; }
}

/// <summary>A conversation. Events arrive on a background thread.</summary>
public interface IAiSession : IAsyncDisposable
{
    /// <summary>The provider's id for the conversation, once it has one (after the first message).</summary>
    string? ProviderSessionId { get; }
    bool IsBusy { get; }
    event Action<AiEvent>? Event;
    /// <summary>Sends a message and completes when the reply is finished (or failed, or was stopped).</summary>
    Task SendAsync(AiUserMessage message, CancellationToken ct = default);
    /// <summary>Stops the reply being written.</summary>
    Task AbortAsync();
    Task SetModelAsync(string model);
    Task SetEffortAsync(string effort);
    /// <summary>Answers a <see cref="AiPermissionRequest"/> or <see cref="AiQuestionRequest"/>.</summary>
    Task AnswerAsync(string requestId, AiAnswer answer);
}

public sealed class AiUserMessage
{
    public string Text { get; init; } = "";
    public List<AiAttachment> Attachments { get; init; } = [];
}

/// <param name="Path">A file avamux saved (in the conversation's folder).</param>
public sealed record AiAttachment(string Path, string MimeType, string Name)
{
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public enum AiDecision { Allow, AllowForSession, Deny }

public sealed class AiAnswer
{
    public AiDecision Decision { get; init; } = AiDecision.Allow;
    /// <summary>Why it was denied, passed on to the model.</summary>
    public string? Reason { get; init; }
    /// <summary>For questions: each question's text and the answer (labels joined with ", " for several).</summary>
    public Dictionary<string, string> Answers { get; init; } = [];
}

// ---- events ----

public abstract record AiEvent;
/// <summary>The provider's id for the conversation is known.</summary>
public sealed record AiSessionStarted(string ProviderSessionId, string? Model) : AiEvent;
/// <param name="ForkToken">
/// Where to branch from to send this message again differently (for <see cref="AiSessionOptions.ForkAt"/>); empty when
/// it is the first message, null when the provider cannot branch. What it holds is the provider's own business.
/// </param>
public sealed record AiTurnStarted(string? ForkToken) : AiEvent;
/// <summary>Text of the reply, as it streams.</summary>
public sealed record AiTextDelta(string Text) : AiEvent;
/// <summary>The model's thinking, for providers that show it.</summary>
public sealed record AiThinkingDelta(string Text) : AiEvent;
/// <summary>The model started thinking (also for providers that hide what it thinks).</summary>
public sealed record AiThinkingStarted : AiEvent;
public sealed record AiToolStarted(string Id, string Name, string Summary, string? InputJson) : AiEvent;
public sealed record AiToolFinished(string Id, string? Output, bool IsError) : AiEvent;
/// <param name="ContextTokens">How much of the context window the conversation fills now.</param>
public sealed record AiUsage(long? ContextTokens, long? ContextWindow, long? InputTokens, long? OutputTokens, double? CostUsd) : AiEvent;
public sealed record AiPermissionRequest(string Id, string Tool, string Summary, string? InputJson) : AiEvent;
public sealed record AiQuestionRequest(string Id, IReadOnlyList<AiQuestion> Questions) : AiEvent;
/// <summary>The request was answered elsewhere or withdrawn (the turn was stopped).</summary>
public sealed record AiRequestClosed(string Id) : AiEvent;
public sealed record AiStatus(string Text) : AiEvent;
public sealed record AiError(string Message) : AiEvent;
/// <param name="ProviderMessageId">The provider's id for the reply, for branching later (when it has one).</param>
public sealed record AiTurnFinished(bool Ok, string? StopReason, string? ProviderMessageId = null) : AiEvent;

public sealed record AiQuestion(string Question, string? Header, IReadOnlyList<AiOption> Options, bool MultiSelect);
public sealed record AiOption(string Label, string? Description);

// ---- what plugins use ----

public sealed record AiProviderInfo(string Id, string Name, string Kind, string Model, string Effort, AiCapabilities Capabilities);

public sealed class AiRunRequest
{
    public required string Prompt { get; init; }
    /// <summary>What the run is for, shown on the AI Workloads page ("Summarize"); null shows the prompt's start.</summary>
    public string? Label { get; init; }
    /// <summary>A configured provider's id; null uses the AI Actions default.</summary>
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    /// <summary>Null is the wiki folder.</summary>
    public string? WorkingDirectory { get; init; }
    public string? SystemPrompt { get; init; }
    /// <summary>Let the agent use its tools (read files, run commands). Off: text in, text out.</summary>
    public bool AllowTools { get; init; }
    public List<AiAttachment> Attachments { get; init; } = [];
}

public sealed class AiRunResult
{
    public bool Ok { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public string ProviderId { get; init; } = "";
    public string? Model { get; init; }
    public string? Effort { get; init; }
    /// <summary>The provider's id for the conversation, to continue it in AI Chat.</summary>
    public string? ProviderSessionId { get; init; }
    public string? WorkingDirectory { get; init; }
}

public sealed class AiChatRequest
{
    public string? Title { get; init; }
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? SystemPrompt { get; init; }
    /// <summary>Text put in the message box, not sent.</summary>
    public string? Draft { get; init; }
    /// <summary>A message sent straight away.</summary>
    public string? Send { get; init; }
    /// <summary>An exchange that already happened (role "user" or "assistant", text), shown at the top.</summary>
    public List<(string Role, string Text)> History { get; init; } = [];
    /// <summary>With <see cref="History"/>: the provider's id for that exchange, so the chat continues it.</summary>
    public string? Resume { get; init; }
}

/// <summary>avamux's AI providers, for plugins.</summary>
public interface IAiService
{
    /// <summary>The configured, switched-on providers.</summary>
    IReadOnlyList<AiProviderInfo> Providers { get; }
    /// <summary>The provider AI Actions use unless told otherwise.</summary>
    string? DefaultProviderId { get; }
    /// <summary>Runs one prompt to the end. <paramref name="onEvent"/> sees the reply stream in (on a background thread).</summary>
    Task<AiRunResult> RunAsync(AiRunRequest request, Action<AiEvent>? onEvent = null, CancellationToken ct = default);
    /// <summary>Opens AI Chat on a new conversation; returns its id. Call from the UI thread.</summary>
    Task<string> OpenChatAsync(AiChatRequest request);
}
