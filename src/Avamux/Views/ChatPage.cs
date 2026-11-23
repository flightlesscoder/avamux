using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avamux.Core.Ai;
using Avamux.Plugins;

namespace Avamux.Views;

/// <summary>
/// AI Chat: conversations with the providers set up under Settings → AI Chat (Claude Code, pi, …). On the left the
/// conversations; on the right the one open, with its provider, model, effort, working folder and how full its context
/// is; its messages; the questions and permission requests it waits on; and the message box (Enter sends, Shift+Enter
/// is a new line; pictures and files can be pasted or dropped in).
/// </summary>
public sealed class ChatPage : UserControl, IPage
{
    private readonly ListBox _chats = new() { Background = Brushes.Transparent };
    private readonly TextBox _search = new() { PlaceholderText = "Search chats…", Margin = new Thickness(8, 4, 8, 6) };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _provider = new() { MinWidth = 140 };
    private readonly ComboBox _model = new() { MinWidth = 150, IsEditable = true };
    private readonly ComboBox _effort = new() { MinWidth = 100 };
    private readonly Button _folder;
    private readonly ContextRing _ring = new();
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly ItemsControl _messages = new();
    private readonly ObservableCollection<ChatRow> _rows = [];
    private readonly StackPanel _cards = new() { Spacing = 8, Margin = new Thickness(18, 0, 18, 8) };
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 240, FontSize = 13.5,
        PlaceholderText = "Message… (Enter sends, Shift+Enter for a new line; paste or drop pictures and files)",
    };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly List<AiChatAttachment> _attachments = [];
    private readonly List<string> _pendingFiles = []; // attached before a chat exists
    private readonly Button _send, _stop;
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Dim, FontSize = 13.5, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Dictionary<string, Action> _refreshers = [];
    private string? _chatId;
    private bool _stick = true, _loadingHeader;
    private SpeechService.Spoken? _autoSpoken;
    private string? _autoSpokenId;

    private static ChatService Chats => App.Services.Ai.Chats;
    private static AiService Ai => App.Services.Ai;

    public ChatPage()
    {
        _chats.ItemTemplate = new FuncDataTemplate<AiChat>((c, _) => c is null ? new Panel() : ChatItem(c));
        _chats.SelectionChanged += (_, _) => { if (_chats.SelectedItem is AiChat c && c.Id != _chatId) Open(c.Id); };
        _search.TextChanged += (_, _) => RefreshList();
        var left = new DockPanel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(8, 8, 8, 2),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Ui.IconButton(Icons.Plus, "New chat (Ctrl+N)", NewChat, "New").Dock(Dock.Right),
                            new TextBlock { Text = "AI Chat", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                }.Dock(Dock.Top),
                _search.Dock(Dock.Top),
                _chats,
            },
        };

        _folder = Ui.IconButton(Icons.FolderOpen, "The folder the agent works in", () => _ = ChooseFolderAsync(), "");
        _provider.SelectionChanged += (_, _) => { if (!_loadingHeader && _provider.SelectedItem is ComboBoxItem { Tag: string id }) _ = ConfigureAsync(provider: id); };
        _effort.SelectionChanged += (_, _) => { if (!_loadingHeader && _effort.SelectedItem is ComboBoxItem { Tag: string e }) _ = ConfigureAsync(effort: e); };
        _model.SelectionChanged += (_, _) => { if (!_loadingHeader && _model.SelectedItem is ComboBoxItem { Tag: string m }) _ = ConfigureAsync(model: m); };
        _model.LostFocus += (_, _) => CommitTypedModel();
        _model.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitTypedModel(); } };
        ToolTip.SetTip(_provider, "Provider (Settings → AI Chat sets them up)");
        ToolTip.SetTip(_model, "Model: pick one or type its name");
        ToolTip.SetTip(_effort, "Effort / thinking");
        var rename = Ui.IconButton(Icons.Pencil, "Rename this chat", () => _ = RenameAsync(_chatId));
        var header = new Border
        {
            Padding = new Thickness(16, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Row(6, _provider, _model, _effort, _folder, _ring).Dock(Dock.Right),
                    Ui.Row(4, _title, rename),
                },
            },
        };

        _messages.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _messages.ItemTemplate = new FuncDataTemplate<ChatRow>((row, _) => row is null ? new Panel() : new ChatMessageView(row, this));
        _messages.ItemsSource = _rows;
        _messages.Margin = new Thickness(18, 10, 18, 10);
        _scroll.Content = _messages;
        _scroll.ScrollChanged += (_, _) =>
        {
            var bottom = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y;
            _stick = bottom < 60;
        };

        _send = Ui.IconButton(Icons.Send, "Send (Enter)", () => _ = SendAsync(), "Send");
        _send.Classes.Add("accent");
        _stop = Ui.IconButton(Icons.Stop, "Stop the reply (Esc)", () => { if (_chatId is { } id) _ = Chats.StopAsync(id); }, "Stop");
        _stop.IsVisible = false;
        var attach = Ui.IconButton(Icons.Paperclip, "Attach pictures or files", () => _ = PickFilesAsync());
        _input.AddHandler(KeyDownEvent, OnInputKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.TextChanged += (_, _) => { if (_chatId is { } id && !_loadingHeader) Chats.SetDraft(id, _input.Text); };
        var composer = new Border
        {
            Padding = new Thickness(18, 8, 18, 12), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel
            {
                Children =
                {
                    _chips.Dock(Dock.Top),
                    Ui.Apply(Ui.Row(4, attach, _stop, _send), r => { r.VerticalAlignment = VerticalAlignment.Bottom; r.Margin = new Thickness(8, 0, 0, 0); }).Dock(Dock.Right),
                    _input,
                },
            },
        };

        var right = new DockPanel
        {
            Children =
            {
                header.Dock(Dock.Top),
                composer.Dock(Dock.Bottom),
                _cards.Dock(Dock.Bottom),
                new Panel { Children = { _scroll, _empty } },
            },
        };
        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            Children =
            {
                new Border { Background = Ui.Tk("bgSide"), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = left },
                right.At(0, 1),
            },
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { if (Dropped(e).Count > 0) { e.DragEffects = DragDropEffects.Copy; e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, (_, e) => { var files = Dropped(e); if (files.Count == 0) return; e.Handled = true; foreach (var f in files) AttachFile(f); }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; NewChat(); }
            else if (e.Key == Key.Escape && _chatId is { } id && Chats.IsBusy(id)) { e.Handled = true; _ = Chats.StopAsync(id); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Chats.Changed += OnChatsChanged;
        Ai.Changed += () => Dispatcher.UIThread.Post(RefreshHeader);
        App.Services.Speech.Changed += () => Dispatcher.UIThread.Post(() => { foreach (var r in _rows) r.Notify(); });
        RefreshList();
        var first = Chats.List().FirstOrDefault();
        if (first is not null) Open(first.Id); else ShowEmpty();
    }

    public void OnShown() { RefreshList(); _input.Focus(); }

    /// <summary>The chat shown, if any.</summary>
    public string? CurrentChat => _chatId;

    // ---- the list ----

    private Control ChatItem(AiChat c)
    {
        var busy = Chats.IsBusy(c.Id);
        var waiting = Chats.Requests(c.Id).Count > 0;
        var title = Ui.Row(5);
        if (c.Pinned) title.Children.Add(new PathIcon { Data = Icons.Pin, Width = 11, Height = 11, Foreground = Ui.Dim });
        title.Children.Add(new TextBlock { Text = c.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
        if (waiting) title.Children.Add(Ui.Dot("warn", 8));
        else if (busy) title.Children.Add(Ui.Dot("info", 8));
        var provider = Ai.Config(c.ProviderId)?.Name ?? c.ProviderId;
        var item = new StackPanel
        {
            Spacing = 1, Background = Brushes.Transparent,
            Children = { title, new TextBlock { Text = $"{Ui.Ago(c.Updated)} · {provider}{(c.Model.Length > 0 ? " · " + c.Model : "")}", FontSize = 11, Foreground = Ui.Dim, TextTrimming = TextTrimming.CharacterEllipsis } },
        };
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Rename…", () => _ = RenameAsync(c.Id), icon: Icons.Pencil));
        menu.Items.Add(Ui.Item(c.Pinned ? "Unpin" : "Pin to the Top", () => Chats.Pin(c.Id, !c.Pinned), icon: Icons.Pin));
        menu.Items.Add(Ui.Item("Show Its Folder", () => Avamux.Core.Shell.Open(Chats.FolderOf(c.Id))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Delete…", () => _ = DeleteAsync(c.Id), icon: Icons.Delete));
        item.ContextMenu = menu;
        return item;
    }

    private void RefreshList()
    {
        var q = (_search.Text ?? "").Trim();
        var list = Chats.List().Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                        || c.Messages.Any(m => m.Text.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        _chats.ItemsSource = list;
        _chats.SelectedItem = list.FirstOrDefault(c => c.Id == _chatId);
    }

    private void OnChatsChanged(string? id)
    {
        var key = id ?? "";
        Action? refresh;
        lock (_refreshers)
            if (!_refreshers.TryGetValue(key, out refresh))
                _refreshers[key] = refresh = Ui.Debounced(() =>
                {
                    if (id is null) { RefreshList(); return; }
                    if (id == _chatId) { RefreshMessages(); RefreshCards(); RefreshBusy(); RefreshRing(); AutoSpeak(); }
                    RefreshListItem();
                }, id is null ? 80 : 40);
        refresh();
    }

    private Action? _listItemSoon;
    private void RefreshListItem() => (_listItemSoon ??= Ui.Debounced(RefreshList, 600))();

    // ---- the chat open ----

    /// <summary>Shows a chat.</summary>
    public void Open(string chatId)
    {
        var chat = Chats.Find(chatId);
        if (chat is null) return;
        _chatId = chat.Id;
        _rows.Clear();
        _attachments.Clear();
        _chips.Children.Clear();
        _loadingHeader = true;
        try { _input.Text = chat.Draft ?? ""; } finally { _loadingHeader = false; }
        _empty.IsVisible = false;
        _scroll.IsVisible = true;
        RefreshHeader();
        RefreshMessages();
        RefreshCards();
        RefreshBusy();
        RefreshRing();
        if (!ReferenceEquals((_chats.SelectedItem as AiChat), chat)) _chats.SelectedItem = (_chats.ItemsSource as IEnumerable<AiChat>)?.FirstOrDefault(c => c.Id == chat.Id);
        _stick = true;
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ShowEmpty()
    {
        _chatId = null;
        _rows.Clear();
        _scroll.IsVisible = false;
        _empty.IsVisible = true;
        var providers = Ai.Providers;
        _empty.Text = providers.Count == 0
            ? "No AI provider is set up. Add one under Settings → AI Chat (Claude Code and pi are there to start with)."
            : $"Type a message below to start a conversation with {Ai.Config(App.Services.Config.Settings.Ai.ChatProvider)?.Name ?? providers[0].Name}.\nIt works in {Ai.DefaultFolder}.";
        _title.Text = "New chat";
        RefreshHeader();
        RefreshCards();
        RefreshBusy();
        _ring.Set(null, null, null);
    }

    public void NewChat()
    {
        ShowEmpty();
        _chats.SelectedItem = null;
        _input.Text = "";
        _input.Focus();
    }

    /// <summary>Opens a new chat as asked (AI Actions' "Open in AI Chat" and Do Research…).</summary>
    public string OpenNew(AiChatRequest request)
    {
        var chat = Chats.Create(request);
        RefreshList();
        Open(chat.Id);
        _input.Focus();
        if (request.Draft is { } draft) { _input.Text = draft; _input.CaretIndex = draft.Length; }
        return chat.Id;
    }

    private AiChat? Current => _chatId is null ? null : Chats.Find(_chatId);

    private void RefreshMessages()
    {
        var chat = Current;
        if (chat is null) return;
        var thread = ChatService.Thread(chat);
        var same = thread.Count >= _rows.Count && _rows.Select((r, i) => r.Message.Id == thread[i].Id).All(x => x);
        if (!same)
        {
            _rows.Clear();
            foreach (var m in thread) _rows.Add(new ChatRow(chat.Id, m));
        }
        else
        {
            for (var i = _rows.Count; i < thread.Count; i++) _rows.Add(new ChatRow(chat.Id, thread[i]));
            foreach (var r in _rows) r.Notify();
        }
        _title.Text = chat.Title;
        if (_stick) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void RefreshBusy()
    {
        var busy = _chatId is { } id && Chats.IsBusy(id);
        _stop.IsVisible = busy;
        _send.IsEnabled = !busy;
    }

    private void RefreshRing()
    {
        var c = Current;
        _ring.Set(c?.ContextTokens, c?.ContextWindow, c?.CostUsd);
    }

    private void RefreshHeader()
    {
        _loadingHeader = true;
        try
        {
            var chat = Current;
            var providerId = chat?.ProviderId ?? (App.Services.Config.Settings.Ai.ChatProvider is { Length: > 0 } p && Ai.Config(p) is not null ? p : Ai.Providers.FirstOrDefault()?.Id);
            _provider.Items.Clear();
            foreach (var info in Ai.Providers) _provider.Items.Add(new ComboBoxItem { Content = info.Name, Tag = info.Id });
            _provider.SelectedItem = _provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == providerId);
            var config = providerId is null ? null : Ai.Config(providerId);
            var kind = config is null ? null : Ai.Kind(config.Kind);
            _effort.Items.Clear();
            _effort.Items.Add(new ComboBoxItem { Content = "Default effort", Tag = "" });
            foreach (var level in kind?.Capabilities.EffortLevels ?? []) _effort.Items.Add(new ComboBoxItem { Content = level, Tag = level });
            var effort = chat?.Effort ?? config?.Effort ?? "";
            _effort.SelectedItem = _effort.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == effort) ?? _effort.Items[0];
            _effort.IsVisible = kind?.Capabilities.EffortLevels.Count > 0;
            var model = chat?.Model ?? config?.Model ?? "";
            _model.Items.Clear();
            _model.Items.Add(new ComboBoxItem { Content = model.Length > 0 ? model : "Default model", Tag = model });
            _model.SelectedIndex = 0;
            if (config is not null && kind is not null) _ = LoadModelsAsync(config, model);
            var folder = chat?.Folder ?? Ai.DefaultFolder;
            ((TextBlock)((StackPanel)_folder.Content!).Children.OfType<TextBlock>().FirstOrDefault()!)?.SetValue(TextBlock.TextProperty, System.IO.Path.GetFileName(folder.TrimEnd('/')));
            ToolTip.SetTip(_folder, "Works in " + folder + " (click to change)");
        }
        finally { _loadingHeader = false; }
    }

    private async Task LoadModelsAsync(AiProviderConfig config, string current)
    {
        var models = await Ai.ModelsAsync(config.Id);
        if (Current?.ProviderId is { } shown && shown != config.Id) return;
        _loadingHeader = true;
        try
        {
            _model.Items.Clear();
            foreach (var m in models) _model.Items.Add(Ui.Apply(new ComboBoxItem { Content = m.Name, Tag = m.Id }, i => { if (m.Note is { } n) ToolTip.SetTip(i, n); }));
            if (current.Length > 0 && models.All(m => m.Id != current)) _model.Items.Add(new ComboBoxItem { Content = current, Tag = current });
            _model.SelectedItem = _model.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == current) ?? _model.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        finally { _loadingHeader = false; }
    }

    private void CommitTypedModel()
    {
        var typed = (_model.Text ?? "").Trim();
        if (typed.Length == 0 || _model.Items.OfType<ComboBoxItem>().Any(i => i.Content as string == typed)) return;
        _ = ConfigureAsync(model: typed);
    }

    private async Task ConfigureAsync(string? provider = null, string? model = null, string? effort = null, string? folder = null)
    {
        if (_chatId is not { } id)
        {
            // No chat yet: the choice applies to the one the next message starts.
            if (provider is not null) App.Services.Config.Set("ai.chatProvider", System.Text.Json.Nodes.JsonValue.Create(provider));
            _pendingModel = model ?? _pendingModel;
            _pendingEffort = effort ?? _pendingEffort;
            _pendingFolder = folder ?? _pendingFolder;
            if (provider is not null) RefreshHeader();
            return;
        }
        await RunAsync(() => Chats.ConfigureAsync(id, provider, model, effort, folder));
        RefreshHeader();
    }

    private string? _pendingModel, _pendingEffort, _pendingFolder;

    private async Task ChooseFolderAsync()
    {
        var folders = await Ui.Owner(this).StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The folder the agent works in", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        await ConfigureAsync(folder: path);
        RefreshHeader();
    }

    // ---- sending ----

    /// <summary>Runs a chat operation, telling the user when it fails.</summary>
    internal async Task RunAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { Ui.Toast(this, "AI Chat", ex.GetBaseException().Message); }
        finally { RefreshBusy(); }
    }

    private async Task SendAsync()
    {
        var text = (_input.Text ?? "").Trim();
        if (text.Length == 0 && _attachments.Count == 0 && _pendingFiles.Count == 0) return;
        if (_chatId is null)
        {
            if (Ai.Providers.Count == 0) { Ui.Toast(this, "No AI provider is set up", "Add one under Settings → AI Chat."); return; }
            var chat = Chats.Create(new AiChatRequest
            {
                ProviderId = _provider.SelectedItem is ComboBoxItem { Tag: string p } ? p : null, Model = _pendingModel, Effort = _pendingEffort, WorkingDirectory = _pendingFolder,
            });
            _pendingModel = _pendingEffort = _pendingFolder = null;
            Open(chat.Id);
            foreach (var f in _pendingFiles) AttachFile(f);
            _pendingFiles.Clear();
        }
        var id = _chatId!;
        var attachments = _attachments.ToList();
        _attachments.Clear();
        _chips.Children.Clear();
        _input.Text = "";
        _stick = true;
        var send = Chats.SendAsync(id, text, attachments);
        RefreshBusy();
        await RunAsync(() => send);
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (_send.IsEnabled) _ = SendAsync();
            return;
        }
        var paste = e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift;
        if (!paste) return;
        e.Handled = true;
        _ = PasteAsync();
    }

    /// <summary>Paste: copied files and pictures become attachments; text goes into the box.</summary>
    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await ClipboardFiles.FilesAsync(top);
            if (files.Count > 0) { foreach (var f in files) AttachFile(f); return; }
            var text = top.Clipboard is { } c ? await c.TryGetTextAsync() : null;
            if (string.IsNullOrEmpty(text) && await ClipboardFiles.PngAsync(top) is { } png)
            {
                AttachBytes(png, $"Pasted image {DateTime.Now:yyyyMMdd-HHmmss}.png");
                return;
            }
            if (!string.IsNullOrEmpty(text))
            {
                var at = _input.CaretIndex;
                var current = _input.Text ?? "";
                var start = Math.Min(_input.SelectionStart, _input.SelectionEnd);
                var end = Math.Max(_input.SelectionStart, _input.SelectionEnd);
                if (end > start) { current = current.Remove(start, end - start); at = start; }
                _input.Text = current.Insert(Math.Min(at, current.Length), text);
                _input.CaretIndex = Math.Min(at, current.Length) + text.Length;
            }
        }
        catch (Exception ex) { Ui.Toast(this, "Could not paste", ex.GetBaseException().Message); }
    }

    private static IReadOnlyList<string> Dropped(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null && File.Exists(p)).Select(p => p!).ToList() ?? [];

    private async Task PickFilesAsync()
    {
        var files = await Ui.Owner(this).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach pictures or files", AllowMultiple = true });
        foreach (var f in files) if (f.TryGetLocalPath() is { } p) AttachFile(p);
    }

    private void AttachFile(string path)
    {
        if (_chatId is not { } id) { _pendingFiles.Add(path); AddChip(new AiChatAttachment(path, Avamux.Core.Web.WebServer.MimeOf(path), System.IO.Path.GetFileName(path)), pending: true); return; }
        try { var a = Chats.Attach(id, path); _attachments.Add(a); AddChip(a); }
        catch (Exception ex) { Ui.Toast(this, "Could not attach " + System.IO.Path.GetFileName(path), ex.Message); }
    }

    private void AttachBytes(byte[] data, string name)
    {
        if (_chatId is null)
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
            File.WriteAllBytes(tmp, data);
            AttachFile(tmp);
            return;
        }
        var a = Chats.AttachBytes(_chatId, data, name);
        _attachments.Add(a);
        AddChip(a);
    }

    private void AddChip(AiChatAttachment a, bool pending = false)
    {
        Control preview = a.IsImage ? ChatMessageView.Picture(a.Path, a.Name, 90, 64) : new TextBlock { Text = "📎 " + a.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        Border chip = null!;
        var remove = Ui.IconButton(Icons.Close, "Remove", () =>
        {
            _chips.Children.Remove(chip);
            if (pending) _pendingFiles.Remove(a.Path); else _attachments.Remove(a);
        });
        chip = new Border
        {
            Background = Ui.Tk("bgDeep"), CornerRadius = Ui.R(6), Padding = new Thickness(6, 3), Margin = new Thickness(0, 0, 6, 0),
            Child = Ui.Row(4, preview, remove),
        };
        ToolTip.SetTip(chip, a.Path);
        _chips.Children.Add(chip);
    }

    // ---- questions and permission requests ----

    private void RefreshCards()
    {
        _cards.Children.Clear();
        if (_chatId is not { } id) return;
        foreach (var r in Chats.Requests(id))
            _cards.Children.Add(r.Request switch
            {
                AiPermissionRequest p => PermissionCard(id, p),
                AiQuestionRequest q => QuestionCard(id, q),
                _ => new Panel(),
            });
    }

    private static Border Card(Control child, string token) => new()
    {
        Background = new SolidColorBrush(Ui.Color(token), 0.12), BorderBrush = Ui.Tk(token), BorderThickness = new Thickness(1), CornerRadius = Ui.R(8),
        Padding = new Thickness(12, 10), Child = child,
    };

    private Control PermissionCard(string chatId, AiPermissionRequest p)
    {
        var reason = new TextBox { PlaceholderText = "Why not (optional, passed on)", MinWidth = 220 };
        Task Answer(AiDecision d) => RunAsync(() => Chats.AnswerAsync(chatId, p.Id, new AiAnswer { Decision = d, Reason = d == AiDecision.Deny && reason.Text is { Length: > 0 } t ? t : null }));
        var allow = Ui.TextButton("Allow", () => _ = Answer(AiDecision.Allow));
        allow.Classes.Add("accent");
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Row(8, new PathIcon { Data = Icons.Lock, Width = 14, Height = 14, Foreground = Ui.Tk("warn") }, new TextBlock { Text = $"Allow {p.Tool}?", FontWeight = FontWeight.SemiBold }),
                new SelectableTextBlock { Text = p.Summary, FontFamily = Ui.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (p.InputJson is { Length: > 2 } json && json.Length > p.Summary.Length + 20)
            body.Children.Add(new Expander { Header = "Details", Content = new SelectableTextBlock { Text = PrettyJson(json), FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap } });
        body.Children.Add(Ui.Row(6, allow, Ui.TextButton("Allow for this chat", () => _ = Answer(AiDecision.AllowForSession), $"Allow {p.Tool} for the rest of this conversation"),
            Ui.TextButton("Deny", () => _ = Answer(AiDecision.Deny)), reason));
        return Card(body, "warn");
    }

    private static string PrettyJson(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); } catch { return json; }
    }

    private Control QuestionCard(string chatId, AiQuestionRequest q)
    {
        var body = new StackPanel { Spacing = 10 };
        var answers = new Dictionary<string, Func<string?>>();
        foreach (var question in q.Questions)
        {
            var part = new StackPanel { Spacing = 6 };
            var head = Ui.Row(8, new PathIcon { Data = Icons.HelpCircle, Width = 14, Height = 14, Foreground = Ui.Tk("accent") });
            if (question.Header is { Length: > 0 } h) head.Children.Add(Ui.Chip(h, Ui.Tk("accent"), 11));
            head.Children.Add(new TextBlock { Text = question.Question, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
            part.Children.Add(head);
            var other = new TextBox { PlaceholderText = question.Options.Count > 0 ? "Or type an answer of your own" : "Your answer", MinWidth = 300 };
            var picked = new HashSet<string>();
            if (question.Options.Count > 0)
            {
                var options = new WrapPanel();
                foreach (var o in question.Options)
                {
                    var label = o.Label;
                    Control choice;
                    if (question.MultiSelect)
                    {
                        var box = new CheckBox { Content = label };
                        box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) picked.Add(label); else picked.Remove(label); };
                        choice = box;
                    }
                    else
                    {
                        var button = Ui.TextButton(label, () =>
                        {
                            // One question with single choices: a click answers it.
                            if (q.Questions.Count == 1) _ = RunAsync(() => Chats.AnswerAsync(chatId, q.Id, new AiAnswer { Answers = { [question.Question] = label } }));
                            else { picked.Clear(); picked.Add(label); }
                        });
                        choice = button;
                    }
                    if (o.Description is { Length: > 0 } d) ToolTip.SetTip(choice, d);
                    choice.Margin = new Thickness(0, 0, 6, 6);
                    options.Children.Add(choice);
                    if (o.Description is { Length: > 0 } desc && q.Questions.Count == 1 && !question.MultiSelect && question.Options.Count <= 4)
                        ToolTip.SetTip(choice, desc);
                }
                part.Children.Add(options);
            }
            part.Children.Add(other);
            answers[question.Question] = () => other.Text is { Length: > 0 } typed ? typed : picked.Count > 0 ? string.Join(", ", picked) : null;
            body.Children.Add(part);
        }
        var submit = Ui.TextButton("Answer", () =>
        {
            var given = answers.ToDictionary(a => a.Key, a => a.Value());
            if (given.Values.Any(v => v is null)) { Ui.Toast(this, "Answer every question first"); return; }
            _ = RunAsync(() => Chats.AnswerAsync(chatId, q.Id, new AiAnswer { Answers = given.ToDictionary(x => x.Key, x => x.Value!) }));
        });
        submit.Classes.Add("accent");
        body.Children.Add(Ui.Row(6, submit, Ui.TextButton("Skip", () => _ = RunAsync(() => Chats.AnswerAsync(chatId, q.Id, new AiAnswer { Decision = AiDecision.Deny, Reason = "The user chose not to answer." })))));
        return Card(body, "accent");
    }

    // ---- reading aloud as replies are written ----

    private void AutoSpeak()
    {
        if (!App.Services.Config.Settings.Ai.Tts.AutoSpeak || !App.Services.Speech.Enabled) return;
        var last = _rows.LastOrDefault()?.Message;
        if (last is null || last.Role != "assistant") return;
        if (_autoSpokenId != last.Id)
        {
            if (!last.IsWriting) return; // only replies being written now
            _autoSpokenId = last.Id;
            App.Services.Speech.Stop();
            _autoSpoken = App.Services.Speech.Stream(last.Id);
            _ = App.Services.Speech.StartAsync();
        }
        _autoSpoken?.Update(last.Text);
        if (!last.IsWriting) { _autoSpoken?.Finish(); _autoSpoken = null; }
    }

    // ---- managing ----

    private async Task RenameAsync(string? id)
    {
        if (id is null || Chats.Find(id) is not { } c) return;
        var title = await Ui.PromptAsync(Ui.Owner(this), "Rename chat", "Title", c.Title);
        if (!string.IsNullOrWhiteSpace(title)) Chats.Rename(id, title);
        if (id == _chatId) _title.Text = Chats.Get(id).Title;
    }

    private async Task DeleteAsync(string id)
    {
        if (Chats.Find(id) is not { } c) return;
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Delete chat", $"Delete \"{c.Title}\" and its attachments?")) return;
        await RunAsync(() => Chats.DeleteAsync(id));
        if (id == _chatId) { var next = Chats.List().FirstOrDefault(); if (next is not null) Open(next.Id); else ShowEmpty(); }
    }
}

