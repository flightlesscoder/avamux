using System.Net;
using System.Net.Sockets;
using System.Text;
using Avamux.Core;
using Avamux.Core.Workloads;
using Avamux.Plugins;

namespace Avamux.Tests;

/// <summary>
/// AI Workloads against a stand-in llama-server (tests/Shared/llama-server). Every profile here has a group and port
/// of its own, so a swap in a test can never stop a real server on this machine.
/// </summary>
public class WorkloadTests
{
    private static string FakeServer => Path.Combine(AppRun.RepoRoot, "tests", "Shared", "llama-server");

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Box : IAsyncDisposable
    {
        public Sandbox Sandbox { get; } = new();
        public AppServices App { get; }
        public string Group { get; } = "test-" + Guid.NewGuid().ToString("N")[..8];
        public string Scripts { get; }

        public Box()
        {
            App = new AppServices(Sandbox.Paths);
            Scripts = Path.Combine(Sandbox.Root, "scripts");
            Directory.CreateDirectory(Scripts);
            App.Workloads.SetScriptFolders([Scripts]);
            App.Workloads.SetModelFolders([Path.Combine(Sandbox.Root, "models")]);
        }

        public WorkloadProfile Server(string name, int port, Dictionary<string, string>? env = null)
        {
            var model = Path.Combine(Sandbox.Root, "models", name + ".gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            File.WriteAllText(model, "");
            return App.Workloads.PutProfile(new WorkloadProfile
            {
                Name = name, Kind = WorkloadProfile.LlamaCpp, Binary = FakeServer, Model = model, Port = port, Context = 8192, Group = Group,
                Env = env ?? new() { ["FAKE_LLAMA_LOAD_SECONDS"] = "0.5" }, StartMinutes = 1,
            });
        }

        public async Task<WorkloadInfo?> Find(WorkloadProfile p) =>
            (await App.Workloads.ListAsync()).Items.FirstOrDefault(e => e.SourceId == "local" && e.Info.ProfileId == p.Id)?.Info;

        public async ValueTask DisposeAsync()
        {
            // Whatever a test left running is stopped (only this box's profiles).
            foreach (var p in App.Workloads.Profiles.Where(p => p.Group == Group))
                try { if (await Find(p) is { State: not WorkloadState.Stopped }) await App.Workloads.StopProfileAsync(p.Id); } catch { }
            await App.DisposeAsync();
            Sandbox.Dispose();
        }
    }

    [Fact]
    public async Task A_profile_starts_answers_and_stops()
    {
        await using var box = new Box();
        var port = FreePort();
        var p = box.Server("alpha", port);
        Assert.Equal(WorkloadState.Stopped, (await box.Find(p))!.State);

        var result = await box.App.Workloads.StartAsync(p.Id).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains("is running", result);
        Assert.Contains("8,192", result);
        var w = await box.Find(p);
        Assert.Equal(WorkloadState.Running, w!.State);
        Assert.True(w.ByAvamux);
        Assert.Equal(port, w.Port);
        Assert.Equal(8192, w.Context);
        Assert.NotNull(w.Pid);
        Assert.Contains(w.Actions, a => a.Id == WorkloadActions.Stop && a.Confirm is null);
        Assert.Single(await box.App.Workloads.RunningStartedByAvamuxAsync());
        Assert.Contains("fake llama-server", await box.App.Workloads.LogAsync("local:profile:" + p.Id));

        await box.App.Workloads.StopProfileAsync(p.Id);
        Assert.Equal(WorkloadState.Stopped, (await box.Find(p))!.State);
        Assert.False(ProcessScanner.Alive(w.Pid!.Value));
        Assert.Empty(await box.App.Workloads.RunningStartedByAvamuxAsync());
    }

    [Fact]
    public async Task Starting_a_profile_swaps_out_the_one_in_its_group()
    {
        await using var box = new Box();
        var a = box.Server("alpha", FreePort());
        var b = box.Server("beta", FreePort());
        await box.App.Workloads.StartAsync(a.Id).WaitAsync(TimeSpan.FromSeconds(60));
        var check = await box.App.Workloads.CheckStartAsync(b.Id);
        Assert.Equal(["alpha"], check.Conflicts.Select(c => c.Info.Name));

        await Assert.ThrowsAsync<InvalidOperationException>(() => box.App.Workloads.StartAsync(b.Id, new StartOptions { StopOthers = false }));
        await box.App.Workloads.StartAsync(b.Id).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(WorkloadState.Stopped, (await box.Find(a))!.State);
        Assert.Equal(WorkloadState.Running, (await box.Find(b))!.State);
    }

    [Fact]
    public async Task A_busy_server_is_not_swapped_out_unless_forced()
    {
        await using var box = new Box();
        var busy = Path.Combine(box.Sandbox.Root, "busy");
        var a = box.Server("alpha", FreePort(), new() { ["FAKE_LLAMA_LOAD_SECONDS"] = "0.2", ["FAKE_LLAMA_BUSY"] = busy });
        var b = box.Server("beta", FreePort());
        await box.App.Workloads.StartAsync(a.Id).WaitAsync(TimeSpan.FromSeconds(60));
        File.WriteAllText(busy, "");
        Assert.Equal(WorkloadState.Busy, (await box.Find(a))!.State);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => box.App.Workloads.StartAsync(b.Id));
        Assert.Contains("answering a request", ex.Message);
        await box.App.Workloads.StartAsync(b.Id, new StartOptions { Force = true }).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(WorkloadState.Stopped, (await box.Find(a))!.State);
    }

