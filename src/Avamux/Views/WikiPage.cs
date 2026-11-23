using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.Logging;
using Avamux.Core.Theming;
using Avamux.Core.Wiki;
using AvaloniaEdit;

namespace Avamux.Views;

/// <summary>A wiki entry in a window of its own.</summary>
public sealed class WikiWindow : AppWindow
{
    public WikiWindow(string path)
    {
        Icon = AppIcon.Window;
        Width = 1040; Height = 720; MinWidth = 420; MinHeight = 300;
        Page = new WikiPage(path);
        Body = Page;
        Title = path + " — avamux wiki";
        Page.Opened += p => Title = (p ?? "Wiki") + " — avamux wiki";
        Closing += (_, _) => Page.Leave();
    }

    public WikiPage Page { get; }

    public static void Show(string path, Control? near = null)
    {
        var window = new WikiWindow(path);
        if (near is not null && TopLevel.GetTopLevel(near) is Window w) { window.Width = Math.Max(640, w.Width * 0.75); window.Height = Math.Max(420, w.Height * 0.8); }
        window.Show();
    }
}

/// <summary>Folder tree, editor and live preview side by side. Also previews pictures, PDFs, media and text files.</summary>
public sealed class WikiPage : UserControl, IPage
{
    private readonly TreeView _tree = new() { Background = Brushes.Transparent };
    private readonly TextEditor _editor = new()
    {
        WordWrap = true, FontFamily = Ui.Mono, FontSize = 13, BorderThickness = new Thickness(0),
        Padding = new Thickness(14, 10), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
    };
    private readonly MarkdownColorizer _colorizer = new();
    private readonly EditorPictures _pictures;
    private readonly DispatcherTimer _autoSave = new();
    private readonly NativeWebView _preview = new();
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _split = new() { ColumnDefinitions = new ColumnDefinitions("*,*") };
    private readonly Grid _layout = new() { ColumnDefinitions = new ColumnDefinitions("250,*") };
    private readonly Border _editorHost, _previewHost, _treeHost;
    private readonly Action _renderSoon;
    private readonly HashSet<string> _collapsed = [];
    private readonly HashSet<string> _opened = [];   // big folders the user opened (they start closed)
    private readonly bool _standalone;
    private readonly Button[] _modeButtons;
    private readonly WikiGitMenu _git;
    private readonly Button _saveButton, _imageButton, _undoButton, _redoButton, _backButton, _forwardButton;
    private const int MaxHistory = 50;
    private readonly List<string> _back = [], _forward = [];   // wiki paths, most recent last
    private bool _navigating;
    private string? _path;            // wiki-relative path of the open file
    private string? _selectedFolder;  // folder new pages go into
    private string _saved = "";
    private bool _loading, _previewReady, _buildingTree, _editable;
    private int _mode = 1;            // 0 edit, 1 split, 2 preview — the user's choice for editable files
    private double? _pendingPreviewScroll;
    private DateTime _lastOwnWrite;

    private static WikiService Wiki => App.Services.Wiki;
    private static UiStateStore State => App.Services.Ui;
    private bool Dirty => _path is not null && _editable && _editor.Text != _saved;

    /// <summary>Raised with the wiki path whenever another file is opened.</summary>
    public event Action<string?>? Opened;

    /// <param name="standalone">When given, the page shows this file without the folder tree (a detached window).</param>
    public WikiPage(string? standalone = null)
    {
        _standalone = standalone is not null;
        _editor.Background = Ui.Tk("bgEditor");
        _editor.Foreground = Ui.Tk("textCode");
        TintSelection(_editor);
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _pictures = new EditorPictures(_editor,
            folder: () => _path is { } open && WikiService.IsMarkdown(open) ? FolderOf(open) : null,
            fullPath: p => { try { return Wiki.Resolve(p); } catch { return null; } },
            options: () =>
            {
                var w = App.Services.Config.Settings.Wiki;
                return new EditorPictureOptions(w.EditorPictures, Math.Clamp(w.EditorPictureHeight, 40, 2000), (long)(Math.Max(0, w.ClickToPreviewMegabytes) * 1024 * 1024));
            });
        _editorHost = new Border { Child = _editor, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) };
        _previewHost = new Border { Child = _preview };
        _split.Children.Add(_editorHost);
        _split.Children.Add(_previewHost.At(0, 1));

