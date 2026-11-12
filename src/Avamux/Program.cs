using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avamux.Core;
using Avamux.Core.Ipc;
using Avamux.Core.Logging;

namespace Avamux;

internal static class Program
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--gui") return Cli.RunAsync(args).GetAwaiter().GetResult();

        var paths = new AppPaths();
        // Only one instance: if one answers on the socket, bring it forward and leave.
        if (IpcClient.IsRunningAsync(paths.SocketPath).GetAwaiter().GetResult())
        {
            IpcClient.SendAsync(paths.SocketPath, "app activate", new JsonObject()).GetAwaiter().GetResult();
            Console.WriteLine("avamux is already running; brought it to the front. Use `avamux /?` for commands.");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.WriteCrash(paths.CrashDir, "AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("app", "Unobserved task exception", e.Exception); e.SetObserved(); };

        if (OperatingSystem.IsLinux())
        {
            // WebKitGTK's DMA-BUF renderer shows a blank view on NVIDIA's proprietary driver. These must be in the
            // native environment (Environment.SetEnvironmentVariable only changes .NET's copy) before GTK loads.
            if (Environment.GetEnvironmentVariable("AVAMUX_WEBKIT_GPU") != "1")
            {
                setenv("WEBKIT_DISABLE_DMABUF_RENDERER", "1", 0);
                setenv("WEBKIT_DISABLE_COMPOSITING_MODE", "1", 0);
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            var file = Log.WriteCrash(paths.CrashDir, "Main", ex);
            Console.Error.WriteLine($"avamux crashed: {ex.Message}\nCrash report: {file}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
