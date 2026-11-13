using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avamux.Core;

namespace Avamux;

/// <summary>Bridges core services to the window. Safe to call from any thread.</summary>
public sealed class AppHost(MainWindow window, IClassicDesktopStyleApplicationLifetime lifetime) : IAppHost
{
    private static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a);
    }

    public Task SetClipboardAsync(string text) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (window.Clipboard is { } c) await c.SetTextAsync(text);
    });

    public Task<string?> GetClipboardAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
        window.Clipboard is { } c ? await c.TryGetTextAsync() : null);

    public void Navigate(string page) => Ui(() => window.Navigate(page));
    public string CurrentPage => window.CurrentPage;

    public void Activate() => Ui(() =>
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    });

    public void SelectTerminal(string id) => Ui(() =>
    {
        // A detached tab is selected in its own window, which comes to the front.
        if (Views.TerminalLayout.WindowOf(id) is { } w) { w.Page.Select(id); w.Activate(); }
        // The terminal of a custom page is shown by going to that page.
        else if (Views.CustomTerminalPage.PageKeyOf(id) is { } page) window.Navigate(page);
        else window.Terminals.Select(id);
    });
    public string? SelectedTerminal => window.Terminals.SelectedId;
    public void Notify(string title, string message) => Ui(() => window.Toast(title, message));
    public void Quit() => Ui(() => lifetime.Shutdown());
    public void ReloadDashboard() => Ui(() => window.Page<Views.DashboardPage>("dashboard")?.Reload());
    public void OpenDashboardDevTools() => Ui(() => window.Page<Views.DashboardPage>("dashboard")?.OpenDevTools());
    public void OpenWikiPage(string path) => Ui(() => window.Page<Views.WikiPage>("wiki")?.Open(path));
    public void Search(string query) => Ui(() => window.Page<Views.SearchPage>("search")?.Run(query));
    public void SelectRepository(string? id) => Ui(() => window.Page<Views.RepositoriesPage>("repositories")?.SelectRepo(id));
}
