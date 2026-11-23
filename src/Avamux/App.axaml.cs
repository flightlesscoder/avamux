using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.Diagnostics;
using Avamux.Core.Ipc;
using Avamux.Core.Logging;

namespace Avamux;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private IpcServer? _ipc;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["MonoFont"] = Views.Ui.Mono;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Timeline.Note("ui toolkit ready");
            Services = new AppServices();
            Timeline.Note("services created");
            Log.Info("app", $"avamux starting (pid {Environment.ProcessId}, data {Services.Paths.DataDir})");
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                // Code typed into C# Interactive that throws later (in an event handler, say) reports there.
                if (Avamux.Scripting.InteractivePage.TryReport(e.Exception)) { e.Handled = true; return; }
                // Keep the app alive on UI-thread exceptions, but leave a crash report.
                var file = Log.WriteCrash(Services.Paths.CrashDir, "UI thread", e.Exception);
                Log.Error("app", $"Unhandled UI exception (report: {file})", e.Exception);
                e.Handled = true;
            };
            // The web server must be listening before any web view navigates to it.
            Services.StartAsync().GetAwaiter().GetResult();
            Timeline.Note("services started");
            Views.ThemeManager.Initialize(this);
            Services.Config.Changed += () => Dispatcher.UIThread.Post(() => Views.ThemeManager.Apply(this));
            var window = new MainWindow();
            Timeline.Note("window created");
            window.Opened += (_, _) =>
            {
                Timeline.NoteOnce("window opened");
                window.RequestAnimationFrame(_ => Timeline.NoteOnce("first frame"));
            };
            Services.Host = new AppHost(window, desktop);
            desktop.MainWindow = window;
            Avamux.Browser.BrowserHost.Instance.Start();
            Avamux.Core.Terminal.TerminalManager.TabOf = Views.TerminalLayout.TabOf;
            _ipc = new IpcServer(Services.Paths.SocketPath, Services.Commands);
            _ipc.Start();
            Timeline.Note("commands ready");
            desktop.ShutdownRequested += (_, _) => Shutdown();
            desktop.Exit += (_, _) => Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private int _down;
    private void Shutdown()
    {
        if (Interlocked.Exchange(ref _down, 1) == 1) return;
        Timeline.Note("shutdown started");
        Log.Info("app", "avamux shutting down");
        _ipc?.Dispose();
        try
        {
            // Closed on the thread pool: this runs on the UI thread, which waits here, and continuations of the web
            // server's shutdown that came back to the UI thread would wait for it in turn until the time ran out.
            if (!Task.Run(() => Services.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3))) Timeline.Note("services still closing after 3 s");
        }
        catch { }
        Timeline.Note("services closed");
        Timeline.Save();
    }
}