/// <summary>How full the conversation's context is: a ring and a percentage, the numbers in the tooltip.</summary>
public sealed class ContextRing : UserControl
{
    private readonly Arc _arc = new() { Width = 20, Height = 20, StrokeThickness = 3, StartAngle = -90, SweepAngle = 0 };
    private readonly TextBlock _text = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Dim };

    public ContextRing()
    {
        var back = new Ellipse { Width = 20, Height = 20, Stroke = Ui.Tk("lineSoft"), StrokeThickness = 3 };
        Content = Ui.Row(5, new Panel { Children = { back, _arc }, VerticalAlignment = VerticalAlignment.Center }, _text);
        VerticalAlignment = VerticalAlignment.Center;
        Set(null, null, null);
    }

    public void Set(long? tokens, long? window, double? cost)
    {
        IsVisible = tokens is not null;
        if (tokens is not { } t) return;
        var fraction = window is > 0 ? Math.Clamp((double)t / window.Value, 0, 1) : 0;
        _arc.SweepAngle = 360 * fraction;
        _arc.Stroke = Ui.Tk(fraction > 0.85 ? "error" : fraction > 0.6 ? "warn" : "accent");
        _text.Text = window is > 0 ? $"{fraction * 100:0}%" : $"{t / 1000.0:0.#}k";
        ToolTip.SetTip(this, $"Context: {t:N0} tokens" + (window is > 0 ? $" of {window:N0}" : "") + (cost is > 0 ? $"\nCost so far: ${cost:0.0000}" : ""));
    }
}

/// <summary>Opens conversations in the window's AI Chat page (for AI Actions and <c>avamux ai chat new</c>).</summary>
public sealed class AppChatHost(MainWindow window) : IAiChatHost
{
    public Task<string> OpenChatAsync(AiChatRequest request) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        window.Navigate("chat");
        var page = window.Page<ChatPage>("chat") ?? throw new InvalidOperationException("AI Chat is not available.");
        return page.OpenNew(request);
    }).GetTask();
}
