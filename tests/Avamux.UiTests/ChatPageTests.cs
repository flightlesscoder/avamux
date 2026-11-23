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
}
