using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avamux.Core;
using Avamux.Core.Ai;
using Avamux.Plugins;
using Avamux.Views;

namespace Avamux.UiTests;

/// <summary>The AI Chat page over stand-in agents (tests/Shared/fake-claude.py, fake-pi.py) in a sandbox.</summary>
public sealed class ChatHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "avamux-chat-" + Guid.NewGuid().ToString("N")[..8]);
    public AppServices Services { get; }
    public Window Window { get; }
    public ChatPage Page { get; }

    public static string Shared(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "avamux.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "tests", "Shared", name);
    }

    public ChatHarness(Action<ChatHarness>? prepare = null, double width = 1200, double height = 800)
    {
        ClipboardFiles.UseSystemTools = false;
        Directory.CreateDirectory(Path.Combine(Root, "home"));
        Services = new AppServices(new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "home"), Root));
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, Services);
        Services.Ai.Put(new AiProviderConfig { Id = "claude-code", Kind = ClaudeCodeProvider.KindId, Name = "Claude Code", Command = Shared("fake-claude.py") });
        Services.Ai.Put(new AiProviderConfig { Id = "pi", Kind = PiProvider.KindId, Name = "pi", Command = Shared("fake-pi.py") });
        prepare?.Invoke(this);
        Page = new ChatPage();
        Window = new Window { Width = width, Height = height, Content = Page };
        Window.Show();
        Settle();
    }

    public TextBox Input => Page.GetVisualDescendants().OfType<TextBox>().First(t => t.AcceptsReturn);
    public List<ChatMessageView> Messages() => Page.GetVisualDescendants().OfType<ChatMessageView>().ToList();
    /// <summary>Every text shown, including text made of runs (markdown).</summary>
    public IEnumerable<string> Texts() => Page.GetVisualDescendants().OfType<TextBlock>()
        .Select(t => t.Text is { Length: > 0 } text ? text : t.Inlines is { } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : "")
        .Where(t => t.Length > 0);

    public void Settle(int minMs = 0)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        } while (watch.ElapsedMilliseconds < minMs);
    }

    public void WaitFor(Func<bool> condition, int ms = 20000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > ms) throw new TimeoutException("Condition not met in time.");
            Settle(minMs: 20);
        }
        Settle(minMs: 200); // let the last updates (a few a second while writing) show
    }

    public void Send(string text)
    {
        Input.Text = text;
        Input.Focus();
        Settle();
        Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle();
    }

    public string Capture(string name)
    {
        var dir = Environment.GetEnvironmentVariable("AVAMUX_UI_CAPTURES");
        if (string.IsNullOrEmpty(dir)) return "";
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".png");
        Window.CaptureRenderedFrame()?.Save(file);
        return file;
    }

    public void Dispose()
    {
        Window.Close();
        ClipboardFiles.UseSystemTools = true;
        try { Services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class ChatPageTests
{
    private static int Pictures(ChatHarness h) =>
        h.Page.GetSelfAndLogicalDescendants().OfType<Image>().Concat(h.Page.GetVisualDescendants().OfType<Image>()).Distinct().Count(i => i.Source is not null);

    [AvaloniaFact]
    public void Sending_a_message_starts_a_chat_and_shows_the_reply_with_its_thinking()
    {
        using var h = new ChatHarness();
        h.Send("hello avamux");
        var chats = h.Services.Ai.Chats;
        h.WaitFor(() => chats.List().Count == 1 && !chats.IsBusy(chats.List()[0].Id) && h.Messages().Count == 2 && h.Texts().Any(t => t.Contains("Echo: hello avamux")));
        Assert.Equal("", h.Input.Text);
        Assert.Contains(h.Texts(), t => t.StartsWith("Thought for"));
        Assert.Contains(h.Texts(), t => t == "hello avamux"); // the title, from the first message
        Assert.Contains(h.Texts(), t => t.EndsWith('%')); // the context ring
        h.Capture("chat-reply");
    }

    [AvaloniaFact]
    public void A_permission_request_waits_as_a_card_and_allowing_it_lets_the_reply_finish()
    {
        using var h = new ChatHarness();
        h.Send("please write a file");
        var chats = h.Services.Ai.Chats;
        h.WaitFor(() => h.Texts().Any(t => t == "Allow Write?"));
        h.Capture("chat-permission");
        var allow = h.Page.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Allow");
        allow.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.WaitFor(() => !chats.IsBusy(chats.List()[0].Id) && h.Texts().Any(t => t.Contains("written")));
        Assert.DoesNotContain(h.Texts(), t => t == "Allow Write?");
        Assert.Contains(h.Texts(), t => t.StartsWith("Write /tmp/x.txt")); // the tool line
    }

    [AvaloniaFact]
    public void A_question_is_answered_with_one_click()
    {
        using var h = new ChatHarness();
        h.Send("ask me something");
        var chats = h.Services.Ai.Chats;
        h.WaitFor(() => h.Texts().Any(t => t == "Which colour?"));
        h.Capture("chat-question");
        var blue = h.Page.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Blue");
        blue.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.WaitFor(() => !chats.IsBusy(chats.List()[0].Id) && h.Texts().Any(t => t.Contains("You chose Blue")));
    }

    [AvaloniaFact]
    public void Editing_a_message_shows_versions_to_switch_between()
    {
        using var h = new ChatHarness();
        h.Send("first try");
        var chats = h.Services.Ai.Chats;
        h.WaitFor(() => chats.List().Count == 1 && !chats.IsBusy(chats.List()[0].Id) && h.Texts().Any(t => t.Contains("Echo: first try")));
        var chat = chats.List()[0];
        var user = ChatService.Thread(chat)[0];
        _ = chats.EditAndResendAsync(chat.Id, user.Id, "second try");
        h.WaitFor(() => !chats.IsBusy(chat.Id) && h.Texts().Any(t => t.Contains("Echo: second try")));
        Assert.Contains(h.Texts(), t => t == "2/2");
        h.Capture("chat-versions");
        var earlier = h.Page.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Earlier version");
        earlier.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.WaitFor(() => h.Texts().Any(t => t.Contains("Echo: first try")) && h.Texts().Any(t => t == "1/2"));
    }

    [AvaloniaFact]
    public void Chats_are_listed_and_opened_and_keep_their_settings()
    {
        using var h = new ChatHarness(x =>
        {
            var c = x.Services.Ai.Chats.Create(new AiChatRequest { Title = "Older chat", ProviderId = "pi", Model = "Local/small", History = [("user", "an old question"), ("assistant", "an **old** answer\n\n```js\nlet x = 1;\n```")] });
            x.Services.Ai.Chats.Pin(c.Id, true);
        });
        Assert.Contains(h.Texts(), t => t == "Older chat");
        Assert.Contains(h.Texts(), t => t.Contains("an old question"));
        Assert.Contains(h.Texts(), t => t == "js"); // the code block's language, beside its copy button
        var provider = h.Page.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal("pi", (provider.SelectedItem as ComboBoxItem)?.Content);
        h.Capture("chat-history");
    }

    [AvaloniaFact]
    public void Pictures_in_replies_are_shown()
    {
        using var h = new ChatHarness(x =>
        {
            var png = Path.Combine(x.Root, "shot.png");
            var other = Path.Combine(x.Root, "other.png");
            using (var bmp = WikiHarness.Picture(120, 80)) { bmp.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); bmp.Save(other, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
            x.Services.Ai.Chats.Create(new AiChatRequest { Title = "Pictures", ProviderId = "claude-code", History = [("user", "make a picture"), ("assistant", $"Here it is:\n\n![a shot]({png})\n\nI also saved one at {other} for you.")] });
        });
        try { h.WaitFor(() => Pictures(h) >= 2, 8000); }
        finally { h.Capture("chat-pictures"); }
    }

    private static Button Tip(ChatHarness h, string tip) => h.Page.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == tip);
    private static void Click(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void A_reply_can_be_edited()
    {
        using var h = new ChatHarness(x => { EchoProvider(x); x.Services.Ai.Chats.Create(new AiChatRequest { Title = "Edit", ProviderId = "echo", History = [("user", "a question"), ("assistant", "the old answer")] }); });
        h.WaitFor(() => h.Texts().Any(t => t.Contains("the old answer")));
        Click(Tip(h, "Edit this reply (only the saved text changes)"));
        h.Settle(200);
        var box = h.Page.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Text == "the old answer");
        Assert.NotNull(box);
        box!.Text = "my new answer";
        Click(h.Page.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Save"));
        h.WaitFor(() => h.Texts().Any(t => t.Contains("my new answer")));
        Assert.Equal("my new answer", ChatService.Thread(h.Services.Ai.Chats.List()[0])[1].Text);
    }

    [AvaloniaFact]
    public void Thinking_opened_by_hand_stays_open_while_the_reply_is_written()
    {
        using var h = new ChatHarness();
        h.Send("slow please");
        var chats = h.Services.Ai.Chats;
        Expander Fold() => h.Page.GetVisualDescendants().OfType<Expander>().First();
        h.WaitFor(() => h.Page.GetVisualDescendants().OfType<Expander>().Any() && h.Texts().Any(t => t.Contains("one two")));
        Fold().IsExpanded = true;
        var shown = Fold();
        // Words keep arriving (the message is built again): the thinking stays open in the new build.
        h.WaitFor(() => !ReferenceEquals(Fold(), shown) && h.Texts().Any(t => t.Contains("nine ten one two")));
        Assert.True(Fold().IsExpanded);
        Fold().IsExpanded = false;
        shown = Fold();
        h.WaitFor(() => !ReferenceEquals(Fold(), shown));
        Assert.False(Fold().IsExpanded);
        _ = chats.StopAsync(chats.List()[0].Id);
        h.WaitFor(() => !chats.IsBusy(chats.List()[0].Id));
    }

    [AvaloniaFact]
    public void A_reply_written_again_switches_versions_on_the_reply()
    {
        using var h = new ChatHarness();
        h.Send("first try");
        var chats = h.Services.Ai.Chats;
        h.WaitFor(() => chats.List().Count == 1 && !chats.IsBusy(chats.List()[0].Id) && h.Texts().Any(t => t.Contains("Echo: first try")));
        Click(Tip(h, "Write this reply again (the old one stays as an earlier version)"));
        var chat = chats.List()[0];
        h.WaitFor(() => !chats.IsBusy(chat.Id) && h.Texts().Count(t => t == "2/2") == 1);
        // The ‹ 2/2 › is beside the reply's buttons, not under the question (its text is the same).
        var earlier = Tip(h, "Earlier version");
        var view = earlier.GetVisualAncestors().OfType<ChatMessageView>().First();
        Assert.Same(h.Messages()[1], view);
        // Straight after "Write this reply again", which makes the versions.
        var switcher = (Control)earlier.Parent!;
        var actions = (Panel)switcher.Parent!;
        Assert.Equal("Write this reply again (the old one stays as an earlier version)", ToolTip.GetTip(actions.Children[actions.Children.IndexOf(switcher) - 1]));
        h.Capture("chat-reply-versions");
        Click(earlier);
        h.WaitFor(() => h.Texts().Any(t => t == "1/2"));
    }

    [AvaloniaFact]
    public void An_edit_stays_open_while_the_chat_updates()
    {
        using var h = new ChatHarness(x => { EchoProvider(x); x.Services.Ai.Chats.Create(new AiChatRequest { Title = "Edit", ProviderId = "echo", History = [("user", "a question"), ("assistant", "the old answer")] }); });
        h.WaitFor(() => h.Texts().Any(t => t.Contains("the old answer")));
        Click(Tip(h, "Edit this reply (only the saved text changes)"));
        h.Settle(100);
        var box = h.Page.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "the old answer");
        box.Text = "half typed";
        // Something else changes (the open chat saved, speech starting or stopping): the editor and its text stay.
        var chat = h.Services.Ai.Chats.List()[0];
        h.Services.Ai.Chats.Rename(chat.Id, "Renamed");
        h.Services.Speech.Stop();
        h.Settle(300);
        Assert.Contains(h.Page.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "half typed");
    }

    /// <summary>A provider avamux gives the whole conversation each turn (its replies can be edited).</summary>
    private static void EchoProvider(ChatHarness x) =>
        x.Services.Ai.Put(new AiProviderConfig { Id = "echo", Kind = CommandProvider.KindId, Name = "Echo", Command = "echo", Options = { ["template"] = "{prompt}", ["output"] = "text" } });

    [AvaloniaFact]
    public void Replies_of_pi_and_Claude_Code_are_not_editable()
    {
        using var h = new ChatHarness(x => x.Services.Ai.Chats.Create(new AiChatRequest { Title = "Agent", ProviderId = "pi", History = [("user", "a question"), ("assistant", "the answer")] }));
        h.WaitFor(() => h.Texts().Any(t => t.Contains("the answer")));
        var edit = h.Page.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string ?? "").StartsWith("Replies can't be edited"));
        Assert.False(edit.IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(edit));
        // It is the pencil that is turned off, and only it.
        Assert.Same(Icons.Pencil, ((PathIcon)((StackPanel)edit.Content!).Children[0]).Data);
        var row = (Panel)edit.Parent!;
        Assert.All(row.Children.OfType<Button>().Where(b => b != edit), b => Assert.True(b.IsEnabled));
        // The user's own message can still be edited and sent again.
        Assert.True(Tip(h, "Edit and send again (the reply below stays as an earlier version)").IsEnabled);
    }
}

