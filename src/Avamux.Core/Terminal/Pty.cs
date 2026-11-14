using System.Runtime.InteropServices;
using System.Text;

namespace Avamux.Core.Terminal;

/// <summary>
/// A child process attached to a pseudo-terminal (Linux and macOS).
/// Spawned with posix_spawn + POSIX_SPAWN_SETSID rather than fork (unsafe inside the CLR): the child becomes a
/// session leader and opens the slave device once as fd 0, which makes it the controlling terminal; fds 1 and 2
/// are dup'ed from it so all three share one open file description (tmux hands that descriptor to its server).
/// <para>
/// Window size: macOS rejects TIOCSWINSZ on the master until a slave is open, and resets the size when the slave
/// is first opened, so the size can only be set after the child has opened its end. The child therefore starts
/// parked on a pipe; the parent sets the size once posix_spawn has returned (the slave is open by then) and only
/// then releases the child to exec the real command, which can never observe a 0x0 terminal.
/// </para>
/// </summary>
public sealed unsafe class Pty : IDisposable
{
    private int _master;
    private readonly object _writeGate = new();
    private int _disposed;

    public int Pid { get; }

    private Pty(int master, int pid) { _master = master; Pid = pid; }

    private static bool Mac => OperatingSystem.IsMacOS();
    private static int O_NOCTTY => Mac ? 0x20000 : 0x100;
    private static int O_CLOEXEC => Mac ? 0x1000000 : 0x80000;
    private const int O_RDWR = 2;
    private static nuint TIOCSWINSZ => Mac ? 0x80087467u : 0x5414u;
    private static short POSIX_SPAWN_SETSID => (short)(Mac ? 0x0400 : 0x80);
    private const short POSIX_SPAWN_SETSIGDEF = 0x04;
    private const short POSIX_SPAWN_SETSIGMASK = 0x08;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    /// <summary>Descriptor the child waits on until the parent has set the window size.</summary>
    private const int ReadyFd = 3;

    // ioctl and fcntl are variadic. Apple's arm64 ABI passes variadic arguments on the stack, while P/Invoke
    // passes every argument as if it were named, i.e. in registers, so the callee would read garbage. Everywhere
    // else (Linux, macOS x64 incl. Rosetta) integer variadic arguments travel in the same registers as named ones.
    private static readonly bool StackVarArgs = Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize { public ushort Row, Col, XPixel, YPixel; }

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern byte* ptsname(int fd);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int ioctl_reg(int fd, nuint request, nint arg);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int fcntl_reg(int fd, int cmd, nint arg);
    // Six padding arguments fill x2-x7, pushing arg into the first stack slot: exactly where an Apple arm64
    // variadic callee looks for its first variadic argument.
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_stack(int fd, nuint request, nint p2, nint p3, nint p4, nint p5, nint p6, nint p7, nint arg);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_stack(int fd, int cmd, nint p2, nint p3, nint p4, nint p5, nint p6, nint p7, nint arg);
    [DllImport("libc", SetLastError = true)] private static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte* buf, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte* buf, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc", SetLastError = true)] private static extern int tcgetpgrp(int fd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(void* fa);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(void* fa);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(void* fa, int fd, byte* path, int oflag, uint mode);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(void* fa, int fd, int newfd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addclose(void* fa, int fd);
    [DllImport("libc")] private static extern int posix_spawnattr_init(void* attr);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(void* attr);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(void* attr, short flags);
    [DllImport("libc")] private static extern int posix_spawnattr_setsigdefault(void* attr, void* sigset);
    [DllImport("libc")] private static extern int posix_spawnattr_setsigmask(void* attr, void* sigset);
    [DllImport("libc")] private static extern int sigfillset(void* set);
    [DllImport("libc")] private static extern int sigemptyset(void* set);
    [DllImport("libc")] private static extern int posix_spawn(out int pid, byte* path, void* fileActions, void* attr, byte** argv, byte** envp);

    private static int Ioctl(int fd, nuint request, nint arg) =>
        StackVarArgs ? ioctl_stack(fd, request, 0, 0, 0, 0, 0, 0, arg) : ioctl_reg(fd, request, arg);

    private static int Fcntl(int fd, int cmd, nint arg) =>
        StackVarArgs ? fcntl_stack(fd, cmd, 0, 0, 0, 0, 0, 0, arg) : fcntl_reg(fd, cmd, arg);

    private static bool SetWindowSize(int fd, int cols, int rows)
    {
        var ws = new Winsize { Col = (ushort)cols, Row = (ushort)rows };
        return Ioctl(fd, TIOCSWINSZ, (nint)(&ws)) == 0;
    }

    /// <summary>Starts <paramref name="command"/> in <paramref name="cwd"/> on a new pty.</summary>
    public static Pty Spawn(string command, IReadOnlyList<string> args, string cwd, IDictionary<string, string> env, int cols, int rows)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Terminals are supported on Linux and macOS.");