        _renderSoon = Ui.Debounced(() => _ = RenderAsync(), 200);
        _editor.TextChanged += (_, _) =>
        {
            _colorizer.Invalidate();
            if (_loading) return;
            UpdateTitle(); UpdateUndoButtons(); _renderSoon(); RestartAutoSave();
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { e.Handled = true; Save(); }
        };
        // Paste goes through here first: a picture or a copied file becomes a file next to the page plus a link to it.
        _editor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            var paste = e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift;
            if (!paste || _editor.IsReadOnly) return;
            e.Handled = true;
            _ = PasteAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Right-click: the usual editing commands, enabled for what the selection and the page allow.
        var editMenu = new ContextMenu();
        MenuItem cut = Ui.Item("Cut", () => _editor.Cut()), copy = Ui.Item("Copy", () => _editor.Copy()),
            paste = Ui.Item("Paste", () => _ = PasteAsync()), delete = Ui.Item("Delete", () => _editor.Delete()),
            undo = Ui.Item("Undo", () => _editor.Undo()), redo = Ui.Item("Redo", () => _editor.Redo()),
            selectAll = Ui.Item("Select All", () => _editor.SelectAll());
        foreach (var (item, keys) in new[] { (cut, "Ctrl+X"), (copy, "Ctrl+C"), (paste, "Ctrl+V"), (undo, "Ctrl+Z"), (redo, "Ctrl+Y"), (selectAll, "Ctrl+A") })
            item.InputGesture = KeyGesture.Parse(keys);
        foreach (var o in new object[] { undo, redo, new Separator(), cut, copy, paste, delete, new Separator(), selectAll }) editMenu.Items.Add(o);
        editMenu.Opening += (_, _) =>
        {
            var selected = _editor.SelectionLength > 0;
            var writable = !_editor.IsReadOnly;
            cut.IsEnabled = delete.IsEnabled = selected && writable;
            copy.IsEnabled = selected;
            paste.IsEnabled = writable;
            undo.IsEnabled = writable && _editor.CanUndo;
            redo.IsEnabled = writable && _editor.CanRedo;
        };
        _editor.TextArea.ContextMenu = editMenu;
        _editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            if (!_loading && _path is not null && _editable) State.RememberWiki(_path, editor: _editor.VerticalOffset, caret: _editor.CaretOffset);
        };
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); if (Dirty) Save(); };
        // Alt+Left / Alt+Right and the mouse's back and forward buttons walk the history.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt) return;
            if (e.Key == Key.Left) { e.Handled = true; GoBack(); }
            else if (e.Key == Key.Right) { e.Handled = true; GoForward(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            var kind = e.InitialPressMouseButton;
            if (kind == MouseButton.XButton1) { e.Handled = true; GoBack(); }
            else if (kind == MouseButton.XButton2) { e.Handled = true; GoForward(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Files dropped anywhere on the page are copied next to it and linked, like a paste.
        DragDrop.SetAllowDrop(this, true);
        // Drag and drop events only bubble (a tunnel handler never runs), and the editor under the pointer may have
        // handled them already as a text drop: listen to handled ones too.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _tree.SelectionChanged += (_, _) =>
        {
            if (_buildingTree || (_tree.SelectedItem as TreeViewItem)?.Tag is not WikiNode node) return;
            _selectedFolder = node.IsFolder ? node.Path : FolderOf(node.Path);
            if (!node.IsFolder && node.Path != _path) Open(node.Path);
        };

        WebViews.FocusOnClick(_preview);

        _preview.EnvironmentRequested += (_, e) => WebViews.Configure(e);
        _preview.WebMessageReceived += OnPreviewMessage;
        _preview.NavigationCompleted += (_, e) => { if (e.IsSuccess) _ = PreviewStartedAsync(); };
        _preview.Source = new Uri(App.Services.Web.WikiPreviewUrl);

        var treeBar = new Border
        {
            Padding = new Thickness(8, 5), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Ui.Row(0,
                Ui.IconButton(Icons.Plus, "New page in the selected folder", () => _ = NewPageAsync(), "Page"),
                Ui.IconButton(Icons.FolderOpen, "New folder", () => _ = NewFolderAsync(), "Folder"),
                Ui.IconButton(Icons.Pencil, "Rename or move the selected page or folder", () => _ = RenameAsync(SelectedNode)),
                Ui.IconButton(Icons.Delete, "Delete the selected page or folder", () => _ = DeleteAsync(SelectedNode))),
        };
        _modeButtons = new[] { "Edit", "Split", "Preview" }.Select((label, i) => Ui.TextButton(label, () => { _mode = i; ApplyMode(); })).ToArray();
        _backButton = Ui.IconButton(Icons.ArrowLeft, "Back (Alt+Left)", GoBack);
        _forwardButton = Ui.IconButton(Icons.ArrowRight, "Forward (Alt+Right)", GoForward);
        _undoButton = Ui.IconButton(Icons.Undo, "Undo (Ctrl+Z)", () => { _editor.Undo(); _editor.Focus(); }, "Undo");
        _redoButton = Ui.IconButton(Icons.Redo, "Redo (Ctrl+Y)", () => { _editor.Redo(); _editor.Focus(); }, "Redo");
        _saveButton = Ui.IconButton(Icons.ContentSave, "Save (Ctrl+S)", Save, "Save");
        _git = new WikiGitMenu(this, () => _path, () => { if (Dirty) Save(); });
        _imageButton = Ui.IconButton(Icons.Image, "Copy a picture or file into the wiki and link it here. Pictures can also be pasted straight into the page.", () => _ = InsertFileAsync(), "Insert");
        var editBar = new Border
        {
            Padding = new Thickness(12, 5), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Row(4,
                        _backButton, _forwardButton,
                        Ui.Row(0, _modeButtons),
                        _undoButton, _redoButton,
                        _imageButton,
                        _saveButton,
                        _git.Button,
                        Ui.IconButton(Icons.OpenInNew, _standalone ? "Open this entry in another window" : "Open this entry in a window of its own", () => { if (_path is not null) { if (Dirty) Save(); WikiWindow.Show(_path, this); } }, _standalone ? null : "Window")).Dock(Dock.Right),
                    _title,
                },
            },
        };

        _treeHost = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0), Background = Ui.Tk("bgSide"), Child = new DockPanel { Children = { treeBar.Dock(Dock.Top), _tree } } };
        _layout.Children.Add(_treeHost);
        _layout.Children.Add(new DockPanel { Children = { editBar.Dock(Dock.Top), _split } }.At(0, 1));
        if (_standalone) { _layout.ColumnDefinitions = new ColumnDefinitions("0,*"); _treeHost.IsVisible = false; }
        Content = _layout;

        var changed = Ui.Debounced(() => { OnWikiChanged(); _pictures.Refresh(); _ = _git.RefreshAsync(); }, 150);
        // Git moved files underneath the wiki (a branch switch, a revert, a restored version): show what is there now.
        var gitChanged = Ui.Debounced(() => { OnWikiChanged(fromGit: true); _pictures.Refresh(); _ = _git.RefreshAsync(); }, 150);
        App.Services.Repos.Changed += gitChanged;
        Action theme = () => { _editor.Background = Ui.Tk("bgEditor"); _editor.Foreground = Ui.Tk("textCode"); TintSelection(_editor); _pictures.Refresh(); _ = PushThemeAsync(); };
        // Settings → Wiki → Pictures in the editor (and their height) apply at once.
        var settingsChanged = Ui.Debounced(() => _pictures.Refresh(), 100);
        Action config = settingsChanged;
        Wiki.Changed += changed;
        Ui.ThemeChanged += theme;
        App.Services.Config.Changed += config;
        if (_standalone) DetachedFromVisualTree += (_, _) => { Wiki.Changed -= changed; App.Services.Repos.Changed -= gitChanged; Ui.ThemeChanged -= theme; App.Services.Config.Changed -= config; };
        _ = _git.RefreshAsync();

        ApplyMode();
        UpdateNavButtons();
        BuildTree();
        Open(standalone ?? Wiki.FindPage("Home") ?? Wiki.Pages().FirstOrDefault(WikiService.IsMarkdown));
    }

    /// <summary>
    /// A see-through selection in the accent colour: the markdown's own colours stay readable under it (links are
    /// drawn in the accent colour, and vanished under the default solid one).
    /// </summary>
    internal static void TintSelection(TextEditor editor)
    {
        editor.TextArea.SelectionBrush = new SolidColorBrush(Ui.Color("accent"), 0.32);
        editor.TextArea.SelectionForeground = null;
        editor.TextArea.SelectionBorder = null;
    }

    public void OnShown() { BuildTree(); _ = _git.RefreshAsync(); if (_editable && EffectiveMode != 2) _editor.Focus(); }

    /// <summary>Saves unsaved edits and the current position; called when the window holding the page closes.</summary>
    public void Leave() { if (Dirty) Save(); RememberPosition(); State.Save(); }

    private static string FolderOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>Files that cannot be edited are always shown as preview only, whatever mode is chosen.</summary>
    private int EffectiveMode => _editable ? _mode : 2;

    private void ApplyMode()
    {
        var mode = EffectiveMode;
        _split.ColumnDefinitions = new ColumnDefinitions(mode switch { 0 => "*,0", 2 => "0,*", _ => "*,*" });
        _editorHost.IsVisible = mode != 2;
        _previewHost.IsVisible = mode != 0;
        for (var i = 0; i < _modeButtons.Length; i++) { _modeButtons[i].Opacity = i == mode ? 1 : 0.55; _modeButtons[i].Classes.Set("glow", i == mode); _modeButtons[i].IsEnabled = _editable || i == 2; }
        _saveButton.IsEnabled = _editable;
        UpdateUndoButtons();
        _imageButton.IsEnabled = _path is not null && WikiService.IsMarkdown(_path);
        if (mode != 0) _ = RenderAsync();
    }

    private void UpdateUndoButtons()
    {
        var undo = _editor.Document.UndoStack;
        _undoButton.IsEnabled = _editable && undo.CanUndo;
        _redoButton.IsEnabled = _editable && undo.CanRedo;
    }

    /// <summary>(Re)starts the countdown to saving: it runs out only after the configured time without any change.</summary>
    private void RestartAutoSave()
    {
        _autoSave.Stop();
        var settings = App.Services.Config.Settings.Wiki;
        if (!settings.AutoSave || !_editable) return;
        _autoSave.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.AutoSaveSeconds, 0.2, 3600));
        _autoSave.Start();
    }

    private void UpdateTitle() => _title.Text = _path is null ? "No page open" : _path + (Dirty ? "  ●" : "");

    // ---- tree ----

    private void BuildTree()
    {
        if (_standalone) return;
        _buildingTree = true;
        try
        {
            _tree.Items.Clear();
            foreach (var child in Wiki.Tree().Children) _tree.Items.Add(BuildNode(child));
        }
        finally { _buildingTree = false; }
    }

    private static Geometry IconFor(WikiNode node) => node.IsFolder ? Icons.FolderOpen : WikiService.KindOf(node.Name) switch
    {
        WikiKind.Markdown => Icons.TextBoxOutline, WikiKind.Image => Icons.Image, WikiKind.Audio => Icons.MusicNote, WikiKind.Video => Icons.Video, _ => Icons.File,
    };

    /// <summary>Folders with more entries than this start closed (until opened once) and list their entries in batches.</summary>
    private const int BigFolder = 300;
    /// <summary>How many entries of a folder the tree lists at a time; "Show more" adds the next batch.</summary>
    private const int TreeBatch = 500;

    /// <summary>
    /// Whether a folder is open in the tree: as the user last left it; a big folder starts closed, unless it holds
    /// the open page.
    /// </summary>
    private bool IsOpen(WikiNode node) =>
        node.Children.Count > BigFolder
            ? _opened.Contains(node.Path) || _path is not null && _path.StartsWith(node.Path + "/", StringComparison.Ordinal)
            : !_collapsed.Contains(node.Path);

    /// <remarks>
    /// The tree holds a control for every entry it lists, so a wiki of tens of thousands of files must not list them
    /// all: a closed folder lists its entries only once opened, a big one in batches, and an entry's right-click menu
    /// is made when it is asked for.
    /// </remarks>
    private TreeViewItem BuildNode(WikiNode node)
    {
        var name = node.IsFolder || !WikiService.IsMarkdown(node.Name) ? node.Name : Path.GetFileNameWithoutExtension(node.Name);
        var item = new TreeViewItem
        {
            Tag = node, IsExpanded = node.IsFolder && IsOpen(node), IsSelected = !node.IsFolder && node.Path == _path,
            Header = Ui.Row(6,
                new PathIcon { Data = IconFor(node), Width = 13, Height = 13, Foreground = node.IsFolder ? Ui.Tk("warn") : Ui.Dim },
                new TextBlock { Text = name, FontSize = 13 }),
        };
        item.ContextRequested += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            item.ContextMenu ??= NodeMenu(node);
            item.ContextMenu.Open(item);
        };
        if (node.IsFolder)
        {
            var listed = false;
            void List()
            {
                if (listed) return;
                listed = true;
                item.Items.Clear();
                AddEntries(item, node, 0);
            }
            if (item.IsExpanded) List();
            else if (node.Children.Count > 0) item.Items.Add(new TreeViewItem { Header = "…", IsEnabled = false }); // shows the expander
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (item.IsExpanded) List();
                if (_buildingTree) return;
                if (item.IsExpanded) { _collapsed.Remove(node.Path); _opened.Add(node.Path); }
                else { _collapsed.Add(node.Path); _opened.Remove(node.Path); }
            };
        }
        return item;
    }

    /// <summary>Lists a batch of a folder's entries in its tree item, starting at <paramref name="from"/>, then "Show more" if there are more.</summary>
    private void AddEntries(TreeViewItem item, WikiNode folder, int from)
    {
        var end = Math.Min(folder.Children.Count, from + TreeBatch);
        for (var i = from; i < end; i++) item.Items.Add(BuildNode(folder.Children[i]));
        if (end >= folder.Children.Count) return;
        var more = new TreeViewItem();
        more.Header = Ui.TextButton($"Show {Math.Min(TreeBatch, folder.Children.Count - end):N0} more of {folder.Children.Count - end:N0}…", () =>
        {
            item.Items.Remove(more);
            AddEntries(item, folder, end);
        });
        item.Items.Add(more);
    }

    private ContextMenu NodeMenu(WikiNode node)
    {
        var menu = new ContextMenu();
        if (!node.IsFolder)
        {
            menu.Items.Add(Ui.Item("Open", () => Open(node.Path)));
            menu.Items.Add(Ui.Item("Open in New Window", () => WikiWindow.Show(node.Path, this), icon: Icons.OpenInNew));
            menu.Items.Add(Ui.Item("Open in Default App", () => Shell.Open(Wiki.Resolve(node.Path))));
            menu.Items.Add(new Separator());
        }
        else
        {
            menu.Items.Add(Ui.Item("New Page Here…", () => { _selectedFolder = node.Path; _ = NewPageAsync(); }));
            menu.Items.Add(Ui.Item("New Folder Here…", () => { _selectedFolder = node.Path; _ = NewFolderAsync(); }));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Ui.Item("Show in Folder", () => Shell.ShowInFolder(Wiki.Resolve(node.Path))));
        menu.Items.Add(Ui.Item("Copy Path", () => { _ = App.Services.Host.SetClipboardAsync(Wiki.Resolve(node.Path)); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Rename or Move…", () => { _ = RenameAsync(node); }));
        menu.Items.Add(Ui.Item("Delete…", () => { _ = DeleteAsync(node); }));
        return menu;
    }

    private WikiNode? SelectedNode => (_tree.SelectedItem as TreeViewItem)?.Tag as WikiNode
        ?? (_path is null ? null : new WikiNode(Path.GetFileName(_path), _path, false, []));

    private void OnWikiChanged() => OnWikiChanged(false);

    /// <param name="fromGit">The files were changed by git, which this page asked for: nothing of its own to wait out.</param>
    private void OnWikiChanged(bool fromGit)
    {
        BuildTree();
        if (_path is null) return;
        string full;
        try { full = Wiki.Resolve(_path); } catch { return; }
        if (!File.Exists(full)) { _path = null; _editable = false; _loading = true; _editor.Text = ""; _saved = ""; _loading = false; UpdateTitle(); ApplyMode(); return; }
        if (!_editable) { _ = RenderAsync(); return; }
        // Changed from outside (CLI, another editor, another window): pick it up unless there are unsaved edits here.
        if (Dirty || (!fromGit && DateTime.Now - _lastOwnWrite < TimeSpan.FromSeconds(1))) return;
        string disk;
        try { disk = File.ReadAllText(full); } catch { return; }
        if (disk == _saved) return;
        _loading = true;
        try { _saved = disk; _editor.Text = disk; } finally { _loading = false; }
        _ = RenderAsync();
    }

    // ---- open / save ----

    private void RememberPosition()
    {
        if (_path is null || !_editable) return;
        State.RememberWiki(_path, editor: _editor.VerticalOffset, caret: _editor.CaretOffset);
    }

    /// <summary>Opens a wiki file. <paramref name="line"/> (1-based) moves the editor and preview to that line.</summary>
    public void Open(string? path, int line = 0)
    {
        if (path is null) { UpdateTitle(); return; }
        var found = Wiki.FindPage(path);
        if (found is null) { Ui.Toast(this, "Page not found", path); return; }
        if (found == _path && line == 0) return;
        if (Dirty) Save();
        RememberPosition();
        if (!_navigating && _path is not null && found != _path)
        {
            // Following a link, picking a page or opening a file starts a new branch: what was ahead is gone.
            Remember(_back, _path);
            _forward.Clear();
        }
        _path = found;
        _selectedFolder = FolderOf(found);
        var remembered = State.WikiPosition(found);
        _loading = true;
        try
        {
            _editable = Wiki.IsEditable(found);
            _saved = _editable ? File.ReadAllText(Wiki.Resolve(found)) : "";
            _editor.Text = _saved;
            _editor.IsReadOnly = !_editable;
            var caret = line > 0 ? OffsetOfLine(_saved, line) : Math.Clamp(remembered?.Caret ?? 0, 0, _saved.Length);
            _editor.CaretOffset = caret;
            _pendingPreviewScroll = remembered?.Preview ?? 0;
            var editorOffset = remembered?.Editor ?? 0;
            // The text box lays out after this call; scroll once it has.
            Dispatcher.UIThread.Post(() =>
            {
                if (_path != found) return;
                _loading = true;
                try
                {
                    if (line > 0) { _editor.CaretOffset = caret; _editor.Select(caret, Math.Min(_saved.Length, EndOfLine(_saved, caret)) - caret); _editor.ScrollToLine(line); }
                    else _editor.ScrollToVerticalOffset(editorOffset);
                }
                finally { _loading = false; }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex) { Ui.Toast(this, "Could not open the page", ex.Message); }
        finally { _loading = false; }
        State.RememberWiki(found);
        Avamux.Core.Diagnostics.Timeline.Note("wiki page opened", found);
        UpdateTitle();
        BuildTree();
        ApplyMode();
        if (EffectiveMode == 0) _ = RenderAsync();
        if (line > 0 && _saved.Length > 0) _ = ScrollPreviewToAsync(OffsetOfLine(_saved, line) / (double)_saved.Length);
        UpdateNavButtons();
        Opened?.Invoke(_path);
    }

    // ---- history ----

    private static void Remember(List<string> list, string path)
    {
        if (list.Count > 0 && list[^1] == path) return;
        list.Add(path);
        if (list.Count > MaxHistory) list.RemoveAt(0);
    }

    private void UpdateNavButtons()
    {
        _backButton.IsEnabled = _back.Count > 0;
        _forwardButton.IsEnabled = _forward.Count > 0;
    }

    private void GoBack() => Walk(_back, _forward);
    private void GoForward() => Walk(_forward, _back);

    /// <summary>Moves to the newest entry of <paramref name="from"/> that still exists, leaving the current page on <paramref name="to"/>.</summary>
    private void Walk(List<string> from, List<string> to)
    {
        while (from.Count > 0)
        {
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            if (Wiki.FindPage(target) is not { } found) continue; // deleted or moved since
            var here = _path;
            _navigating = true;
            try { Open(found); }
            finally { _navigating = false; }
            if (here is not null && here != _path) Remember(to, here);
            break;
        }
        UpdateNavButtons();
    }

    private static int OffsetOfLine(string text, int line)
    {
        var at = 0;
        for (var i = 1; i < line; i++)
        {
            var next = text.IndexOf('\n', at);
            if (next < 0) break;
            at = next + 1;
        }
        return at;
    }

    private static int EndOfLine(string text, int offset) => text.IndexOf('\n', offset) is >= 0 and var e ? e : text.Length;

    private void Save()
    {
        if (_path is null || !_editable) return;
        try
        {
            var text = _editor.Text;
            _lastOwnWrite = DateTime.Now;
            _saved = text;
            Wiki.Write(_path, text);
            UpdateTitle();
        }
        catch (Exception ex) { Ui.Toast(this, "Could not save the page", ex.Message); }
    }

    // ---- preview ----

    private async Task PreviewStartedAsync()
    {
        if (_previewReady) return;
        if (!await WebViews.WaitForAsync(_preview, "typeof window.setContent === 'function'")) { Log.Warn("wiki", "The preview page did not start."); return; }
        _previewReady = true;
        Avamux.Core.Diagnostics.Timeline.Note("wiki preview ready");
        await PushThemeAsync();
        await RenderAsync();
    }

    private async Task PushThemeAsync()
    {
        if (!_previewReady) return;
        try { await _preview.InvokeScript($"(function(){{ setTheme({JsonSerializer.Serialize(Themes.CssVariables(Ui.Theme))}); return 'ok'; }})()"); }
        catch (Exception ex) { Log.Warn("wiki", "Preview theme update failed: " + ex.Message); }
    }

    private async Task RenderAsync()
    {
        if (!_previewReady || EffectiveMode == 0) return;
        string html;
        var path = _path;
        try
        {
            if (path is null) html = "<p class=\"dim\">Select or create a page.</p>";
            else if (WikiService.IsMarkdown(path)) html = WikiService.Render(_editor.Text);
            else
            {
                var text = _editable ? _editor.Text : null; // read on this thread; rendering happens on another
                html = await Task.Run(() => Wiki.RenderFile(path, text));
            }
        }
        catch (Exception ex) { html = $"<p class=\"dim\">{System.Net.WebUtility.HtmlEncode(ex.Message)}</p>"; }
        if (path != _path) return;
        var folder = path is null ? "" : FolderOf(path);
        var scroll = _pendingPreviewScroll ?? 0;
        try
        {
            await _preview.InvokeScript($"(function(){{ setContent({JsonSerializer.Serialize(html)}, {JsonSerializer.Serialize(folder)}, {JsonSerializer.Serialize(path ?? "")}, {scroll.ToString(System.Globalization.CultureInfo.InvariantCulture)}); return 'ok'; }})()");
            Avamux.Core.Diagnostics.Timeline.Note("wiki rendered", path);
        }
        catch (Exception ex) { Log.Warn("wiki", "Preview update failed: " + ex.Message); }
    }

    private async Task ScrollPreviewToAsync(double fraction)
    {
        await Task.Delay(350); // after the render that Open started
        if (!_previewReady) return;
        try { await _preview.InvokeScript($"(function(){{ scrollToLine({fraction.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}); return 'ok'; }})()"); } catch { }
    }

    private void OnPreviewMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Body is not { Length: > 0 } body || JsonNode.Parse(body) is not JsonObject m) return;
            string? S(string key) => m[key]?.GetValue<string>();
            switch (S("t"))
            {
                case "ready": _ = PreviewStartedAsync(); break;
                case "open": if (S("url") is { } url && (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("mailto:"))) Shell.Open(url); break;
                case "copyText": _ = App.Services.Host.SetClipboardAsync(S("text") ?? ""); break;
                case "scroll":
                    if (S("path") is { } p && p == _path && m["y"] is { } y) { _pendingPreviewScroll = y.GetValue<double>(); State.RememberWiki(p, preview: _pendingPreviewScroll); }
                    break;
                case "file": _ = FileActionAsync(S("a") ?? "", S("path") ?? ""); break;
                case "page":
                    var target = S("path") ?? "";
                    if (Wiki.FindPage(target) is { } page) Open(page);
                    else _ = OfferCreateAsync(target);
                    break;
            }
        }
        catch (Exception ex) { Log.Warn("wiki", "Bad message from the preview: " + ex.Message); }
    }

    /// <summary>Right-click actions on a picture and the buttons of a file view.</summary>
    private async Task FileActionAsync(string action, string path)
    {
        string full;
        try { full = Wiki.Resolve(path); } catch { return; }
        if (!File.Exists(full)) { Ui.Toast(this, "File not found", path); return; }
        switch (action)
        {
            case "show": Shell.ShowInFolder(full); break;
            case "open": Shell.Open(full); break;
            case "copyPath":
                await App.Services.Host.SetClipboardAsync(full);
                Ui.Toast(this, "Path copied", full);
                break;
            case "copy":
                var ok = TopLevel.GetTopLevel(this) is { } top && await ClipboardFiles.CopyImageAsync(top, full);
                Ui.Toast(this, ok ? "Picture copied" : "Could not copy the picture", ok ? Path.GetFileName(full) : "The clipboard did not accept it.");
                break;
        }
    }

    private async Task OfferCreateAsync(string target)
    {
        if (target.Length == 0) return;
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Create page", $"\"{target}\" does not exist yet. Create it?", "Create")) return;
        Ui.Try(this, () => Open(Wiki.Write(target, $"# {Path.GetFileNameWithoutExtension(target)}\n\n")));
    }

    // ---- inserting ----

    private void InsertText(string text)
    {
        // Through the selection so the insert is one undo step and keeps the scroll position.
        _editor.TextArea.Selection.ReplaceSelectionWithText(text);
        _editor.Focus();
    }

    /// <summary>
    /// Paste into the editor. Copied files and pictures are stored next to the page and linked (pictures show
    /// inline in the preview); anything else is pasted as text.
    /// </summary>
    private async Task PasteAsync()
    {
        if (_path is null || !_editable || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (WikiService.IsMarkdown(_path))
            {
                var folder = FolderOf(_path);
                var files = await ClipboardFiles.FilesAsync(top);
                if (files.Count > 0)
                {
                    InsertText(string.Join("\n", files.Select(f => WikiService.LinkMarkdown(Path.GetFileName(Wiki.Import(f, folder))))));
                    return;
                }
                // Text wins when both are offered: programs that copy rich text often add a rendered picture of it.
                var text = top.Clipboard is { } c ? await c.TryGetTextAsync() : null;
                if (string.IsNullOrEmpty(text) && await ClipboardFiles.PngAsync(top) is { } png)
                {
                    var name = Path.GetFileName(Wiki.ImportBytes(png, folder, $"Pasted image {DateTime.Now:yyyyMMdd-HHmmss}.png"));
                    InsertText(WikiService.LinkMarkdown(name));
                    Ui.Toast(this, "Picture pasted", $"Saved as {name} next to this page.");
                    return;
                }
                if (!string.IsNullOrEmpty(text)) InsertText(text);
                return;
            }
            if (top.Clipboard is { } clip && await clip.TryGetTextAsync() is { Length: > 0 } plain) InsertText(plain);
        }
        catch (Exception ex) { Ui.Toast(this, "Could not paste", ex.GetBaseException().Message); }
    }

    private bool CanTakeFiles => _path is not null && _editable && WikiService.IsMarkdown(_path);

    private static IReadOnlyList<string> DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null && File.Exists(p)).Select(p => p!).ToList() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!CanTakeFiles || DroppedFiles(e).Count == 0) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanTakeFiles) return;
        var files = DroppedFiles(e);
        if (files.Count == 0) return;
        e.Handled = true;
        // Drop where the pointer is when it is over the text; otherwise at the caret.
        if (_editorHost.IsVisible && _editor.GetPositionFromPoint(e.GetPosition(_editor)) is { } at)
            _editor.CaretOffset = _editor.Document.GetOffset(at.Line, at.Column);
        var added = new List<string>();
        foreach (var file in files)
        {
            try { added.Add(WikiService.LinkMarkdown(Path.GetFileName(Wiki.Import(file, FolderOf(_path!))))); }
            catch (Exception ex) { Ui.Toast(this, "Could not add " + Path.GetFileName(file), ex.GetBaseException().Message); }
        }
        if (added.Count == 0) return;
        InsertText(string.Join("\n", added));
        Ui.Toast(this, added.Count == 1 ? "File added" : $"{added.Count} files added", "Copied next to this page and linked.");
    }

    private async Task InsertFileAsync()
    {
        if (_path is null || !WikiService.IsMarkdown(_path)) { Ui.Toast(this, "Open a page first"); return; }
        var files = await Ui.Owner(this).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert a picture or file", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.bmp", "*.avif"] }, FilePickerFileTypes.All],
        });
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } local) continue;
            Ui.Try(this, () => InsertText(WikiService.LinkMarkdown(Path.GetFileName(Wiki.Import(local, FolderOf(_path))))), "Could not import the file");
        }
    }

    // ---- actions ----

    private async Task NewPageAsync()
    {
        var folder = _selectedFolder ?? "";
        var name = await Ui.PromptAsync(Ui.Owner(this), "New page", folder.Length > 0 ? $"Page name (in {folder}/). Use / to create folders." : "Page name. Use / to create folders.");
        if (string.IsNullOrWhiteSpace(name)) return;
        Ui.Try(this, () =>
        {
            var path = (folder.Length > 0 ? folder + "/" : "") + name.Trim().TrimStart('/');
            if (Wiki.FindPage(path) is { } existing && File.Exists(Wiki.Resolve(existing)) && existing.StartsWith(path, StringComparison.OrdinalIgnoreCase)) { Open(existing); return; }
            Open(Wiki.Write(path, $"# {Path.GetFileNameWithoutExtension(path)}\n\n"));
        }, "Could not create the page");
    }

    private async Task NewFolderAsync()
    {
        var folder = _selectedFolder ?? "";
        var name = await Ui.PromptAsync(Ui.Owner(this), "New folder", folder.Length > 0 ? $"Folder name (in {folder}/)" : "Folder name");
        if (string.IsNullOrWhiteSpace(name)) return;
        Ui.Try(this, () => Wiki.CreateFolder((folder.Length > 0 ? folder + "/" : "") + name.Trim().Trim('/')), "Could not create the folder");
    }

    private async Task RenameAsync(WikiNode? node)
    {
        if (node is null) { Ui.Toast(this, "Select a page or folder first"); return; }
        var to = await Ui.PromptAsync(Ui.Owner(this), "Rename or move", "New path inside the wiki", node.Path);
        if (string.IsNullOrWhiteSpace(to) || to.Trim() == node.Path) return;
        Ui.Try(this, () =>
        {
            if (Dirty) Save();
            var moved = Wiki.Move(node.Path, to.Trim().Trim('/'));
            State.RenameWiki(node.Path, moved);
            foreach (var list in new[] { _back, _forward })
                for (var i = 0; i < list.Count; i++)
                    if (list[i] == node.Path) list[i] = moved;
                    else if (node.IsFolder && list[i].StartsWith(node.Path + "/", StringComparison.Ordinal)) list[i] = moved + list[i][node.Path.Length..];
            if (_path == node.Path) { _path = moved; UpdateTitle(); Opened?.Invoke(_path); }
            else if (_path is not null && node.IsFolder && _path.StartsWith(node.Path + "/", StringComparison.Ordinal)) { _path = moved + _path[node.Path.Length..]; UpdateTitle(); Opened?.Invoke(_path); }
            BuildTree();
        }, "Could not rename");
    }

    private async Task DeleteAsync(WikiNode? node)
    {
        if (node is null) { Ui.Toast(this, "Select a page or folder first"); return; }
        var what = node.IsFolder ? $"the folder \"{node.Path}\" and everything in it" : $"\"{node.Path}\"";
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Delete", $"Delete {what}?")) return;
        Ui.Try(this, () =>
        {
            if (_path == node.Path || (node.IsFolder && _path?.StartsWith(node.Path + "/", StringComparison.Ordinal) == true)) { _saved = _editor.Text; }
            Wiki.Delete(node.Path);
            State.ForgetWiki(node.Path);
        }, "Could not delete");
    }
}
