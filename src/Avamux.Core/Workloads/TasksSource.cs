using Avamux.Plugins;

namespace Avamux.Core.Workloads;

/// <summary>avamux's own AI work: chats writing a reply, and one-off runs (AI Actions, <c>avamux ai run</c>), with what they wrote and thought so far.</summary>
internal sealed class TasksSource(AppServices app) : IWorkloadSource
{
    private const string ChatPrefix = "chat:", RunPrefix = "run:";

    public string Id => "avamux";
    public string Name => "avamux";
    public string Machine => "This computer";
    public event Action? Changed { add { } remove { } }

    public Task<WorkloadSnapshot> ListAsync(CancellationToken ct = default)
    {
        var items = new List<WorkloadInfo>();
        foreach (var b in app.Ai.Chats.Busy())
        {
            var provider = app.Ai.Config(b.Chat.ProviderId);
            var writing = b.Writing;
            string detail = b.Waiting > 0 ? "Waiting for your answer in AI Chat"
                : writing?.IsThinking == true ? "Thinking" : writing is { Text.Length: > 0 } ? $"Writing ({writing.Text.Length:N0} characters)" : "Starting";
            if (writing?.Tools.LastOrDefault(t => !t.Done) is { } tool) detail = "Using " + tool.Summary;
            items.Add(new WorkloadInfo
            {
                Id = ChatPrefix + b.Chat.Id, Kind = WorkloadKind.Task, Name = "Chat: " + b.Chat.Title, Type = "AI Chat reply",
                State = b.Waiting > 0 ? WorkloadState.Waiting : WorkloadState.Busy, Detail = detail, Model = b.Chat.Model.Length > 0 ? b.Chat.Model : provider?.Model,
                Started = writing?.Time, StartedBy = "you (AI Chat)", Folder = b.Chat.Folder.Length > 0 ? b.Chat.Folder : null,
                Actions = [new(WorkloadActions.Cancel, "Stop"), new("open", "Open in AI Chat")],
                Facts = new Dictionary<string, string> { ["Provider"] = provider?.Name ?? b.Chat.ProviderId },
            });
        }
        foreach (var r in app.Ai.Runs)
        {
            var provider = app.Ai.Config(r.ProviderId);
            var state = r.Finished is null ? WorkloadState.Busy : r.Ok ? WorkloadState.Done : r.Error == "Stopped from AI Workloads." ? WorkloadState.Cancelled : WorkloadState.Failed;
            int text, thinking;
            lock (r.Text) text = r.Text.Length;
            lock (r.Thinking) thinking = r.Thinking.Length;
            items.Add(new WorkloadInfo
            {
                Id = RunPrefix + r.Id, Kind = WorkloadKind.Task, Name = r.Label, Type = "AI run", State = state, Model = r.Model ?? provider?.Model,
                Detail = r.Finished is not null ? null : r.Tool is { } t ? "Using " + t : text > 0 ? $"Writing ({text:N0} characters)" : thinking > 0 ? "Thinking" : "Starting",
                Error = state == WorkloadState.Failed ? r.Error : null, Started = r.Started, Finished = r.Finished, StartedBy = "avamux (AI Actions or the CLI)",
                Actions = r.Finished is null ? [new(WorkloadActions.Cancel, "Stop")] : [],
                Facts = new Dictionary<string, string> { ["Provider"] = provider?.Name ?? r.ProviderId },
            });
        }
        return Task.FromResult(new WorkloadSnapshot(items));
    }

    public async Task<string> ActAsync(string workloadId, string action, CancellationToken ct = default)
    {
        if (workloadId.StartsWith(ChatPrefix, StringComparison.Ordinal))
        {
            var chat = workloadId[ChatPrefix.Length..];
            if (action == "open")
            {
                if (app.Ai.ChatHost is null) throw new InvalidOperationException("There is no window to show it in.");
                await app.Ai.ChatHost.ShowChatAsync(chat);
                return "Opened.";
            }
            await app.Ai.Chats.StopAsync(chat);
            return "Stopped.";
        }
        if (workloadId.StartsWith(RunPrefix, StringComparison.Ordinal))
            return app.Ai.CancelRun(workloadId[RunPrefix.Length..]) ? "Stopping." : "It had already finished.";
        throw new InvalidOperationException($"Cannot {action} {workloadId}.");
    }

    public Task<string> LogAsync(string workloadId, int lines = 200, CancellationToken ct = default)
    {
        string text = "";
        if (workloadId.StartsWith(RunPrefix, StringComparison.Ordinal) && app.Ai.Runs.FirstOrDefault(r => r.Id == workloadId[RunPrefix.Length..]) is { } r)
        {
            string thinking, reply;
            lock (r.Thinking) thinking = r.Thinking.ToString();
            lock (r.Text) reply = r.Text.ToString();
            text = (thinking.Length > 0 ? "──── thinking ────\n" + thinking.Trim() + "\n\n" : "") + "──── reply ────\n" + reply.Trim() + (r.Error is { } e ? "\n\n──── error ────\n" + e : "");
        }
        else if (workloadId.StartsWith(ChatPrefix, StringComparison.Ordinal) && app.Ai.Chats.Busy().FirstOrDefault(b => b.Chat.Id == workloadId[ChatPrefix.Length..]) is { Writing: { } w })
        {
            var tools = string.Join("\n", w.Tools.Select(t => (t.Done ? "✓ " : "… ") + t.Summary));
            text = (w.Thinking is { Length: > 0 } th ? "──── thinking ────\n" + th.Trim() + "\n\n" : "") + (tools.Length > 0 ? "──── tools ────\n" + tools + "\n\n" : "") + "──── reply so far ────\n" + w.Text.Trim();
        }
        var all = text.Split('\n');
        return Task.FromResult(string.Join('\n', all.TakeLast(Math.Max(lines, 1))));
    }
}
