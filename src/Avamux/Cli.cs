using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Commands;
using Avamux.Core.Ipc;

namespace Avamux;

/// <summary>Command-line client: parses arguments, sends the command to the running instance, prints the result.</summary>
internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var registry = CommandRegistry.Build(null);
        if (args is ["--version"] or ["-v"] or ["version"])
        {
            Console.WriteLine("avamux " + typeof(AppServices).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        var parsed = CliParser.Parse(registry, args);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine(parsed.Error);
            Console.Error.WriteLine();
            Console.Error.Write(CliParser.Help(registry, parsed.HelpTopic));
            return 2;
        }
        if (parsed.Help || parsed.Command is null)
        {
            Console.Write(CliParser.Help(registry, parsed.HelpTopic));
            return 0;
        }

        var cmd = parsed.Command;
        var a = parsed.Args;
        // Paths are relative to where the CLI was run, not to the app's working folder.
        foreach (var key in new[] { "cwd", "file", "out", "dir" })
            if (a[key] is JsonValue v && v.TryGetValue<string>(out var p)) a[key] = Path.GetFullPath(p);
        if (cmd.Name is "browser upload" && a["files"] is JsonArray files)
            a["files"] = new JsonArray(files.Select(f => (JsonNode)Path.GetFullPath(f!.GetValue<string>())).ToArray());
        // The terminal tab this runs in, so browser commands act on the pane beside it.
        if (cmd.Group == "browser" && Environment.GetEnvironmentVariable("AVAMUX_TERMINAL_ID") is { Length: > 0 } tab) a["callerTerminal"] = tab;
        // A split from inside a terminal splits that terminal (its pane), in its folder.
        if (cmd.Name is "terminal split" && Environment.GetEnvironmentVariable("AVAMUX_TERMINAL_ID") is { Length: > 0 } pane) a["callerTerminal"] = pane;
        if (cmd.Name is "repo add" && a["path"] is JsonValue rp && rp.TryGetValue<string>(out var repoPath) && !repoPath.StartsWith('~'))
            a["path"] = Path.GetFullPath(repoPath);
        if (cmd.Name is "terminal new" && !a.ContainsKey("cwd")) a["cwd"] = Environment.CurrentDirectory;
        if (cmd.Name is "wiki write" && !a.ContainsKey("content") && !a.ContainsKey("file") && Console.IsInputRedirected)
            a["content"] = new JsonArray(await Console.In.ReadToEndAsync());

        var paths = new AppPaths();
        JsonObject? response;
        try { response = await IpcClient.SendAsync(paths.SocketPath, cmd.Name, a); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not talk to avamux: " + ex.Message);
            return 3;
        }
        if (response is null)
        {
            Console.Error.WriteLine("avamux is not running. Start it with `avamux` and try again.");
            return 3;
        }
        if (response["ok"]?.GetValue<bool>() != true)
        {
            Console.Error.WriteLine("Error: " + (response["error"]?.GetValue<string>() ?? "unknown error"));
            return 1;
        }
        var result = response["result"];
        var text = parsed.Json ? JsonUtil.Pretty(result) : CliParser.Format(result);
        if (text.Length > 0) Console.WriteLine(text);
        return 0;
    }
}
