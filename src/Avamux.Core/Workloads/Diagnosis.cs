using System.Text;
using System.Text.RegularExpressions;

namespace Avamux.Core.Workloads;

/// <summary>Reads the end of a log and says, in plain words, why a model server failed (when it is one of the usual reasons).</summary>
public static partial class Diagnosis
{
    private static readonly (Regex Pattern, string Why)[] Known =
    [
        (new(@"out of memory|cudaMalloc failed|failed to allocate .* buffer|CUDA error: out of memory|ggml_backend_cuda_buffer_type_alloc_buffer: allocating .* failed", RegexOptions.IgnoreCase),
            "The GPU ran out of memory. Free some (close what else is on the card), or give the GPU fewer layers or experts, or use a smaller context."),
        (new(@"RPC.*(version mismatch|protocol version)|incompatible RPC", RegexOptions.IgnoreCase),
            "The RPC server was built from a different llama.cpp commit. Rebuild one side at the other's commit."),
        (new(@"(Failed to connect|Connection refused).*(rpc|50052)|ggml_rpc.*connect", RegexOptions.IgnoreCase),
            "The RPC server did not answer. Is the other machine on, and its rpc-server running?"),
        (new(@"address already in use|couldn't bind|failed to bind|bind\(\) failed", RegexOptions.IgnoreCase),
            "Something else is already using the port. Stop it first, or use another port."),
        (new(@"something is already listening on :(\d+)", RegexOptions.IgnoreCase),
            "Another server is already on that port. Stop it first (avamux can: use Swap)."),
        (new(@"already running \(pid (\d+)\)", RegexOptions.IgnoreCase), "It is already running."),
        (new(@"cannot open shared object file|error while loading shared libraries", RegexOptions.IgnoreCase),
            "A library the program needs is missing (often the CUDA runtime next to a fresh llama.cpp build)."),
        (new(@"failed to load model|error loading model|unable to load model|gguf_init_from_file.*failed|model not found", RegexOptions.IgnoreCase),
            "The model could not be loaded: the file is missing, incomplete, or this llama.cpp build does not know its architecture."),
        (new(@"unknown model architecture", RegexOptions.IgnoreCase), "This llama.cpp build does not know the model's architecture. A newer build (or the fork the model needs) can load it."),
        (new(@"error: invalid argument|unknown argument|error: unrecognized", RegexOptions.IgnoreCase), "The server did not accept one of its arguments (this build may not have that option)."),
        (new(@"No such file or directory", RegexOptions.IgnoreCase), "A file it needs is not there (the program, the model, or a script)."),
        (new(@"Killed|oom-kill|killed process", RegexOptions.IgnoreCase), "The system ran out of RAM and ended it."),
    ];

    /// <summary>The reason, or null when the log does not say anything recognisable.</summary>
    public static string? Explain(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;
        // The newest lines say most about how it ended.
        var lines = log.Split('\n');
        for (var i = lines.Length - 1; i >= Math.Max(0, lines.Length - 120); i--)
            foreach (var (pattern, why) in Known)
                if (pattern.IsMatch(lines[i])) return why;
        return null;
    }

    /// <summary>The last error-looking line, for when nothing known matched.</summary>
    public static string? LastErrorLine(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;
        foreach (var line in log.Split('\n').Reverse().Take(80))
            if (ErrorLine().IsMatch(line)) return line.Trim().Length > 300 ? line.Trim()[..300] + "…" : line.Trim();
        return null;
    }

    [GeneratedRegex(@"\b(error|failed|fatal|abort|exception)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    /// <summary>The last <paramref name="lines"/> lines of a file, read from its end (logs can be large).</summary>
    public static string Tail(string? path, int lines = 200)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var chunk = (int)Math.Min(f.Length, Math.Max(16384, lines * 400L));
            f.Seek(-chunk, SeekOrigin.End);
            var buffer = new byte[chunk];
            f.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer).Replace("\r\n", "\n");
            var all = text.Split('\n');
            if (chunk < f.Length && all.Length > 1) all = all[1..]; // the first line was cut
            return string.Join('\n', all.TakeLast(lines)).TrimEnd();
        }
        catch { return ""; }
    }
}
