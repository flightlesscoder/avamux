using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using Avamux.Core.Repos;
using Avamux.Core.Wiki;

namespace Avamux.Views;

/// <summary>
/// The wiki's git screen: the Repositories page working on the wiki folder alone, in a window of its own.
/// Staging, the commit dialog, history (view, revert, reset, check out, cherry-pick, tag) and branches are the
/// very same code the Repositories page runs.
/// </summary>
public sealed class WikiGitWindow : AppWindow
{
    private static WikiGitWindow? _open;

    private WikiGitWindow()
    {
        Icon = AppIcon.Window;
        Title = "Wiki in git — avamux";
        Width = 1240; Height = 780; MinWidth = 720; MinHeight = 420;
        Page = new RepositoriesPage(App.Services.Wiki.Repo);
        Body = Page;
        // The page listens to the services for as long as it lives, so the window is put away rather than
        // thrown away when the user closes it; it only really closes with the app.
        Closing += (_, e) =>
        {
            if (e.CloseReason != WindowCloseReason.WindowClosing || e.IsProgrammatic) return;
            // With the main window gone this is the last one: closing it has to end the app.
            if (Ui.Main is not { IsVisible: true }) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
    }

    public RepositoriesPage Page { get; }

    /// <summary>Shows the window on a tab ("changes", "history", "branches"), or on one commit.</summary>
    public static void Show(Control? near, string tab = "changes", string? commit = null)
    {
        var window = _open ??= new WikiGitWindow();
        if (!window.IsVisible && near is not null && TopLevel.GetTopLevel(near) is Window w)
        {
            window.Width = Math.Max(900, w.Width * 0.9);
            window.Height = Math.Max(520, w.Height * 0.9);
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        if (commit is not null) window.Page.Reveal(WikiService.RepoId, "commit:" + commit);
        else window.Page.ShowTab(tab);
    }
}

/// <summary>
/// The Git button of the wiki's toolbar: it shows the branch and how many files changed, and opens a menu to
/// commit, switch or create a branch, see history, and put the open page back to an earlier version.
/// </summary>
internal sealed class WikiGitMenu
{
    private readonly Control _owner;
    private readonly Func<string?> _path;
    private readonly Action _save;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private int _generation;

    private static RepoService Repos => App.Services.Repos;
    private static RepoEntry Repo => App.Services.Wiki.Repo;

    /// <param name="path">The wiki path of the page being shown, or null.</param>
    /// <param name="save">Saves unsaved edits, so that git sees what the editor shows.</param>
    public WikiGitMenu(Control owner, Func<string?> path, Action save)
    {
        _owner = owner; _path = path; _save = save;
        _label.Text = "Git";
        Button = new Button
        {
            Classes = { "flat" }, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new PathIcon { Data = Icons.SourceBranch }, _label } },
        };
        Button.Click += (_, _) => _ = Ui.TryAsync(_owner, OpenAsync, "Git did not answer");
    }

    public Button Button { get; }

    /// <summary>Brings the button's text up to date: the branch, and the number of changed files when there are any.</summary>
    public async Task RefreshAsync()
    {
        var gen = ++_generation;
        string text, tip;
        try
        {
            if (!await Repos.IsRepositoryAsync(Repo)) (text, tip) = ("Git", "The wiki's history is not kept yet. Click to start keeping it with git.");
            else
            {
                var status = await Repos.StatusAsync(Repo);
                var changed = status.Changes.Select(c => c.Path).Distinct().Count();
                text = (status.Branch ?? "detached") + (changed > 0 ? $" · {changed}" : "");
                tip = $"The wiki is on {status.Branch ?? "a detached commit"}" + (changed > 0 ? $", with {changed} file{(changed == 1 ? "" : "s")} changed since the last commit." : ", with nothing to commit.") + "\nClick to commit, switch branch or see history.";
            }
        }
        catch (Exception ex) { (text, tip) = ("Git", ex.Message); }
        if (gen != _generation) return;
        _label.Text = text;
        ToolTip.SetTip(Button, tip);
    }

