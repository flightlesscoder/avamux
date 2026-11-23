using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avamux.Core.Ai;
using Avamux.Core.Logging;

namespace Avamux.Views;

/// <summary>A message as the chat list holds it: the message, and what the view needs to know around it.</summary>
public sealed class ChatRow(string chatId, AiChatMessage message)
{
    public string ChatId { get; } = chatId;
    public AiChatMessage Message { get; set; } = message;
    /// <summary>Raised (on the UI thread) when the message changed and its view should show it again.</summary>
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();
    /// <summary>What was last shown, so an unchanged message is not built again.</summary>
    internal string Shown = "";
}

/// <summary>
/// One message of a chat: the user's on the right, the model's on the left with its thinking (folded), its tool calls
/// (one line each, open for the details), its text as markdown with pictures, and Copy, Edit, Speak, Retry and Insert
/// into Wiki. A message being written updates a few times a second.
/// </summary>
public sealed partial class ChatMessageView : UserControl
{
    private readonly ChatRow _row;
    private readonly ChatPage _page;
    private bool _editing;
    private DispatcherTimer? _dots;
    private DateTime _lastBuild;
    private bool _buildPending;

    private static ChatService Chats => App.Services.Ai.Chats;

    public ChatMessageView(ChatRow row, ChatPage page)
    {
        _row = row;
        _page = page;
        Margin = new Thickness(0, 6);
        AttachedToVisualTree += (_, _) => { _row.Changed += OnChanged; Build(force: true); };
        DetachedFromVisualTree += (_, _) => { _row.Changed -= OnChanged; _dots?.Stop(); };
    }

    private void OnChanged()
    {
        // A reply streaming in: build at most about eight times a second.
        var since = DateTime.Now - _lastBuild;
        if (!_row.Message.IsWriting || since > TimeSpan.FromMilliseconds(120)) { Build(); return; }
        if (_buildPending) return;
        _buildPending = true;
        DispatcherTimer.RunOnce(() => { _buildPending = false; Build(); }, TimeSpan.FromMilliseconds(120) - since);
    }

    private static string Signature(AiChatMessage m) =>
        $"{m.Text.Length}|{m.Text.GetHashCode()}|{m.Thinking?.Length}|{m.Tools.Count}|{m.Tools.Count(t => t.Done)}|{m.IsWriting}|{m.IsThinking}|{m.Error}|{m.Edited}|{m.Notes.Count}|{m.Stopped}";