    [Fact]
    public async Task A_failed_start_says_why_in_plain_words()
    {
        await using var box = new Box();
        var p = box.Server("broken", FreePort(), new() { ["FAKE_LLAMA_FAIL"] = "ggml_backend_cuda_buffer_type_alloc_buffer: allocating 9000.00 MiB on device 0: cudaMalloc failed: out of memory" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => box.App.Workloads.StartAsync(p.Id).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Contains("ran out of memory", ex.Message);
        var w = await box.Find(p);
        Assert.Equal(WorkloadState.Failed, w!.State);
        Assert.Contains("ran out of memory", w.Error);
        Assert.Contains(w.Actions, a => a.Id == "dismiss");
        box.App.Workloads.Dismiss(p.Id);
        Assert.Equal(WorkloadState.Stopped, (await box.Find(p))!.State);
    }

    [Fact]
    public async Task A_server_avamux_did_not_start_is_listed_and_asks_before_it_is_stopped()
    {
        await using var box = new Box();
        var port = FreePort();
        // Started by a shell that exits at once, so its parent is not this test (avamux would count it as its own).
        var r = await Shell.RunAsync($"FAKE_LLAMA_LOAD_SECONDS=0 '{FakeServer}' --port {port} --alias stranger-{box.Group} >/dev/null 2>&1 & echo $!");
        var pid = int.Parse(r.Stdout.Trim());
        try
        {
            WorkloadEntry? e = null;
            for (var i = 0; i < 40 && e?.Info.Port is null; i++)
            {
                await Task.Delay(250);
                e = (await box.App.Workloads.ListAsync()).Items.FirstOrDefault(x => x.Info.Pid == pid);
            }
            Assert.NotNull(e);
            Assert.Equal(WorkloadKind.Server, e!.Info.Kind);
            Assert.Equal(port, e.Info.Port);
            Assert.Equal("stranger-" + box.Group, e.Info.Model);
            Assert.False(e.Info.ByAvamux);
            var stop = Assert.Single(e.Info.Actions);
            Assert.NotNull(stop.Confirm);
            await box.App.Workloads.ActAsync(e.Id, WorkloadActions.Stop);
            Assert.False(ProcessScanner.Alive(pid));
        }
        finally { ProcessScanner.Signal(pid, ProcessScanner.SIGKILL); }
    }

    [Fact]
    public async Task Script_pairs_become_profiles_that_start_and_stop_with_their_own_scripts()
    {
        await using var box = new Box();
        var port = FreePort();
        var run = Path.Combine(box.Scripts, "run");
        File.WriteAllText(Path.Combine(box.Scripts, "_common.sh"), $"""
            STATE_DIR="$(cd "$(dirname "${"{"}BASH_SOURCE[0]{"}"}")" && pwd)"
            RUN_DIR="$STATE_DIR/run"
            PORT={port}
            mkdir -p "$RUN_DIR"
            server_start() {"{"}
              FAKE_LLAMA_LOAD_SECONDS=0.3 nohup "$BIN" --model "$MODEL" --alias "$ALIAS" --port "$PORT" --ctx-size "$CTX" >> "$LOG_FILE" 2>&1 &
              echo $! > "$PID_FILE"
              echo "[start] loading"
              for i in $(seq 1 50); do curl -sf "http://127.0.0.1:$PORT/health" >/dev/null && echo "[start] healthy" && return 0; sleep 0.2; done
              return 1
            {"}"}
            server_stop() {"{"} kill "$(cat "$PID_FILE")"; rm -f "$PID_FILE"; echo "[stop] port free"; {"}"}
            """);
        File.WriteAllText(Path.Combine(box.Scripts, "start-tiny.sh"), $"""
            #!/usr/bin/env bash
            set -euo pipefail
            source "$(dirname "${"{"}BASH_SOURCE[0]{"}"}")/_common.sh"
            CTX="${"{"}1:-2048{"}"}"
            ALIAS="tiny-{box.Group}"
            BIN="{FakeServer}"
            MODEL="/models/tiny.gguf"
            PID_FILE="$RUN_DIR/llama-server-tiny.pid"
            LOG_FILE="$RUN_DIR/llama-server-tiny.log"
            server_start
            """);
        File.WriteAllText(Path.Combine(box.Scripts, "stop-tiny.sh"), """
            #!/usr/bin/env bash
            source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
            PID_FILE="$RUN_DIR/llama-server-tiny.pid"
            server_stop
            """);
        File.WriteAllText(Path.Combine(box.Scripts, "start-orphan.sh"), "#!/bin/sh\necho no stop script\n");

        var p = Assert.Single(box.App.Workloads.Profiles, x => x.Discovered);
        Assert.Equal("script:tiny", p.Id);
        Assert.Equal(port, p.Port);
        Assert.Equal(2048, p.Context);
        Assert.True(p.ContextArg);
        Assert.Equal("tiny-" + box.Group, p.Alias);
        Assert.Equal(Path.Combine(run, "llama-server-tiny.pid"), p.PidFile);
        Assert.Equal(Path.Combine(run, "llama-server-tiny.log"), p.LogFile);
        // Changes to a found script are kept beside it; the script stays the config.
        p.Group = box.Group;
        box.App.Workloads.PutProfile(p);

        var result = await box.App.Workloads.StartAsync(p.Id, new StartOptions { Context = 4096 }).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains("4,096", result);
        var w = await box.Find(p);
        Assert.Equal(WorkloadState.Running, w!.State);
        Assert.Contains("[start] healthy", await box.App.Workloads.LogAsync("local:profile:" + p.Id));
        await box.App.Workloads.StopProfileAsync(p.Id);
        Assert.Equal(WorkloadState.Stopped, (await box.Find(p))!.State);
        Assert.Contains("[stop] port free", File.ReadAllText(Path.Combine(box.App.Workloads.RunDir, "script-tiny.launch.log")));
    }

    [Fact]
    public void Start_scripts_are_read()
    {
        using var sandbox = new Sandbox();
        var dir = Path.Combine(sandbox.Root, "rpc");
        Directory.CreateDirectory(dir);
        // Everything in the script.
        File.WriteAllText(Path.Combine(dir, "start-big-rpc.sh"), """
            #!/usr/bin/env bash
            BIN="/opt/llama/llama-server"
            MODEL="/models/big.gguf"   # shards found automatically
            PORT=8080
            RPC_HOST="192.0.2.5"
            RPC_PORT=50052
            CTX=98304
            ALIAS="big-rpc"
            WORK_DIR="${1:-}"
            RUN_DIR="$(dirname "${BASH_SOURCE[0]}")/run"
            PID_FILE="$RUN_DIR/llama-server.pid"
            LOG_FILE="$RUN_DIR/llama-server.log"
            """);
        File.WriteAllText(Path.Combine(dir, "stop-big-rpc.sh"), "#!/bin/sh\n");
        // Settings in a shared file and a function call, which only a plugin's convention knows how to read.
        File.WriteAllText(Path.Combine(dir, "_engine-common.sh"), "ENGINE_PORT=9123\nRUN_DIR=\"$(dirname \"${BASH_SOURCE[0]}\")/run\"\n");
        File.WriteAllText(Path.Combine(dir, "start-engine-small.sh"), "#!/usr/bin/env bash\nsource \"$(dirname \"${BASH_SOURCE[0]}\")/_engine-common.sh\"\nengine_init small\n");
        File.WriteAllText(Path.Combine(dir, "stop-engine-small.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "start-unwanted.sh"), "#!/bin/sh\nPORT=9124\n");
        File.WriteAllText(Path.Combine(dir, "stop-unwanted.sh"), "#!/bin/sh\n");
        // Needs an argument to know what to start.
        File.WriteAllText(Path.Combine(dir, "start-picky.sh"), "#!/bin/sh\nMODEL=\"${1:?usage: start-picky.sh MODEL}\"\n");
        File.WriteAllText(Path.Combine(dir, "stop-picky.sh"), "#!/bin/sh\n");

        var plain = ScriptCatalog.Discover([dir]).ToDictionary(p => p.Id);
        Assert.Equal(["script:big-rpc", "script:engine-small", "script:unwanted"], plain.Keys.Order());
        var big = plain["script:big-rpc"];
        Assert.Equal("big rpc", big.Name);
        Assert.Equal(8080, big.Port);
        Assert.Equal(98304, big.Context);
        Assert.False(big.ContextArg); // its first argument is a folder
        Assert.Equal("big-rpc", big.Alias);
        Assert.Equal("192.0.2.5:50052", big.RpcAddress);
        Assert.Equal(Path.Combine(dir, "run", "llama-server.pid"), big.PidFile);
        Assert.Equal("/models/big.gguf", big.Model);
        Assert.Equal(0, plain["script:engine-small"].Port);

        ScriptConvention engine = (script, p) =>
        {
            if (Path.GetFileName(script.StartScript) == "start-unwanted.sh") return false;
            if (System.Text.RegularExpressions.Regex.Match(script.Text, @"engine_init\s+(\w+)") is { Success: true } m)
            {
                p.Port = int.Parse(script.Vars["ENGINE_PORT"]);
                p.LogFile = Path.Combine(script.Vars["RUN_DIR"], $"engine-{m.Groups[1].Value}.log");
                p.ServerType = "Engine";
            }
            return true;
        };
        ScriptConvention broken = (_, _) => throw new InvalidOperationException("a broken plugin");
        var list = ScriptCatalog.Discover([dir], [engine, broken]).ToDictionary(p => p.Id);
        Assert.Equal(["script:big-rpc", "script:engine-small"], list.Keys.Order());
        var small = list["script:engine-small"];
        Assert.Equal(9123, small.Port);
        Assert.Equal("Engine", small.ServerType);
        Assert.Equal(Path.Combine(dir, "run", "engine-small.log"), small.LogFile);
    }

    [Fact]
    public void Gguf_headers_are_read_and_split_models_counted_once()
    {
        using var sandbox = new Sandbox();
        var dir = Path.Combine(sandbox.Root, "models", "qwen");
        Directory.CreateDirectory(dir);
        WriteGguf(Path.Combine(dir, "Tiny-Q4_K_M.gguf"), "qwen3moe", "Tiny Model", "30B-A3B", 15, 262144, 48, 128);
        WriteGguf(Path.Combine(dir, "Big-00001-of-00002.gguf"), "llama", "Big", "70B", 30, 131072, 80, null);
        WriteGguf(Path.Combine(dir, "Big-00002-of-00002.gguf"), "llama", "Big", "70B", 30, 131072, 80, null);
        WriteGguf(Path.Combine(dir, "mmproj-Tiny-f16.gguf"), "clip", "Tiny projector", null, 1, null, null, null);
        File.WriteAllBytes(Path.Combine(dir, "notes.txt"), new byte[2_000_000]);

        var lib = new ModelLibrary(Path.Combine(sandbox.Root, "cache.json"));
        var models = lib.Scan([Path.Combine(sandbox.Root, "models")]);
        Assert.Equal(3, models.Count);
        var tiny = models.Single(m => m.Architecture == "qwen3moe");
        Assert.Equal("Tiny-Q4_K_M", tiny.Name);
        Assert.Equal("Tiny Model", tiny.Title);
        Assert.Equal("Q4_K_M", tiny.Quant);
        Assert.Equal("30B-A3B", tiny.Size);
        Assert.Equal(262144, tiny.ContextLength);
        Assert.Equal(48, tiny.Layers);
        Assert.Equal(128, tiny.Experts);
        var big = models.Single(m => m.Architecture == "llama");
        Assert.Equal(2, big.Shards);
        Assert.Equal("IQ4_XS", big.Quant);
        Assert.True(models.Single(m => m.Architecture == "clip").Projector);
        // The second scan comes from the cache.
        Assert.Equal(3, new ModelLibrary(Path.Combine(sandbox.Root, "cache.json")).Scan([Path.Combine(sandbox.Root, "models")]).Count);
    }

    /// <summary>A GGUF file with just a header (and a tokenizer array to skip), padded past the size models are counted from.</summary>
    private static void WriteGguf(string path, string arch, string name, string? size, long fileType, long? ctx, long? layers, long? experts)
    {
        using var f = File.Create(path);
        using var w = new BinaryWriter(f, Encoding.UTF8);
        var kv = new List<Action>();
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        void KStr(string k, string v) => kv.Add(() => { Str(k); w.Write(8u); Str(v); });
        void KU32(string k, long v) => kv.Add(() => { Str(k); w.Write(4u); w.Write((uint)v); });
        KStr("general.architecture", arch);
        KStr("general.name", name);
        if (size is not null) KStr("general.size_label", size);
        KU32("general.file_type", fileType);
        if (ctx is { } c) KU32($"{arch}.context_length", c);
        if (layers is { } l) KU32($"{arch}.block_count", l);
        if (experts is { } e) KU32($"{arch}.expert_count", e);
        kv.Add(() => { Str("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(3ul); Str("a"); Str("bb"); Str("ccc"); });
        kv.Add(() => { Str("tokenizer.ggml.scores"); w.Write(9u); w.Write(6u); w.Write(3ul); w.Write(0f); w.Write(1f); w.Write(2f); });
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(0ul);
        w.Write((ulong)kv.Count);
        foreach (var a in kv) a();
        w.Write(new byte[1_100_000]);
    }

    [Theory]
    [InlineData("llama_model_load: error loading model: unknown model architecture: 'qwen9'", "architecture")]
    [InlineData("couldn't bind HTTP server socket, hostname: 0.0.0.0, port: 8080", "already using the port")]
    [InlineData("./llama-server: error while loading shared libraries: libcudart.so.13: cannot open shared object file", "library")]
    [InlineData("[start] ERROR: something is already listening on :8080.", "already on that port")]
    public void Diagnosis_explains_the_usual_failures(string line, string expected) =>
        Assert.Contains(expected, Diagnosis.Explain("main: loading\n" + line + "\nmain: exiting"));

    [Fact]
    public void Ai_processes_are_recognised()
    {
        ProcInfo P(params string[] args) => new(1, 0, args[0], args, 0, null, null);
        Assert.Equal("llama-server", ProcessScanner.Classify(P("/opt/llama/build/bin/llama-server", "--port", "8080"))!.Program);
        Assert.Equal(WorkloadKind.RpcServer, ProcessScanner.Classify(P("/opt/llama/rpc-server", "-H", "0.0.0.0"))!.Kind);
        Assert.Equal("claude", ProcessScanner.Classify(P("claude"))!.Program);
        Assert.Null(ProcessScanner.Classify(new ProcInfo(1, 0, "/home/x/.local/share/claude/versions/2.1.0", ["claude bg-pty-host", "--bg-pty-host", "x"], 0, null, null)));
        Assert.Equal("claude", ProcessScanner.Classify(new ProcInfo(1, 0, "/home/x/.local/share/claude/versions/2.1.0", ["/home/x/.local/share/claude/versions/2.1.0", "--session-id", "x"], 0, null, null))!.Program);
        Assert.Equal("pi", ProcessScanner.Classify(P("node", "/home/linuxbrew/.linuxbrew/bin/pi", "--mode", "rpc"))!.Program);
        Assert.Equal("llama-server", ProcessScanner.Classify(P("/usr/bin/python3", "/repo/tests/Shared/llama-server", "--port", "1"))!.Program);
        Assert.Null(ProcessScanner.Classify(P("/usr/bin/python3", "-m", "http.server")));
        Assert.Null(ProcessScanner.Classify(P("bash")));
    }

    [Fact]
    public async Task Plugins_recognise_programs_and_name_the_servers_providers_need()
    {
        ProcInfo P(params string[] args) => new(1, 0, args[0], args, 0, null, null);
        var engine = P("/usr/bin/python3", "/opt/engine/serve-engine.py");
        Assert.Null(ProcessScanner.Classify(engine));
        using (ProcessScanner.Recognisers.Add(p => p.Args.Any(a => a.EndsWith("serve-engine.py", StringComparison.Ordinal)) ? new ProcKind("engine", "Engine", WorkloadKind.Server) : null))
            Assert.Equal("engine", ProcessScanner.Classify(engine)!.Program);
        Assert.Null(ProcessScanner.Classify(engine));

        using var sandbox = new Sandbox();
        await using var app = new AppServices(sandbox.Paths);
        var provider = new AiProviderConfig { Id = "box", Kind = Avamux.Core.Ai.PiProvider.KindId, Name = "Box" };
        Assert.Null(await app.Workloads.CheckEndpointAsync(provider, null));
        var port = FreePort();
        using (app.Workloads.EndpointResolvers.Add((c, _) => c.Id == "box" ? new LocalEndpoint(port, "Box 1") : null))
        {
            var check = await app.Workloads.CheckEndpointAsync(provider, null);
            Assert.NotNull(check);
            Assert.False(check.Answering);
            Assert.Equal(port, check.Port);
            Assert.Equal("Box 1", check.Name);
        }
        Assert.Null(await app.Workloads.CheckEndpointAsync(provider, null));
        Assert.Null(app.Workloads.KnownContext(port));
        app.Workloads.SeenContext(port, 4096);
        Assert.Equal(4096, app.Workloads.KnownContext(port));
    }

    private sealed class FakeLease(string id) : IWorkloadLease
    {
        public TaskCompletionSource<string> LostSource { get; } = new();
        public bool Released { get; private set; }
        public string Id => id;
        public Task<string> Lost => LostSource.Task;
        public ValueTask DisposeAsync() { Released = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLeases : IWorkloadLeaseProvider
    {
        public List<FakeLease> Granted { get; } = [];
        public string Id => "fake-queue";
        public string Name => "Fake queue";
        public Task<IWorkloadLease> AcquireAsync(WorkloadLeaseRequest request, CancellationToken ct = default)
        {
            var l = new FakeLease("lease-" + Granted.Count);
            Granted.Add(l);
            return Task.FromResult<IWorkloadLease>(l);
        }
    }

    [Fact]
    public async Task A_lease_is_taken_before_starting_and_losing_it_stops_the_server()
    {
        await using var box = new Box();
        var leases = new FakeLeases();
        box.App.Workloads.AddLeaseProvider(leases);
        var p = box.Server("leased", FreePort());
        p.Leases = ["fake-queue"];
        box.App.Workloads.PutProfile(p);
        await box.App.Workloads.StartAsync(p.Id).WaitAsync(TimeSpan.FromSeconds(60));
        var lease = Assert.Single(leases.Granted);
        Assert.False(lease.Released);
        Assert.Single(box.App.Workloads.HeldLeases);

        string? notice = null;
        box.App.Workloads.Notice += (t, m) => notice = t + ": " + m;
        lease.LostSource.SetResult("Someone pressed Stop on the TV.");
        for (var i = 0; i < 80 && (await box.Find(p))!.State != WorkloadState.Stopped; i++) await Task.Delay(250);
        Assert.Equal(WorkloadState.Stopped, (await box.Find(p))!.State);
        Assert.Contains("Stop on the TV", notice);
        Assert.Empty(box.App.Workloads.HeldLeases);
    }

    [Fact]
    public async Task A_server_that_uses_an_rpc_server_checks_it_first()
    {
        await using var box = new Box();
        var rpcListener = new TcpListener(IPAddress.Loopback, 0);
        rpcListener.Start();
        var rpcAddress = "127.0.0.1:" + ((IPEndPoint)rpcListener.LocalEndpoint).Port;
        try
        {
            var rpc = box.App.Workloads.PutProfile(new WorkloadProfile
            {
                Id = "gpu-box", Name = "GPU box", Kind = WorkloadProfile.Rpc, Group = "", RpcAddress = rpcAddress, VersionCommand = "echo built from abc1234",
            });
            var w = (await box.App.Workloads.ListAsync()).Items.Single(e => e.Info.ProfileId == rpc.Id).Info;
            Assert.Equal(WorkloadKind.RpcServer, w.Kind);
            Assert.Equal(WorkloadState.Running, w.State);

            var p = box.Server("split", FreePort());
            p.NeedsRpc = [rpc.Id];
            box.App.Workloads.PutProfile(p);
            await box.App.Workloads.StartAsync(p.Id).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains("--rpc " + rpcAddress, File.ReadAllText(Path.Combine(box.App.Workloads.RunDir, "split.log")));
            await box.App.Workloads.StopProfileAsync(p.Id);
            Assert.Contains("same llama.cpp commit", await box.App.Workloads.ActAsync("local:profile:" + rpc.Id, "check"));

            // Built from another commit: refused, saying so.
            rpc.VersionCommand = "echo built from def5678";
            box.App.Workloads.PutProfile(rpc);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => box.App.Workloads.StartAsync(p.Id));
            Assert.Contains("same commit", ex.Message);

            // Not answering, and no way to start it.
            rpcListener.Stop();
            rpc.VersionCommand = "";
            box.App.Workloads.PutProfile(rpc);
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => box.App.Workloads.StartAsync(p.Id));
            Assert.Contains("does not answer", ex.Message);
        }
        finally { rpcListener.Stop(); }
    }

    [Fact]
    public async Task The_cli_lists_starts_and_stops()
    {
        await using var box = new Box();
        var p = box.Server("cli-one", FreePort());
        var start = await box.App.Commands.ExecuteAsync("ai workloads start", new System.Text.Json.Nodes.JsonObject { ["profile"] = "cli-one" });
        Assert.True(start["ok"]!.GetValue<bool>(), start.ToJsonString());
        var list = await box.App.Commands.ExecuteAsync("ai workloads list", new System.Text.Json.Nodes.JsonObject { ["source"] = "local" });
        Assert.Contains(list["result"]!.AsArray(), r => r!["name"]!.GetValue<string>() == "cli-one" && r["state"]!.GetValue<string>() == "running");
        var stop = await box.App.Commands.ExecuteAsync("ai workloads stop", new System.Text.Json.Nodes.JsonObject { ["workload"] = "local:profile:" + p.Id });
        Assert.True(stop["ok"]!.GetValue<bool>(), stop.ToJsonString());
        Assert.Equal(WorkloadState.Stopped, (await box.Find(p))!.State);
    }
}

/// <summary>
/// Read-only looks at this machine's real scripts, processes and models (opt-in: AVAMUX_WORKLOAD_REAL=1). Nothing is
/// started or stopped. The report goes to $AVAMUX_WORKLOAD_REPORT (default: the temp folder).
/// </summary>
public class RealWorkloadTests
{
    /// <summary>This machine's folders, from AVAMUX_WORKLOAD_SCRIPTS / AVAMUX_WORKLOAD_MODELS (comma separated), so none are written here.</summary>
    private static string[] RealScripts => Folders("AVAMUX_WORKLOAD_SCRIPTS");
    private static string[] RealModels => Folders("AVAMUX_WORKLOAD_MODELS");
    private static string[] Folders(string variable) =>
        (Environment.GetEnvironmentVariable(variable) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Avamux.Core.Ai.AiService.ExpandHome).ToArray();

    [Fact]
    public async Task Describe_this_machine()
    {
        if (Environment.GetEnvironmentVariable("AVAMUX_WORKLOAD_REAL") != "1") return;
        var report = new StringBuilder();
        foreach (var p in ScriptCatalog.Discover(RealScripts))
            report.AppendLine($"profile {p.Id}: port={p.Port} ctx={p.Context} ctxArg={p.ContextArg} alias={p.Alias} rpc={p.RpcAddress} pid={p.PidFile} log={p.LogFile} model={p.Model}");
        using var sandbox = new Sandbox();
        await using var app = new AppServices(sandbox.Paths);
        app.Workloads.SetScriptFolders(RealScripts);
        var overview = await app.Workloads.ListAsync();
        foreach (var e in overview.Items.Where(e => e.Info.State != WorkloadState.Stopped || e.SourceId != "local"))
            report.AppendLine($"workload {e.Id}: {e.Info.Name} [{e.Info.State}] type={e.Info.Type} port={e.Info.Port} vram={e.Info.VramMiB} by={e.Info.StartedBy} detail={e.Info.Detail}");
        foreach (var (k, v) in overview.Problems) report.AppendLine($"problem {k}: {v}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var models = app.Workloads.Models.Scan([.. ModelLibrary.DefaultFolders(), .. RealModels]);
        report.AppendLine($"models: {models.Count} in {sw.ElapsedMilliseconds} ms");
        foreach (var m in models.Take(400))
            report.AppendLine($"model {m.Name} | {m.Bytes / 1073741824.0:0.0} GB | {m.Architecture} {m.Size} {m.Quant} ctx={m.ContextLength} layers={m.Layers} shards={m.Shards} proj={m.Projector} err={m.Error} | {m.Path}");
        File.WriteAllText(Environment.GetEnvironmentVariable("AVAMUX_WORKLOAD_REPORT") ?? Path.Combine(Path.GetTempPath(), "avamux-workloads-report.txt"), report.ToString());
    }

    /// <summary>
    /// Starts a real start/stop script pair through avamux, then stops it (opt-in: AVAMUX_WORKLOAD_LIVE=&lt;profile id&gt;,
    /// e.g. script:my-model). Skipped when something already answers on the profile's port: it only stops what it started.
    /// </summary>
    [Fact]
    public async Task Live_script_profile_starts_and_stops()
    {
        if (Environment.GetEnvironmentVariable("AVAMUX_WORKLOAD_LIVE") is not { Length: > 0 } id) return;
        using var sandbox = new Sandbox();
        await using var app = new AppServices(sandbox.Paths);
        app.Workloads.SetScriptFolders(RealScripts);
        var p = app.Workloads.GetProfile(id);
        if ((await ServerProbe.ProbeAsync(p.BaseUrl!)).Answering) return;
        var result = await app.Workloads.StartAsync(p.Id).WaitAsync(TimeSpan.FromMinutes(10));
        try
        {
            Assert.Contains("is running", result);
            var w = (await app.Workloads.ListAsync()).Items.Single(e => e.Info.ProfileId == p.Id && e.SourceId == "local").Info;
            Assert.Equal(WorkloadState.Running, w.State);
            Assert.True(w.VramMiB > 0, "it holds GPU memory");
            Assert.True(w.Context > 0);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "avamux-workloads-live.txt"), $"{result}\n{w.Name} pid={w.Pid} vram={w.VramMiB} ctx={w.Context} model={w.Model}\n\n{await app.Workloads.LogAsync("local:profile:" + p.Id, 30)}");
        }
        finally { await app.Workloads.StopProfileAsync(p.Id); }
        Assert.False((await ServerProbe.ProbeAsync(p.BaseUrl!)).Answering);
    }
}
