using System.Text.Json.Nodes;
using Avamux.Core.Workloads;
using Avamux.Plugins;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private void AddWorkloadCommands(AppServices s)
    {
        static string State(WorkloadState st) => st.ToString().ToLowerInvariant();
        static JsonArray Arr(IEnumerable<string> items) => new(items.Select(x => (JsonNode)x).ToArray());
        JsonObject Row(WorkloadEntry e) => new()
        {
            ["id"] = e.Id, ["name"] = e.Info.Name, ["state"] = State(e.Info.State), ["type"] = e.Info.Type, ["model"] = e.Info.Model,
            ["port"] = e.Info.Port, ["pid"] = e.Info.Pid, ["vramMiB"] = e.Info.VramMiB, ["detail"] = e.Info.Error ?? e.Info.Detail,
        };
        JsonObject Full(WorkloadEntry e)
        {
            var i = e.Info;
            var o = new JsonObject
            {
                ["id"] = e.Id, ["source"] = e.SourceName, ["machine"] = e.Machine, ["name"] = i.Name, ["kind"] = i.Kind.ToString().ToLowerInvariant(), ["type"] = i.Type,
                ["state"] = State(i.State), ["detail"] = i.Detail, ["error"] = i.Error, ["model"] = i.Model, ["endpoint"] = i.Endpoint, ["port"] = i.Port, ["pid"] = i.Pid,
                ["context"] = i.Context, ["vramMiB"] = i.VramMiB, ["ramMiB"] = i.RamMiB, ["started"] = i.Started, ["finished"] = i.Finished, ["progress"] = i.Progress,
                ["startedBy"] = i.StartedBy, ["byAvamux"] = i.ByAvamux, ["folder"] = i.Folder, ["profile"] = i.ProfileId,
                ["actions"] = Arr(i.Actions.Select(a => a.Id)),
            };
            var facts = new JsonObject();
            foreach (var (k, v) in i.Facts) facts[k] = v;
            o["facts"] = facts;
            return o;
        }
        JsonObject Profile(WorkloadProfile p)
        {
            var o = (JsonObject)JsonUtil.ToNode(p)!;
            o["discovered"] = p.Discovered;
            return o;
        }
        var all = F("all", "Also what is stopped or finished.");
        var id = P("workload", "A workload's id as `ai workloads list` shows it (or its name).", true);

        Add("ai workloads list", "List AI workloads: model servers, agents, queue jobs, avamux's AI tasks, containers.",
            [all, O("source", "Only one source (local, avamux, containers, or a plugin's).")], async a =>
        {
            var overview = await s.Workloads.ListAsync();
            var items = overview.Items.Where(e => a.Str("source") is not { } src || e.SourceId == src);
            if (!a.Flag("all")) items = items.Where(e => e.Info.State is not (WorkloadState.Stopped or WorkloadState.Done or WorkloadState.Cancelled));
            return new JsonArray(items.Select(e => (JsonNode)Row(e)).ToArray());
        });
        Add("ai workloads show", "Everything about one workload.", [id], async a => Full(await s.Workloads.FindAsync(a.Require("workload"))));
        Add("ai workloads problems", "What sources could not answer (another machine off, a gateway not set up).", [], async _ =>
        {
            var o = new JsonObject();
            foreach (var (k, v) in (await s.Workloads.ListAsync()).Problems) o[k] = v;
            return o;
        });
        Add("ai workloads logs", "The end of a workload's log or output (a server's log, an AI run's thinking and reply).",
            [id, O("lines", "How many lines (default 200).")], async a => await s.Workloads.LogAsync(a.Require("workload"), a.Int("lines") ?? 200) is { Length: > 0 } t ? t : "(nothing to show)");
        Add("ai workloads act", "Do one of the actions a workload offers (see `ai workloads show`).",
            [id, P("action", "The action's id (stop, start, cancel, unload …).", true)], async a => await s.Workloads.ActAsync(a.Require("workload"), a.Require("action")));
        Add("ai workloads stop", "Stop a workload (a server, an agent, a job, a task), the way its source does it.", [id], async a =>
        {
            var e = await s.Workloads.FindAsync(a.Require("workload"));
            var action = e.Info.Actions.FirstOrDefault(x => x.Id is WorkloadActions.Stop or WorkloadActions.Cancel or WorkloadActions.Unload)
                ?? throw new InvalidOperationException($"{e.Info.Name} cannot be stopped (it offers: {string.Join(", ", e.Info.Actions.Select(x => x.Id))}).");
            return await s.Workloads.ActAsync(e.Id, action.Id);
        });
        Add("ai workloads start", "Start a profile (stopping what shares its port or GPU group first: a swap) and wait until it answers.",
            [P("profile", "A profile's id or name (see `ai workloads profiles`).", true), O("ctx", "Context size for this start."),
             F("force", "Swap even when what is running is answering a request."), F("no-swap", "Refuse instead of stopping what is in the way.")], async a =>
                await s.Workloads.StartAsync(a.Require("profile"), new StartOptions { Context = a.Int("ctx"), Force = a.Flag("force"), StopOthers = !a.Flag("no-swap") }));
        Add("ai workloads swap", "Stop the server running now and start this profile instead (the same as start).",
            [P("profile", "A profile's id or name.", true), O("ctx", "Context size for this start."), F("force", "Even when the running one is answering a request.")], async a =>
                await s.Workloads.StartAsync(a.Require("profile"), new StartOptions { Context = a.Int("ctx"), Force = a.Flag("force") }));
        Add("ai workloads check", "What starting a profile would stop, and what else holds the GPU. Changes nothing.",
            [P("profile", "A profile's id or name.", true)], async a =>
        {
            var c = await s.Workloads.CheckStartAsync(a.Require("profile"));
            return new JsonObject
            {
                ["profile"] = c.Profile.Name, ["wouldStop"] = Arr(c.Conflicts.Select(x => x.Info.Name)), ["busy"] = Arr(c.Busy.Select(x => x.Info.Name)),
                ["gpuHolders"] = Arr(c.GpuHolders.Select(h => $"{h.Name} (pid {h.Pid}, {h.MiB:N0} MiB)")), ["freeVramMiB"] = c.FreeVramMiB, ["notes"] = Arr(c.Notes),
            };
        });
        Add("ai workloads cancel", "Cancel a profile's start while it is loading.", [P("profile", "A profile's id or name.", true)], a =>
            s.Workloads.CancelStart(s.Workloads.GetProfile(a.Require("profile")).Id) ? "Cancelling." : "It is not starting.");
        Add("ai workloads gpu", "GPU memory, and how much each process holds.", [], async _ =>
        {
            var g = await Gpu.ReadAsync();
            var procs = ProcessScanner.List().ToDictionary(p => p.Pid);
            return new JsonObject
            {
                ["gpus"] = new JsonArray(g.Gpus.Select(x => (JsonNode)new JsonObject { ["index"] = x.Index, ["name"] = x.Name, ["usedMiB"] = x.UsedMiB, ["totalMiB"] = x.TotalMiB, ["freeMiB"] = x.FreeMiB }).ToArray()),
                ["processes"] = new JsonArray(g.UsedByPid.OrderByDescending(kv => kv.Value).Select(kv => (JsonNode)new JsonObject
                    { ["pid"] = kv.Key, ["name"] = procs.TryGetValue(kv.Key, out var p) ? p.Name : "?", ["MiB"] = kv.Value }).ToArray()),
            };
        });
        Add("ai workloads quit-check", "Servers avamux started that are still running (what avamux asks about when it quits).", [], async _ =>
            new JsonArray((await s.Workloads.RunningStartedByAvamuxAsync()).Select(e => (JsonNode)Row(e)).ToArray()));
        Add("ai workloads stop-started", "Stop every server avamux started.", [], async _ => { await s.Workloads.StopAllStartedAsync(); return "stopped"; });
        Add("ai workloads leases", "Leases avamux holds on other machines (for servers that use their GPUs).", [], _ => Arr(s.Workloads.HeldLeases));

        // ---- profiles ----
        Add("ai workloads profiles", "List the profiles: llama.cpp servers avamux starts, start/stop scripts it found, commands, RPC servers, APIs.",
            [F("all", "Also hidden ones.")], a => new JsonArray(s.Workloads.Profiles.Where(p => a.Flag("all") || !p.Hidden).Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["kind"] = p.Kind, ["port"] = p.Port > 0 ? p.Port : null, ["context"] = p.Context > 0 ? p.Context : null,
                ["model"] = p.Model.Length > 0 ? Path.GetFileName(p.Model) : null, ["group"] = p.Group, ["hidden"] = p.Hidden,
            }).ToArray()));
        Add("ai workloads profile show", "Everything about a profile.", [P("profile", "A profile's id or name.", true)], a => Profile(s.Workloads.GetProfile(a.Require("profile"))));
        CommandParam[] fields =
        [
            O("name", "Name to show."), O("kind", "llama.cpp, script, command, rpc or api."), O("binary", "llama.cpp: the llama-server program."), O("model", "llama.cpp: the model file."),
            O("alias", "llama.cpp: the name it answers to (--alias)."), O("host", "llama.cpp: address to listen on (default 127.0.0.1)."), O("port", "Port."), O("ctx", "Context size."),
            M("arg", "llama.cpp: another argument for llama-server (repeat it; each is one argument)."), M("env", "A variable for it, NAME=value (repeat it)."),
            O("start-script", "script: the start script."), O("stop-script", "script: the stop script."), M("start-arg", "script: an argument for the start script (repeat it)."),
            O("command", "command: what starts it (stays in the foreground)."), O("stop-command", "command or rpc: what stops it."), O("url", "command or api: where it answers."),
            O("folder", "command: the folder it runs in."), O("rpc-address", "rpc: host:port of the RPC server."), O("status-command", "rpc: exits 0 when it runs."),
            O("start-command", "rpc: starts it."), O("restart-command", "rpc: restarts it."), O("version-command", "rpc: prints the llama.cpp commit it was built from."),
            O("api-key-env", "api: the variable holding the key."), O("api-style", "api: openai, anthropic or ollama."), O("group", "Profiles in one group never run together (default gpu; empty: no group)."),
            O("needs-rpc", "RPC profiles it uses (comma separated)."), O("leases", "Lease providers that must agree first (comma separated)."),
            O("idle", "Stop it after this many idle minutes, when avamux started it (0: never)."), O("start-minutes", "How long to wait for it to be ready."),
            O("hidden", "true or false."), O("notes", "Notes."),
        ];
        static List<string> Split(string v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        void Apply(WorkloadProfile p, Args a)
        {
            if (a.Str("name") is { } n) p.Name = n;
            if (a.Str("kind") is { } k) p.Kind = k;
            if (a.Str("binary") is { } b) p.Binary = b;
            if (a.Str("model") is { } m) p.Model = m;
            if (a.Str("alias") is { } al) p.Alias = al;
            if (a.Str("host") is { } h) p.Host = h;
            if (a.Int("port") is { } port) p.Port = port;
            if (a.Int("ctx") is { } ctx) p.Context = ctx;
            if (a.Has("arg")) p.Args = a.List("arg");
            if (a.Has("env")) p.Env = a.List("env").Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
            if (a.Str("start-script") is { } ss) p.StartScript = ss;
            if (a.Str("stop-script") is { } st) p.StopScript = st;
            if (a.Has("start-arg")) p.StartArgs = a.List("start-arg");
            if (a.Str("command") is { } c) p.Command = c;
            if (a.Str("stop-command") is { } sc) p.StopCommand = sc;
            if (a.Str("url") is { } u) p.Url = u;
            if (a.Str("folder") is { } f) p.Folder = f;
            if (a.Str("rpc-address") is { } ra) p.RpcAddress = ra;
            if (a.Str("status-command") is { } stc) p.StatusCommand = stc;
            if (a.Str("start-command") is { } sac) p.StartCommand = sac;
            if (a.Str("restart-command") is { } rc) p.RestartCommand = rc;
            if (a.Str("version-command") is { } vc) p.VersionCommand = vc;
            if (a.Str("api-key-env") is { } ke) p.ApiKeyEnv = ke;
            if (a.Str("api-style") is { } aps) p.ApiStyle = aps;
            if (a.Str("group") is { } g) p.Group = g;
            if (a.Str("needs-rpc") is { } nr) p.NeedsRpc = Split(nr);
            if (a.Str("leases") is { } l) p.Leases = Split(l);
            if (a.Num("idle") is { } idle) p.IdleMinutes = idle;
            if (a.Num("start-minutes") is { } sm) p.StartMinutes = sm;
            if (a.Bool("hidden") is { } hid) p.Hidden = hid;
            if (a.Str("notes") is { } notes) p.Notes = notes;
            if (p.Kind is WorkloadProfile.Rpc or WorkloadProfile.Api && !a.Has("group")) p.Group = "";
        }
        Add("ai workloads profile add", "Add a profile.", [P("name", "Name to show.", true), .. fields.Where(x => x.Name != "name")], a =>
        {
            var p = new WorkloadProfile { Name = a.Require("name"), Kind = a.Str("kind") ?? WorkloadProfile.LlamaCpp };
            Apply(p, a);
            return Profile(s.Workloads.PutProfile(p));
        });
        Add("ai workloads profile set", "Change a profile (found scripts: name, start arguments, group, idle, RPC, leases, notes, hidden).",
            [P("profile", "A profile's id or name.", true), .. fields], a =>
        {
            var p = s.Workloads.GetProfile(a.Require("profile"));
            Apply(p, a);
            return Profile(s.Workloads.PutProfile(p));
        });
        Add("ai workloads profile remove", "Remove a profile (a found script is hidden instead).", [P("profile", "A profile's id or name.", true)], a =>
        {
            s.Workloads.RemoveProfile(a.Require("profile"));
            return "removed";
        });
        Add("ai workloads folders", "The folders searched for start/stop scripts and for models; give a list to change them.",
            [O("scripts", "Script folders (comma separated)."), O("models", "Model folders (comma separated).")], a =>
        {
            if (a.Str("scripts") is { } sf) s.Workloads.SetScriptFolders(Split(sf));
            if (a.Str("models") is { } mf) s.Workloads.SetModelFolders(Split(mf));
            return new JsonObject { ["scripts"] = Arr(s.Workloads.ScriptFolders), ["models"] = Arr(s.Workloads.ModelFolders) };
        });

        // ---- models ----
        Add("ai workloads models", "Model files on disk (GGUF ones with what their header says), and which profiles use each.",
            [O("folder", "Only this folder (default: the model folders)."), F("projectors", "Also vision/audio projectors (mmproj).")], a =>
        {
            var folders = a.Str("folder") is { } f ? [f] : s.Workloads.ModelFolders;
            var usedBy = s.Workloads.ModelUse();
            return new JsonArray(s.Workloads.Models.Scan(folders).Where(m => a.Flag("projectors") || !m.Projector).Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Name, ["size"] = $"{m.Bytes / 1073741824.0:0.0} GB", ["arch"] = m.Architecture, ["params"] = m.Size, ["quant"] = m.Quant,
                ["context"] = m.ContextLength, ["usedBy"] = usedBy.TryGetValue(m.Path, out var u) ? string.Join(", ", u) : null, ["path"] = m.Path,
            }).ToArray());
        });
    }
}