    private void Build(bool force = false)
    {
        if (_editing) return;
        var m = _row.Message;
        var sig = Signature(m) + "|" + App.Services.Speech.SpeakingTag;
        if (!force && sig == _row.Shown && Content is not null) return;
        _row.Shown = sig;
        _lastBuild = DateTime.Now;
        try { Content = m.Role == "user" ? UserMessage(m) : AssistantMessage(m); }
        catch (Exception ex) { Log.Error("chat", "Showing a message failed", ex); Content = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap }; }
    }

    // ---- the user's ----

    private Control UserMessage(AiChatMessage m)
    {
        var body = new StackPanel { Spacing = 6 };
        if (m.Text.Length > 0) body.Children.Add(new SelectableTextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 });
        if (m.Attachments.Count > 0)
        {
            var wrap = new WrapPanel();
            foreach (var a in m.Attachments)
                wrap.Children.Add(a.IsImage ? Ui.Apply(Picture(a.Path, a.Name, 160, 120), p => p.Margin = new Thickness(0, 0, 6, 6))
                    : Ui.Apply(new Border
                    {
                        Background = Ui.Tk("bgDeep"), CornerRadius = Ui.R(4), Padding = new Thickness(6, 2),
                        Child = new TextBlock { Text = "📎 " + a.Name, FontSize = 11.5 },
                    }, c => { c.Margin = new Thickness(0, 0, 6, 6); ToolTip.SetTip(c, a.Path); }));
            body.Children.Add(wrap);
        }
        var bubble = new Border
        {
            Background = new SolidColorBrush(Ui.Color("accent"), 0.16), CornerRadius = Ui.R(10), Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 680, Child = body,
        };
        var actions = Actions(
            (Icons.ContentCopy, "Copy", () => Copy(m.Text)),
            (Icons.Pencil, "Edit and send again (the reply below stays as an earlier version)", () => BeginEdit(m, resend: true)));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var column = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, Children = { bubble } };
        var footer = Ui.Row(4);
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        if (Versions(m) is { } switcher) footer.Children.Add(switcher);
        footer.Children.Add(actions);
        column.Children.Add(footer);
        return column;
    }

    /// <summary>‹ 2/3 › between the versions of a message, when there is more than one.</summary>
    private Control? Versions(AiChatMessage m)
    {
        var chat = Chats.Find(_row.ChatId);
        if (chat is null) return null;
        var versions = ChatService.Versions(chat, m);
        if (versions.Count < 2) return null;
        var i = versions.FindIndex(v => v.Id == m.Id);
        var prev = Ui.IconButton(Icons.ChevronLeft, "Earlier version", () => Chats.SelectVersion(_row.ChatId, versions[i - 1].Id));
        var next = Ui.IconButton(Icons.ChevronRight, "Later version", () => Chats.SelectVersion(_row.ChatId, versions[i + 1].Id));
        prev.IsEnabled = i > 0;
        next.IsEnabled = i < versions.Count - 1;
        return Ui.Row(0, prev, new TextBlock { Text = $"{i + 1}/{versions.Count}", Foreground = Ui.Dim, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }, next);
    }

    // ---- the model's ----

    private Control AssistantMessage(AiChatMessage m)
    {
        var column = new StackPanel { Spacing = 6, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch };
        var waiting = m.IsWriting && m.Text.Length == 0;
        if (!string.IsNullOrEmpty(m.Thinking) || m.IsThinking || waiting && m.Tools.Count == 0)
            column.Children.Add(Thinking(m, live: m.IsWriting && m.Text.Length == 0 && (m.IsThinking || m.Tools.Count == 0)));
        foreach (var tool in m.Tools) column.Children.Add(Tool(tool));
        if (m.Text.Length > 0)
        {
            column.Children.Add(MarkdownView.Build(m.Text, 13.5, new MarkdownOptions
            {
                Selectable = true, CopyCode = true,
                Picture = (src, alt) => Resolve(src) is { } path ? Picture(path, alt, 520, 360) : null,
            }));
            // Pictures the reply names by their path (an agent saying where it saved one), shown under it.
            var named = PicturePaths(m.Text).Where(p => !m.Text.Contains($"]({p})", StringComparison.Ordinal)).Take(6).ToList();
            if (named.Count > 0)
            {
                var wrap = new WrapPanel();
                foreach (var p in named) wrap.Children.Add(Ui.Apply(Picture(p, Path.GetFileName(p), 360, 260), x => x.Margin = new Thickness(0, 0, 8, 8)));
                column.Children.Add(wrap);
            }
        }
        foreach (var note in m.Notes) column.Children.Add(new TextBlock { Text = note, Foreground = Ui.Dim, FontSize = 11.5, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap });
        if (m.Error is { } error)
            column.Children.Add(new Border
            {
                Background = new SolidColorBrush(Ui.Color("error"), 0.14), CornerRadius = Ui.R(6), Padding = new Thickness(10, 6),
                Child = new SelectableTextBlock { Text = error, Foreground = Ui.Tk("error"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
            });
        if (m.Stopped) column.Children.Add(new TextBlock { Text = "Stopped.", Foreground = Ui.Dim, FontSize = 11.5 });
        if (!m.IsWriting)
        {
            var speaking = App.Services.Speech.SpeakingTag == m.Id;
            var actions = Actions(
                (Icons.ContentCopy, "Copy (markdown)", () => Copy(m.Text)),
                (Icons.TextBoxOutline, "Copy as plain text", () => Copy(SpeechService.Speakable(m.Text).Trim())),
                (Icons.Pencil, "Edit this reply (only the saved text changes)", () => BeginEdit(m, resend: false)),
                (speaking ? Icons.Stop : Icons.VolumeHigh, speaking ? "Stop speaking" : "Speak (Kokoro)", () => _ = SpeechUi.ToggleAsync(this, m.Text, m.Id)),
                (Icons.Refresh, "Write this reply again (the old one stays as an earlier version)", () => _ = _page.RunAsync(() => Chats.RetryAsync(_row.ChatId, m.Id))),
                (Icons.BookOpenVariant, "Insert into a wiki page…", () => _ = InsertIntoWikiAsync(m.Text)));
            var footer = Ui.Row(6, actions);
            if (m.Edited) footer.Children.Add(new TextBlock { Text = "edited", Foreground = Ui.Tk("faint"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            if (m.Model is { Length: > 0 } model) footer.Children.Add(new TextBlock { Text = model, Foreground = Ui.Tk("faint"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            column.Children.Add(footer);
        }
        return column;
    }

    private Control Thinking(AiChatMessage m, bool live)
    {
        var label = new TextBlock { Foreground = Ui.Dim, FontSize = 12.5, FontStyle = FontStyle.Italic, VerticalAlignment = VerticalAlignment.Center };
        if (live)
        {
            var step = 0;
            label.Text = "Thinking";
            _dots?.Stop();
            _dots = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
            _dots.Tick += (_, _) => label.Text = "Thinking" + new string('.', ++step % 4);
            _dots.Start();
        }
        else
        {
            _dots?.Stop();
            label.Text = m.ThinkingSeconds is { } s ? $"Thought for {Math.Max(1, Math.Round(s)):0} s" : "Thought";
        }
        var header = Ui.Row(6, new PathIcon { Data = Icons.Sparkles, Width = 12, Height = 12, Foreground = Ui.Tk("purple") }, label);
        if (string.IsNullOrEmpty(m.Thinking)) return header;
        return new Expander
        {
            Header = header, IsExpanded = App.Services.Config.Settings.Ai.ExpandThinking, Padding = new Thickness(10, 4),
            Content = new SelectableTextBlock { Text = m.Thinking, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Dim, FontSize = 12.5, FontStyle = FontStyle.Italic },
        };
    }

    private static Control Tool(AiChatTool t)
    {
        var icon = new PathIcon
        {
            Data = !t.Done ? Icons.Refresh : t.IsError ? Icons.AlertCircle : Icons.Check, Width = 12, Height = 12,
            Foreground = Ui.Tk(!t.Done ? "accent" : t.IsError ? "error" : "ok"),
        };
        var header = Ui.Row(6, icon, new TextBlock { Text = t.Summary, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Dim, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 720 });
        var details = new StackPanel { Spacing = 6 };
        if (t.Input is { Length: > 2 } input) details.Children.Add(Mono(input));
        if (t.Output is { Length: > 0 } output) details.Children.Add(Mono(output.Length > 4000 ? output[..4000] + "\n…" : output, t.IsError));
        if (details.Children.Count == 0) return header;
        return new Expander { Header = header, Padding = new Thickness(10, 4), Content = details };
    }

    private static Control Mono(string text, bool error = false) => new Border
    {
        Background = Ui.Tk("bgEditor"), CornerRadius = Ui.R(5), Padding = new Thickness(8, 5),
        Child = new SelectableTextBlock { Text = text, FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = error ? Ui.Tk("error") : Ui.Tk("textCode") },
    };

    private static StackPanel Actions(params (Geometry Icon, string Tip, Action Click)[] actions)
    {
        var row = Ui.Row(0);
        foreach (var (icon, tip, click) in actions)
        {
            var b = Ui.IconButton(icon, tip, click);
            b.Padding = new Thickness(5, 3);
            b.Opacity = 0.7;
            row.Children.Add(b);
        }
        return row;
    }

    private void Copy(string text) => _ = App.Services.Host.SetClipboardAsync(text);

    // ---- editing ----

    private void BeginEdit(AiChatMessage m, bool resend)
    {
        _editing = true;
        var box = new TextBox { Text = m.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 420, FontSize = 13.5 };
        void Done() { _editing = false; Build(force: true); }
        var save = Ui.TextButton(resend ? "Send" : "Save", () =>
        {
            var text = box.Text ?? "";
            Done();
            if (resend) _ = _page.RunAsync(() => Chats.EditAndResendAsync(_row.ChatId, m.Id, text));
            else Ui.Try(this, () => Chats.EditReply(_row.ChatId, m.Id, text));
        }, resend ? "Send this as a new version of the message" : "Keep this text as the reply (nothing is sent)");
        save.Classes.Add("accent");
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Done(); }
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; save.Command?.Execute(null); save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); }
        };
        Content = new StackPanel
        {
            Spacing = 6, MaxWidth = 860, HorizontalAlignment = m.Role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Children =
            {
                box,
                Ui.Apply(Ui.Row(6, Ui.TextButton("Cancel", Done), save), r => r.HorizontalAlignment = HorizontalAlignment.Right),
                new TextBlock { Text = resend ? "Ctrl+Enter sends; the earlier version stays (‹ › switch between them)." : "Ctrl+Enter saves.", Foreground = Ui.Dim, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right },
            },
        };
        Dispatcher.UIThread.Post(() => { box.Focus(); box.CaretIndex = box.Text?.Length ?? 0; }, DispatcherPriority.Background);
    }

    private async Task InsertIntoWikiAsync(string text)
    {
        var wiki = Ui.Main?.Page<WikiPage>("wiki");
        var current = wiki?.CurrentPath ?? "";
        var target = await Ui.PromptAsync(Ui.Owner(this), "Insert into a wiki page", "Page to add the reply to (it is added at the end; a new page is created if there is none):", current);
        if (string.IsNullOrWhiteSpace(target)) return;
        Ui.Try(this, () =>
        {
            var w = App.Services.Wiki;
            var page = w.FindPage(target.Trim());
            var before = page is null ? "" : w.Read(page).TrimEnd('\n') + "\n\n";
            var written = w.Write(page ?? target.Trim(), before + text.Trim() + "\n");
            Ui.Toast(this, "Added to the wiki", written);
        }, "Could not add it to the wiki");
    }

    // ---- pictures ----

    [GeneratedRegex(@"(?<![\w(])(?:~|/)[^\s'""`<>()\[\]]+\.(?:png|jpe?g|gif|webp|bmp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PathPattern();

    /// <summary>Local picture files a text names by path (that exist).</summary>
    public static List<string> PicturePaths(string text) =>
        PathPattern().Matches(text).Select(x => AiService.ExpandHome(x.Value)).Where(File.Exists).Distinct().ToList();

    /// <summary>A picture's source as a local path or a web address; null when it is neither.</summary>
    private string? Resolve(string src)
    {
        if (src.StartsWith("http://") || src.StartsWith("https://")) return src;
        if (src.StartsWith("file://")) src = Uri.UnescapeDataString(new Uri(src).AbsolutePath);
        var path = AiService.ExpandHome(Uri.UnescapeDataString(src));
        if (!Path.IsPathRooted(path) && Chats.Find(_row.ChatId) is { } chat) path = Path.Combine(chat.Folder, path);
        return File.Exists(path) ? path : null;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>A picture, shown small: click to see it whole; right-click (or the download button) to save, copy or show it.</summary>
    public static Control Picture(string source, string alt, double maxWidth, double maxHeight)
    {
        var image = new Image { Stretch = Stretch.Uniform, MaxWidth = maxWidth, MaxHeight = maxHeight };
        var holder = new Border
        {
            CornerRadius = Ui.R(6), ClipToBounds = true, BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand), MinWidth = 40, MinHeight = 30,
        };
        var remote = source.StartsWith("http://") || source.StartsWith("https://");
        byte[]? bytes = null;
        async Task LoadAsync()
        {
            try
            {
                bytes = remote ? await Http.GetByteArrayAsync(source) : await File.ReadAllBytesAsync(source);
                if (bytes.Length > 40 * 1024 * 1024) { holder.Child = new TextBlock { Text = alt + " (too big to show)", Margin = new Thickness(8) }; return; }
                using var ms = new MemoryStream(bytes);
                var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(new MemoryStream(bytes), (int)Math.Min(2048, maxWidth * 2)));
                image.Source = bitmap;
            }
            catch (Exception ex) { holder.Child = new TextBlock { Text = $"{alt} (could not be shown: {ex.Message})", Foreground = Ui.Dim, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth }; }
        }
        var save = Ui.IconButton(Icons.Download, "Save a copy…", () => _ = SaveAsync(holder, source, bytes, alt));
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.VerticalAlignment = VerticalAlignment.Top;
        save.Margin = new Thickness(4);
        save.Background = new SolidColorBrush(Ui.Color("panel"), 0.85);
        holder.Child = new Panel { Children = { image, save } };
        ToolTip.SetTip(holder, alt.Length > 0 ? $"{alt}\n{source}" : source);
        holder.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && image.Source is Bitmap b) ImageWindow.Show(b, alt.Length > 0 ? alt : Path.GetFileName(source), holder); };
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("View", () => { if (image.Source is Bitmap b) ImageWindow.Show(b, alt, holder); }));
        menu.Items.Add(Ui.Item("Save a Copy…", () => _ = SaveAsync(holder, source, bytes, alt), icon: Icons.Download));
        if (!remote)
        {
            menu.Items.Add(Ui.Item("Copy Picture", async () => { if (TopLevel.GetTopLevel(holder) is { } top) await ClipboardFiles.CopyImageAsync(top, source); }, icon: Icons.ContentCopy));
            menu.Items.Add(Ui.Item("Copy Path", () => _ = App.Services.Host.SetClipboardAsync(source)));
            menu.Items.Add(Ui.Item("Show in Folder", () => Avamux.Core.Shell.ShowInFolder(source)));
            menu.Items.Add(Ui.Item("Open in Default App", () => Avamux.Core.Shell.Open(source)));
        }
        else
        {
            menu.Items.Add(Ui.Item("Copy Address", () => _ = App.Services.Host.SetClipboardAsync(source)));
            menu.Items.Add(Ui.Item("Open in Browser", () => Avamux.Core.Shell.Open(source)));
        }
        holder.ContextMenu = menu;
        _ = LoadAsync();
        return holder;
    }

    private static async Task SaveAsync(Control from, string source, byte[]? bytes, string alt)
    {
        var name = Path.GetFileName(new Uri(source.StartsWith("http") ? source : "file://" + source).LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name)) name = (alt.Length > 0 ? alt : "picture") + ".png";
        var file = await Ui.Owner(from).StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save a copy of the picture", SuggestedFileName = name });
        if (file?.TryGetLocalPath() is not { } target) return;
        try
        {
            bytes ??= source.StartsWith("http") ? await Http.GetByteArrayAsync(source) : await File.ReadAllBytesAsync(source);
            await File.WriteAllBytesAsync(target, bytes);
            Ui.Toast(from, "Picture saved", target);
        }
        catch (Exception ex) { Ui.Toast(from, "Could not save the picture", ex.Message); }
    }
}

/// <summary>A picture in a window of its own, as big as fits.</summary>
public sealed class ImageWindow : AppWindow
{
    public static void Show(Bitmap bitmap, string title, Control near)
    {
        var w = new ImageWindow
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Picture" : title, Icon = AppIcon.Window,
            Width = Math.Clamp(bitmap.PixelSize.Width + 40, 320, 1400), Height = Math.Clamp(bitmap.PixelSize.Height + 70, 240, 1000),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        w.Body = new Border { Background = Ui.Tk("bgDeep"), Padding = new Thickness(10), Child = new Image { Source = bitmap, Stretch = Stretch.Uniform } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        if (TopLevel.GetTopLevel(near) is Window owner) w.Show(owner); else w.Show();
    }
}
