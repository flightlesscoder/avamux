using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core.Logging;

namespace Avamux.Core.Ai;

/// <summary>
/// A long-running program spoken to in JSON lines: one object per line on stdin and stdout. Lines are split on '\n'
/// only (a JSON string may hold U+2028), stderr is kept for error messages, and disposing ends the whole process tree.
/// </summary>
public sealed class JsonLinesProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Task _reader, _errors;
    private readonly string _label;
    private int _disposed;

    public JsonLinesProcess(string label, string file, IEnumerable<string> args, string cwd, IReadOnlyDictionary<string, string>? env, Action<JsonObject> onLine, Action<int>? onExit = null)
    {
        _label = label;
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        // Never let a terminal's agent variables leak into what avamux starts.
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { _process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException($"Could not start {file}: {ex.Message}. Is it installed and on PATH?", ex); }
        Log.Info("ai", $"{label}: started {file} (pid {_process.Id}) {string.Join(' ', psi.ArgumentList.Select(Quote))}");
        _reader = Task.Run(async () =>
        {
            var stream = _process.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            var line = new MemoryStream();
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buffer); } catch { break; }
                if (n <= 0) break;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    Deliver(line, onLine);
                    line.SetLength(0);
                    start = i + 1;
                }
                line.Write(buffer, start, n - start);
            }
            if (line.Length > 0) Deliver(line, onLine);
            try { await _process.WaitForExitAsync(); } catch { }
            int code;
            try { code = _process.ExitCode; } catch { code = -1; }
            Log.Info("ai", $"{label}: exited with {code}");
            onExit?.Invoke(code);
        });
        _errors = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } l)
                    lock (_stderr) { if (_stderr.Length < 64 * 1024) _stderr.AppendLine(l); }
            }
            catch { }
        });
    }

    private static string Quote(string a) => a.Length > 80 ? "\"" + a[..77] + "…\"" : a.Contains(' ') ? "\"" + a + "\"" : a;

    private void Deliver(MemoryStream line, Action<JsonObject> onLine)
    {
        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
        if (text.Length == 0) return;
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch
        {
            // Not JSON (a warning printed to stdout): keep it with the error output.
            lock (_stderr) { if (_stderr.Length < 64 * 1024) _stderr.AppendLine(text); }
            return;
        }
        if (node is not JsonObject o) return;
        try { onLine(o); }
        catch (Exception ex) { Log.Error("ai", $"{_label}: handling a line failed", ex); }
    }

    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }
    public int Pid => _process.Id;

    /// <summary>What the program wrote to stderr (and stdout lines that were not JSON), latest last.</summary>
    public string Errors { get { lock (_stderr) return _stderr.ToString().Trim(); } }

    public async Task SendAsync(JsonNode message)
    {
        if (HasExited) throw new InvalidOperationException($"{_label} is not running any more." + (Errors.Length > 0 ? " " + LastLines(Errors) : ""));
        var text = message.ToJsonString(JsonUtil.Compact) + "\n";
        await _write.WaitAsync();
        try
        {
            await _process.StandardInput.WriteAsync(text);
            await _process.StandardInput.FlushAsync();
        }
        finally { _write.Release(); }
    }

    public static string LastLines(string text, int lines = 6)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>Closes stdin (the program may finish by itself) and waits up to <paramref name="wait"/> before ending it.</summary>
    public async Task StopAsync(TimeSpan wait)
    {
        try { _process.StandardInput.Close(); } catch { }
        try { await _process.WaitForExitAsync().WaitAsync(wait); } catch { }
        Kill();
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await StopAsync(TimeSpan.FromSeconds(2));
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { await _errors.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        _process.Dispose();
    }
}
