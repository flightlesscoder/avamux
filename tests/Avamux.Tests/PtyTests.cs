using System.Text;
using Avamux.Core.Terminal;

namespace Avamux.Tests;

public class PtyTests
{
    private static string RunToEnd(Pty pty)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        int n;
        while ((n = pty.Read(buf)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        pty.WaitForExit();
        return sb.ToString();
    }

    private static Dictionary<string, string> Env() => new() { ["PATH"] = "/usr/bin:/bin", ["TERM"] = "xterm" };

    [Fact]
    public void Child_sees_the_requested_window_size_from_its_first_instruction()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        // Repeat: a size applied late would only lose the race some of the time.
        for (var i = 0; i < 20; i++)
        {
            using var pty = Pty.Spawn("stty", ["size"], "/", Env(), cols: 101, rows: 33);
            Assert.Contains("33 101", RunToEnd(pty));
        }
    }

    [Fact]
    public void Resize_is_visible_to_the_child()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var pty = Pty.Spawn("sh", ["-c", "read -r _; stty size"], "/", Env(), cols: 80, rows: 24);
        pty.Resize(132, 43);
        pty.Write("\n");
        Assert.Contains("43 132", RunToEnd(pty));
    }

    [Fact]
    public void Child_starts_in_cwd_without_the_handshake_descriptor()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        // fd 3 must be closed again before the real command runs.
        using var pty = Pty.Spawn("sh", ["-c", "pwd; if (: <&3) 2>/dev/null; then echo fd3-open; else echo fd3-closed; fi"], "/tmp", Env(), 80, 24);
        var output = RunToEnd(pty);
        Assert.Contains("/tmp", output);
        Assert.Contains("fd3-closed", output);
    }
}