/// <summary>The list of chats answers clicks anywhere on an entry, not only on its text.</summary>
public class ChatListTests
{
    private static ListBoxItem Entry(ChatHarness h, string title) =>
        h.Page.GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as AiChat)?.Title == title);

    private static void Press(ChatHarness h, ListBoxItem item, Point inside, MouseButton button)
    {
        var at = item.TranslatePoint(inside, h.Window)!.Value;
        h.Window.MouseMove(at);
        h.Window.MouseDown(at, button);
        h.Window.MouseUp(at, button);
        h.Settle();
    }

    private static ChatHarness TwoChats() => new(x =>
    {
        x.Services.Ai.Chats.Create(new AiChatRequest { Title = "First", ProviderId = "pi", History = [("user", "one"), ("assistant", "1")] });
        x.Services.Ai.Chats.Create(new AiChatRequest { Title = "Second", ProviderId = "pi", History = [("user", "two"), ("assistant", "2")] });
    });

    [AvaloniaFact]
    public void Clicking_the_edge_of_an_entry_opens_its_chat()
    {
        using var h = TwoChats();
        string Other() => h.Services.Ai.Chats.List().First(c => c.Id != h.Page.CurrentChat).Title;
        foreach (var inside in new Func<ListBoxItem, Point>[] { i => new Point(2, 2), i => new Point(i.Bounds.Width - 3, i.Bounds.Height / 2) }) // the padding; right of the text
        {
            var title = Other();
            var item = Entry(h, title);
            Press(h, item, inside(item), MouseButton.Left);
            Assert.Equal(title, h.Services.Ai.Chats.Find(h.Page.CurrentChat!)!.Title);
        }
    }

    [AvaloniaFact]
    public void Right_clicking_anywhere_on_an_entry_opens_its_menu()
    {
        using var h = TwoChats();
        foreach (var inside in new Func<ListBoxItem, Point>[] { i => new Point(2, 2), i => new Point(i.Bounds.Width - 3, i.Bounds.Height / 2), i => new Point(i.Bounds.Width / 2, i.Bounds.Height - 2) })
        {
            var item = Entry(h, "Second");
            Press(h, item, inside(item), MouseButton.Right);
            var menu = h.Page.ChatMenuShown;
            Assert.NotNull(menu);
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.Header as string == "Rename…");
            menu.Close();
            h.Settle();
        }
    }

    [AvaloniaFact]
    public void The_menu_stays_open_while_the_list_changes_under_it()
    {
        using var h = TwoChats();
        var item = Entry(h, "Second");
        Press(h, item, new Point(item.Bounds.Width - 3, item.Bounds.Height / 2), MouseButton.Right);
        var menu = h.Page.ChatMenuShown!;
        var first = h.Services.Ai.Chats.List().First(c => c.Title == "First");
        h.Services.Ai.Chats.Pin(first.Id, true); // as a busy chat does, many times a second
        h.Settle(minMs: 800);
        Assert.True(menu.IsOpen);
        Assert.Same(item, Entry(h, "Second"));
        menu.Close();
        h.Settle(minMs: 200);
        Assert.True(((AiChat)Entry(h, "First").DataContext!).Pinned); // refreshed once the menu closed
    }
}
