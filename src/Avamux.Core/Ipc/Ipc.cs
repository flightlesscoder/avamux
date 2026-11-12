using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core.Commands;
using Avamux.Core.Logging;

namespace Avamux.Core.Ipc;

/// <summary>
/// Single-instance control channel: a Unix domain socket in the data folder. One JSON line in
/// ({"command":"terminal list","args":{…}}), one JSON line out ({"ok":true,"result":…}).
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly string _path;
    private readonly CommandRegistry _commands;
    private Socket? _listener;
    private readonly CancellationTokenSource _cts = new();

    public IpcServer(string socketPath, CommandRegistry commands) { _path = socketPath; _commands = commands; }

    public void Start()
    {
        if (File.Exists(_path)) File.Delete(_path); // stale socket from a crashed instance (caller already checked nobody answers)
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(16);
        _ = AcceptLoopAsync();
        Log.Info("ipc", $"Listening on {_path}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(Socket client)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
            while (await reader.ReadLineAsync() is { } line)
            {
                JsonObject response;
                try
                {
                    var req = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("Expected a JSON object.");
                    var name = req["command"]?.GetValue<string>() ?? throw new FormatException("Missing \"command\".");
                    response = await _commands.ExecuteAsync(name, req["args"] as JsonObject ?? new JsonObject());
                }
                catch (Exception ex) { response = new JsonObject { ["ok"] = false, ["error"] = "Bad request: " + ex.Message }; }
                await writer.WriteLineAsync(JsonUtil.Line(response));
                await writer.FlushAsync();
            }
        }
        catch (IOException) { }
        catch (Exception ex) { Log.Warn("ipc", "Client failed: " + ex.Message); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}

public static class IpcClient
{
    /// <summary>Sends one command to the running instance. Returns null when no instance is listening.</summary>
    public static async Task<JsonObject?> SendAsync(string socketPath, string command, JsonObject args, TimeSpan? timeout = null)
    {
        if (!File.Exists(socketPath)) return null;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        await writer.WriteLineAsync(JsonUtil.Line(new JsonObject { ["command"] = command, ["args"] = args.DeepClone() }));
        await writer.FlushAsync();
        var line = await reader.ReadLineAsync().WaitAsync(timeout ?? TimeSpan.FromMinutes(5));
        return line is null ? new JsonObject { ["ok"] = false, ["error"] = "The running instance closed the connection." } : JsonNode.Parse(line) as JsonObject;
    }

    public static async Task<bool> IsRunningAsync(string socketPath) =>
        await SendAsync(socketPath, "app ping", new JsonObject(), TimeSpan.FromSeconds(3)) is { } r && r["ok"]?.GetValue<bool>() == true;
}