        var master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (master < 0) throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()})");
        try
        {
            if (grantpt(master) != 0 || unlockpt(master) != 0)
                throw new IOException($"grantpt/unlockpt failed (errno {Marshal.GetLastPInvokeError()})");
            var slave = Marshal.PtrToStringUTF8((nint)ptsname(master)) ?? throw new IOException("ptsname failed");

            // Handshake pipe: the child blocks on the read end until the window size is in place.
            var ready = stackalloc int[2];
            if (pipe(ready) != 0) throw new IOException($"pipe failed (errno {Marshal.GetLastPInvokeError()})");
            int readyRead = ready[0], readyWrite = ready[1];
            // Keep both ends out of unrelated children. The child gets its copy via dup2, which clears the flag;
            // if the read end already is ReadyFd there is no dup2 to clear it, so it stays inheritable.
            Fcntl(readyWrite, F_SETFD, FD_CLOEXEC);
            if (readyRead != ReadyFd) Fcntl(readyRead, F_SETFD, FD_CLOEXEC);

            // sh waits for the parent (a byte, or EOF if the parent died), changes directory, then execs the
            // real command in place (same pid).
            var argv = new List<string>
            {
                "/bin/sh", "-c", $"read -r _ <&{ReadyFd}; exec {ReadyFd}<&-; cd \"$1\" 2>/dev/null; shift; exec \"$@\"", "sh", cwd, command,
            };
            argv.AddRange(args);
            var envp = env.Select(kv => kv.Key + "=" + kv.Value).ToList();

            var handles = new List<nint>();
            byte* Str(string s) { var p = Marshal.StringToCoTaskMemUTF8(s); handles.Add(p); return (byte*)p; }
            byte** Vec(List<string> items)
            {
                var v = (byte**)Marshal.AllocCoTaskMem(sizeof(nint) * (items.Count + 1));
                handles.Add((nint)v);
                for (var i = 0; i < items.Count; i++) v[i] = Str(items[i]);
                v[items.Count] = null;
                return v;
            }

            // The opaque libc structs are small on both platforms; 1 KiB is ample.
            var fa = stackalloc byte[1024];
            var attr = stackalloc byte[1024];
            var sigAll = stackalloc byte[1024];
            var sigNone = stackalloc byte[1024];
            try
            {
                posix_spawn_file_actions_init(fa);
                posix_spawnattr_init(attr);
                posix_spawn_file_actions_addopen(fa, 0, Str(slave), O_RDWR, 0);
                posix_spawn_file_actions_adddup2(fa, 0, 1);
                posix_spawn_file_actions_adddup2(fa, 0, 2);
                if (readyRead != ReadyFd) posix_spawn_file_actions_adddup2(fa, readyRead, ReadyFd);
                else posix_spawn_file_actions_addclose(fa, readyWrite);
                // .NET ignores SIGPIPE and blocks some signals; give the child a clean slate.
                sigfillset(sigAll);
                sigemptyset(sigNone);
                posix_spawnattr_setsigdefault(attr, sigAll);
                posix_spawnattr_setsigmask(attr, sigNone);
                posix_spawnattr_setflags(attr, (short)(POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK));
                var rc = posix_spawn(out var pid, Str("/bin/sh"), fa, attr, Vec(argv), Vec(envp));
                if (rc != 0) throw new IOException($"posix_spawn failed (errno {rc})");

                // The file actions have run, so the child holds the slave open: the size now sticks on macOS too.
                SetWindowSize(master, cols, rows);
                byte go = (byte)'\n';
                write(readyWrite, &go, 1);
                return new Pty(master, pid);
            }
            finally
            {
                close(readyRead);
                close(readyWrite);
                posix_spawn_file_actions_destroy(fa);
                posix_spawnattr_destroy(attr);
                foreach (var h in handles) Marshal.FreeCoTaskMem(h);
            }
        }
        catch { close(master); throw; }
    }

    /// <summary>Blocking read. Returns 0 when the child side has closed.</summary>
    public int Read(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            while (true)
            {
                var fd = _master;
                if (fd < 0) return 0;
                var n = read(fd, p, (nuint)buffer.Length);
                if (n >= 0) return (int)n;
                var errno = Marshal.GetLastPInvokeError();
                if (errno == 4) continue; // EINTR
                return 0; // EIO: slave closed
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_writeGate)
        {
            fixed (byte* p = data)
            {
                var off = 0;
                while (off < data.Length)
                {
                    var fd = _master;
                    if (fd < 0) return;
                    var n = write(fd, p + off, (nuint)(data.Length - off));
                    if (n < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno is 4 or 11 or 35) { if (errno != 4) Thread.Sleep(1); continue; }
                        return;
                    }
                    off += (int)n;
                }
            }
        }
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        if (_master < 0 || cols < 1 || rows < 1) return;
        SetWindowSize(_master, cols, rows);
    }

    /// <summary>Process group currently in the foreground of this terminal, or -1.</summary>
    public int ForegroundProcessGroup() => _master < 0 ? -1 : tcgetpgrp(_master);

    public void Signal(int signal) { if (Pid > 0) kill(-Pid, signal); }

    /// <summary>Blocks until the child exits and returns its exit code (128+signal when killed).</summary>
    public int WaitForExit()
    {
        while (true)
        {
            var r = waitpid(Pid, out var status, 0);
            if (r == Pid) return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            if (r < 0 && Marshal.GetLastPInvokeError() == 4) continue;
            return -1;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var fd = Interlocked.Exchange(ref _master, -1);
        if (fd >= 0) close(fd);
    }
}
