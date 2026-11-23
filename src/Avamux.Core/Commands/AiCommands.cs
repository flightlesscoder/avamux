using System.Text.Json.Nodes;
using Avamux.Core.Ai;
using Avamux.Plugins;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private void AddAiCommands(AppServices s)
    {
        JsonObject Provider(AiProviderConfig c) => new()
        {
            ["id"] = c.Id, ["name"] = c.Name, ["kind"] = c.Kind, ["enabled"] = c.Enabled, ["command"] = c.Command, ["model"] = c.Model, ["effort"] = c.Effort,
            ["fullAccess"] = c.FullAccess, ["permissionMode"] = c.PermissionMode,
            ["allowedTools"] = new JsonArray(c.AllowedTools.Select(t => (JsonNode)t).ToArray()), ["disallowedTools"] = new JsonArray(c.DisallowedTools.Select(t => (JsonNode)t).ToArray()),
            ["extraFolders"] = new JsonArray(c.ExtraFolders.Select(t => (JsonNode)t).ToArray()),
            ["available"] = s.Ai.Kind(c.Kind) is not null,
        };
        JsonObject Message(AiChatMessage m) => new()
        {
            ["id"] = m.Id, ["role"] = m.Role, ["text"] = m.Text, ["thinking"] = m.Thinking, ["error"] = m.Error, ["edited"] = m.Edited, ["model"] = m.Model,
            ["tools"] = new JsonArray(m.Tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["summary"] = t.Summary, ["error"] = t.IsError }).ToArray()),
        };
        JsonObject Chat(AiChat c, bool full) => new()
        {
            ["id"] = c.Id, ["title"] = c.Title, ["provider"] = c.ProviderId, ["model"] = c.Model, ["effort"] = c.Effort, ["folder"] = c.Folder,
            ["updated"] = c.Updated, ["pinned"] = c.Pinned, ["busy"] = s.Ai.Chats.IsBusy(c.Id), ["contextTokens"] = c.ContextTokens, ["contextWindow"] = c.ContextWindow,
            ["messages"] = full ? new JsonArray(ChatService.Thread(c).Select(m => (JsonNode)Message(m)).ToArray()) : ChatService.Thread(c).Count,
        };
        var provider = O("provider", "A provider's id or name (see `ai providers`).");
        var model = O("model", "Model (Claude Code: haiku, sonnet, opus …; pi: provider/model from `ai models`).");
        var effort = O("effort", "Effort or thinking level (low, medium, high …).");

        Add("ai providers", "List the AI providers set up under Settings → AI Chat.", [], _ => new JsonArray(s.Ai.Configs.Select(c => (JsonNode)Provider(c)).ToArray()));
        Add("ai kinds", "List the kinds of provider avamux can drive (built in and from plugins).", [], _ =>
            new JsonArray(s.Ai.Kinds.Select(k => (JsonNode)new JsonObject { ["kind"] = k.Kind, ["name"] = k.DisplayName, ["description"] = k.Description }).ToArray()));
        Add("ai models", "List a provider's models.", [P("provider", "Provider id or name.", true)], async a =>
        {
            var c = s.Ai.GetConfig(a.Require("provider"));
            var kind = s.Ai.Kind(c.Kind) ?? throw new InvalidOperationException($"Nothing provides the kind '{c.Kind}'.");
            return new JsonArray((await kind.ListModelsAsync(c)).Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["context"] = m.ContextWindow, ["note"] = m.Note }).ToArray());
        });
        Add("ai provider add", "Add a provider.", [P("name", "Name to show.", true), O("kind", "claude-code, pi or command (see `ai kinds`).", true), O("command", "The program to run (default: the kind's usual one)."),
            model, effort, O("template", "command kind: the arguments, with {prompt} and [--model {model}] (see Settings → AI Chat for presets)."), O("output", "command kind: text or jsonl.")], a =>
        {
            var c = new AiProviderConfig { Name = a.Require("name"), Kind = a.Require("kind"), Command = a.Str("command") ?? "", Model = a.Str("model") ?? "", Effort = a.Str("effort") ?? "" };
            if (a.Str("template") is { } t) c.Options["template"] = t;
            if (a.Str("output") is { } o) c.Options["output"] = o;
            return Provider(s.Ai.Put(c));
        });
        Add("ai provider set", "Change a provider: its program, model, effort and permission settings (all off unless set).",
        [
            P("provider", "Provider id or name.", true), O("name", "New name."), O("command", "The program to run."), model, effort,
            O("enabled", "true or false."), O("full-access", "true: skip every permission prompt (Claude Code: --dangerously-skip-permissions)."),
            O("permission-mode", "Claude Code's permission mode (default, acceptEdits, plan …); empty for its own default."),
            O("allowed-tools", "Tools allowed without asking (comma separated); empty clears."), O("disallowed-tools", "Tools never allowed (comma separated); empty clears."),
            O("folders", "Extra folders the agent may use (comma separated); empty clears."), O("confirm-tools", "pi: tools to ask about before they run (comma separated, * for all)."),
        ], a =>
        {
            var c = s.Ai.GetConfig(a.Require("provider"));
            static List<string> Split(string v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (a.Str("name") is { } n) c.Name = n;
            if (a.Str("command") is { } cmd) c.Command = cmd;
            if (a.Str("model") is { } m) c.Model = m;
            if (a.Str("effort") is { } e) c.Effort = e;
            if (a.Bool("enabled") is { } en) c.Enabled = en;
            if (a.Bool("full-access") is { } fa) c.FullAccess = fa;
            if (a.Str("permission-mode") is { } pm) c.PermissionMode = pm;
            if (a.Str("allowed-tools") is { } at) c.AllowedTools = Split(at);
            if (a.Str("disallowed-tools") is { } dt) c.DisallowedTools = Split(dt);
            if (a.Str("folders") is { } f) c.ExtraFolders = Split(f);
            if (a.Str("confirm-tools") is { } ct) c.Options["confirmTools"] = ct;
            return Provider(s.Ai.Put(c));
        });
        Add("ai provider remove", "Remove a provider.", [P("provider", "Provider id or name.", true)], a => { s.Ai.Remove(a.Require("provider")); return "removed"; });

        Add("ai run", "Send one prompt to a provider and print the reply (no tools unless --tools).",
            [Rest("prompt", "The prompt.", true), provider, model, effort, O("folder", "Working folder (default: the wiki)."), F("tools", "Let the agent use its tools.")], async a =>
        {
            var r = await s.Ai.RunAsync(new AiRunRequest
            {
                Prompt = a.Require("prompt"), ProviderId = a.Str("provider") is { } p ? s.Ai.GetConfig(p).Id : null, Model = a.Str("model"), Effort = a.Str("effort"),
                WorkingDirectory = a.Str("folder") is { } f ? AiService.ExpandHome(f) : null, AllowTools = a.Flag("tools"),
            });
            if (!r.Ok && r.Text.Length == 0) throw new InvalidOperationException(r.Error ?? "The provider gave no reply.");
            return r.Text;
        });

        Add("ai tts status", "Whether Kokoro is on and set up, and its voices.", [], _ => new JsonObject
        {
            ["enabled"] = s.Speech.Enabled, ["state"] = s.Speech.State.ToString().ToLowerInvariant(), ["problems"] = new JsonArray(s.Speech.Problems().Select(p => (JsonNode)p).ToArray()),
            ["voices"] = new JsonArray(s.Speech.Voices.Select(v => (JsonNode)v).ToArray()), ["lastError"] = s.Speech.LastError,
        });
        Add("ai tts on", "Switch Kokoro speech on (it starts when something is spoken).", [], _ => { s.Config.Set("ai.tts.enabled", JsonValue.Create(true)); return "on"; });
        Add("ai tts off", "Switch Kokoro speech off, ending it and anything playing.", [], async _ => { s.Config.Set("ai.tts.enabled", JsonValue.Create(false)); await s.Speech.ShutdownAsync(); return "off"; });
        Add("ai tts stop", "Stop speaking.", [], _ => { s.Speech.Stop(); return "stopped"; });
        Add("ai tts say", "Speak text with Kokoro (it must be switched on).", [Rest("text", "What to say.", true)], async a =>
        {
            var error = await s.Speech.SpeakAsync(a.Require("text"));
            if (error is not null) throw new InvalidOperationException(error);
            return "speaking";
        });
        Add("ai chat list", "List AI Chat's conversations.", [], _ => new JsonArray(s.Ai.Chats.List().Select(c => (JsonNode)Chat(c, false)).ToArray()));
        Add("ai chat show", "Show a conversation (the branch shown).", [P("chat", "Chat id or title.", true)], a => Chat(s.Ai.Chats.Get(a.Require("chat")), true));
        Add("ai chat new", "Start a conversation in AI Chat (and open it, unless --no-open).",
            [Rest("message", "A first message to send."), O("title", "Title."), provider, model, effort, O("folder", "Working folder (default: the wiki)."), F("no-open", "Do not open it in the window.")], async a =>
        {
            var request = new AiChatRequest
            {
                Title = a.Str("title"), ProviderId = a.Str("provider") is { } p ? s.Ai.GetConfig(p).Id : null, Model = a.Str("model"), Effort = a.Str("effort"),
                WorkingDirectory = a.Str("folder") is { } f ? AiService.ExpandHome(f) : null, Send = a.Str("message"),
            };
            string id;
            if (a.Flag("no-open") || s.Ai.ChatHost is null) id = s.Ai.Chats.Create(request).Id;
            else id = await s.Ai.ChatHost.OpenChatAsync(request);
            return id;
        });
        Add("ai chat send", "Send a message in a conversation and print the reply.", [P("chat", "Chat id or title.", true), Rest("message", "The message.", true)], async a =>
        {
            var chat = s.Ai.Chats.Get(a.Require("chat"));
            await s.Ai.Chats.SendAsync(chat.Id, a.Require("message"));
            var reply = ChatService.Thread(chat).LastOrDefault();
            if (reply?.Error is { } err && reply.Text.Length == 0) throw new InvalidOperationException(err);
            return reply?.Text.Trim() ?? "";
        });
        Add("ai chat stop", "Stop the reply being written.", [P("chat", "Chat id or title.", true)], async a => { await s.Ai.Chats.StopAsync(s.Ai.Chats.Get(a.Require("chat")).Id); return "stopped"; });
        Add("ai chat delete", "Delete a conversation.", [P("chat", "Chat id or title.", true)], async a => { await s.Ai.Chats.DeleteAsync(s.Ai.Chats.Get(a.Require("chat")).Id); return "deleted"; });
    }
}