    private async Task OpenAsync()
    {
        _save();
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        if (!await Repos.IsRepositoryAsync(Repo))
        {
            menu.Items.Add(Ui.Item("Start Keeping History with Git", async () =>
            {
                await Repos.InitAsync(Repo, GitIgnore.WikiDefault);
                await RefreshAsync();
                Ui.Toast(_owner, "The wiki is now a git repository", "Commit to record its first version. Its .gitignore leaves out system and editor clutter.");
            }, icon: Icons.SourceBranch));
            menu.Items.Add(new MenuItem { Header = $"Runs git init in {Repo.Path}, and writes a .gitignore if there is none", IsEnabled = false });
            menu.Open(Button);
            return;
        }

        var status = await Repos.StatusAsync(Repo);
        var branches = (await Repos.BranchesAsync(Repo, remotes: false)).Where(b => !b.IsRemote).ToList();
        var changed = status.Changes.Select(c => c.Path).Distinct().Count();
        var path = _path();
        var mine = path is null ? null : status.Changes.FirstOrDefault(c => c.Path == path);

        menu.Items.Add(new MenuItem { Header = $"On {status.Branch ?? "a detached commit"} · {(changed == 0 ? "nothing to commit" : $"{changed} changed file{(changed == 1 ? "" : "s")}")}", IsEnabled = false });
        menu.Items.Add(Ui.Item("Commit All Changes…", async () => { if (await RepositoriesPage.CommitDialogAsync(_owner, Repo, everything: true)) Ui.Toast(_owner, "Committed", $"The wiki's changes are recorded on {status.Branch}."); }, changed > 0, Icons.Check));
        menu.Items.Add(Ui.Item("Review Changes…", () => WikiGitWindow.Show(_owner, "changes"), icon: Icons.PencilCircle));
        menu.Items.Add(Ui.Item("History…", () => WikiGitWindow.Show(_owner, "history"), icon: Icons.TextBoxOutline));
        menu.Items.Add(Ui.Item("Branches…", () => WikiGitWindow.Show(_owner, "branches"), icon: Icons.SourceBranch));
        menu.Items.Add(new Separator());

        var switchTo = new MenuItem { Header = "Switch Branch" };
        foreach (var b in branches)
        {
            var item = Ui.Item(b.Name, async () =>
            {
                await Repos.SwitchAsync(Repo, b.Name);
                Ui.Toast(_owner, "Switched branch", $"The wiki is now on {b.Name}");
            }, !b.IsCurrent, b.IsCurrent ? Icons.Check : null);
            switchTo.Items.Add(item);
        }
        if (branches.Count > 0) switchTo.Items.Add(new Separator());
        switchTo.Items.Add(Ui.Item("New Branch…", NewBranchAsync, icon: Icons.Plus));
        menu.Items.Add(switchTo);
        menu.Items.Add(new Separator());

        var name = path is null ? "This Page" : System.IO.Path.GetFileName(path);
        menu.Items.Add(Ui.Item($"History of {name}…", () => WikiFileHistory.Show(_owner, path!), path is not null, Icons.CalendarClock));
        menu.Items.Add(Ui.Item($"Discard Changes to {name}…", async () =>
        {
            if (!await Ui.ConfirmAsync(Ui.Owner(_owner), "Discard changes", $"Put {path} back the way it was in the last commit? What was written since is lost.", "Discard")) return;
            if (mine!.Staged) await Repos.UnstageAsync(Repo, path!);
            await Repos.DiscardAsync(Repo, path!, untracked: false);
            Ui.Toast(_owner, "Changes discarded", path!);
        }, mine is { Untracked: false }, Icons.Undo));
        menu.Open(Button);
    }

    private async Task NewBranchAsync()
    {
        var name = await Ui.PromptAsync(Ui.Owner(_owner), "New branch", "Name of the new branch (the wiki switches to it)");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Repos.CreateBranchAsync(Repo, name.Trim());
        Ui.Toast(_owner, "Branch created", $"The wiki is now on {name.Trim()}");
    }
}

/// <summary>The commits that changed one wiki page, the page as each of them left it, and a way back to any of them.</summary>
internal sealed class WikiFileHistory : AppWindow
{
    private readonly string _path;
    private readonly ListBox _commits = new() { Classes = { "plain" } };
    private readonly TextEditor _text = new()
    {
        IsReadOnly = true, WordWrap = true, FontFamily = Ui.Mono, FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(14, 10),
        Background = Ui.Tk("bgEditor"), Foreground = Ui.Tk("textCode"), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
    };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _restore, _view;
    private int _generation;

    private static RepoService Repos => App.Services.Repos;
    private static RepoEntry Repo => App.Services.Wiki.Repo;
    private RepoCommit? Selected => (_commits.SelectedItem as ListBoxItem)?.Tag as RepoCommit;

    private WikiFileHistory(string path)
    {
        _path = path;
        Icon = AppIcon.Window;
        Title = $"History of {path} — avamux wiki";
        Width = 1040; Height = 680; MinWidth = 560; MinHeight = 320;
        _text.TextArea.TextView.LineTransformers.Add(new MarkdownColorizer());
        _restore = Ui.IconButton(Icons.Undo, "Put the page back the way this commit left it. It becomes a change to commit, so nothing is lost.", () => _ = Ui.TryAsync(this, RestoreAsync, "Could not restore the page"), "Restore This Version");
        _view = Ui.IconButton(Icons.Eye, "Show everything this commit changed", () => { if (Selected is { } c) WikiGitWindow.Show(this, commit: c.Hash); }, "View Commit");
        _restore.IsEnabled = _view.IsEnabled = false;
        _commits.SelectionChanged += (_, _) => _ = ShowAsync();
        Body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("340,*"),
            Children =
            {
                new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0), Background = Ui.Tk("bgSide"), Child = _commits },
                new DockPanel
                {
                    Children =
                    {
                        new Border
                        {
                            Padding = new Thickness(12, 6), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                            Child = new DockPanel { Children = { Ui.Row(4, _view, _restore).Dock(Dock.Right), _title } },
                        }.Dock(Dock.Top),
                        _text,
                    },
                }.At(0, 1),
            },
        };
        Opened += (_, _) => _ = Ui.TryAsync(this, LoadAsync, "Could not read the history");
    }

    public static void Show(Control near, string path)
    {
        var window = new WikiFileHistory(path);
        if (TopLevel.GetTopLevel(near) is Window owner) window.Show(owner); else window.Show();
    }

    private async Task LoadAsync()
    {
        var log = await Repos.FileLogAsync(Repo, _path);
        _commits.Items.Clear();
        foreach (var c in log)
        {
            _commits.Items.Add(new ListBoxItem
            {
                Tag = c,
                Content = Ui.Column(2,
                    Ui.Apply(Ui.Text(c.Subject), t => { t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; }),
                    Ui.Apply(Ui.Text($"{c.Date} · {c.Author} · {c.Hash}", "dim", 11.5), t => { t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; })),
            });
        }
        if (log.Count == 0) { _title.Text = "No commit has this page yet. Commit the wiki to start its history."; return; }
        _commits.SelectedIndex = 0;
    }

    private async Task ShowAsync()
    {
        var gen = ++_generation;
        _restore.IsEnabled = _view.IsEnabled = Selected is not null;
        if (Selected is not { } c) return;
        var text = await Repos.FileAtAsync(Repo, _path, c.Hash);
        if (gen != _generation) return;
        _title.Text = text is null ? $"{c.Hash} · the page did not exist under this name here (it was renamed or deleted)" : $"{c.Hash} · {c.Subject}";
        _text.Text = text ?? "";
        _restore.IsEnabled = text is not null;
    }

    private async Task RestoreAsync()
    {
        if (Selected is not { } c) return;
        if (!await Ui.ConfirmAsync(this, "Restore this version", $"Put {_path} back the way it was in {c.Hash} (\"{c.Subject}\")? The page changes; commit it to keep the restored version.", "Restore")) return;
        await Repos.RestoreFileAsync(Repo, _path, c.Hash);
        Ui.Toast(this, "Version restored", $"{_path} is as it was in {c.Hash}");
        Close();
    }
}
