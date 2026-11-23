using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avamux.Core.Ai;
using Avamux.Plugins;
using Avamux.Plugins.AiActions;
using Avamux.Views;

namespace Avamux.UiTests;

public class AiActionsPromptTests
{
    [Fact]
    public void Tags_are_read_from_a_reply()
    {
        Assert.Equal(["alpha", "beta/gamma", "c"], Prompts.ParseTags("Here you go:\n#Alpha #beta/gamma, #c #alpha"));
    }

    [Fact]
    public void A_reply_wrapped_in_one_fence_is_unwrapped()
    {
        Assert.Equal("# T\n\ntext\n", Prompts.Unwrap("```markdown\n# T\n\ntext\n```"));
        Assert.Equal("plain\n", Prompts.Unwrap("plain"));
    }

    [Fact]
    public void The_line_diff_marks_what_changed()
    {
        var d = LineDiff.Compute("a\nb\nc\n", "a\nB\nc\nd\n");
        Assert.Equal(["  a", "- b", "+ B", "  c", "+ d"], d.Select(x => (x.Kind switch { LineDiff.Kind.Added => "+ ", LineDiff.Kind.Removed => "- ", _ => "  " }) + x.Line));
    }

    [Fact]
    public void Placeholders_are_filled()
    {
        Assert.Equal("Do x with sel on p.md", Prompts.Fill("Do {{input}} with {{selection}} on {{pagePath}}", new Dictionary<string, string> { ["input"] = "x", ["selection"] = "sel", ["pagePath"] = "p.md" }));
    }
}

/// <summary>AI Actions in the wiki editor, against the stand-in Claude Code.</summary>
public class AiActionsTests
{
    private static void Fill(WikiHarness h)
    {
        h.Wiki.Write("Notes/Plan.md", "# Plan\n\nSome  text   about #existing things.\n");
        h.Services.Ai.Put(new AiProviderConfig { Id = "claude-code", Kind = ClaudeCodeProvider.KindId, Name = "Claude Code", Command = ChatHarness.Shared("fake-claude.py") });
        h.Services.Plugins.Ui = new AppPluginUi();
        h.Services.Plugins.AddBuiltIn(() => new AiActionsPlugin());
        h.Services.Plugins.Start();
        h.Services.Tags.Start();
        h.Services.Tags.Ready.Wait(5000);
    }

    private static PluginMenuItem Action(WikiHarness h, string header)
    {
        var items = h.Services.Plugins.MenuItems(new MenuContext { Menu = Menus.WikiEditor, WikiPath = "Notes/Plan.md", SelectedText = h.Editor.SelectedText, Editor = h.Page.EditorAccess });
        var sub = Assert.Single(items, i => i.Header == "AI Actions");
        return sub.Children.First(c => c.Header == header);
    }

    private static T WaitForView<T>(WikiHarness h, Func<T, bool>? ready = null, int ms = 20000) where T : Control
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            h.Settle();
            List<Window> open;
            lock (AppPluginUi.Open) open = [.. AppPluginUi.Open];
            var view = open.SelectMany(w => w.GetSelfAndLogicalDescendants().OfType<T>()).FirstOrDefault();
            if (view is not null && (ready is null || ready(view))) return view;
            if (watch.ElapsedMilliseconds > ms) throw new TimeoutException($"No {typeof(T).Name} in time.");
            Thread.Sleep(20);
        }
    }

    private static void Click(Control root, string text)
    {
        var button = root.GetSelfAndLogicalDescendants().OfType<Button>().First(b => b.Content as string == text && b.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void CloseAll() { List<Window> open; lock (AppPluginUi.Open) open = [.. AppPluginUi.Open]; foreach (var w in open) w.Close(); }

    [AvaloniaFact]
    public void The_menu_has_the_actions_for_a_page()
    {
        using var h = new WikiHarness(Fill, open: "Notes/Plan.md");
        var items = h.Services.Plugins.MenuItems(new MenuContext { Menu = Menus.WikiEditor, WikiPath = "Notes/Plan.md", Editor = h.Page.EditorAccess });
        var headers = Assert.Single(items).Children.Where(c => !c.IsSeparator).Select(c => c.Header).ToList();
        Assert.Equal(["Add Tags", "Summarize", "Format", "Rewrite…", "Ask About This Page…", "Do Research…", "List the To-dos"], headers);
        var terminal = h.Services.Plugins.MenuItems(new MenuContext { Menu = Menus.Terminal, TerminalId = "t1", SelectedText = "error: x" });
        Assert.Contains(Assert.Single(terminal).Children, c => c.Header == "Explain Selection");
        // Switched off: gone.
        h.Services.Plugins.SetEnabled("ai-actions", false);
        Assert.Empty(h.Services.Plugins.MenuItems(new MenuContext { Menu = Menus.WikiEditor, WikiPath = "Notes/Plan.md" }));
    }

    [AvaloniaFact]
    public void Add_Tags_suggests_tags_and_adds_the_ones_kept_as_one_undo_step()
    {
        using var h = new WikiHarness(Fill, open: "Notes/Plan.md");
        try
        {
            _ = Action(h, "Add Tags").OnClick!();
            var view = WaitForView<TagView>(h, v => v.GetLogicalDescendants().OfType<CheckBox>().Any());
            var boxes = view.GetLogicalDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(["#alpha  (new)", "#beta/gamma  (new)"], boxes.Select(b => b.Content as string)); // #existing is on the page already
            boxes[1].IsChecked = false;
            Click(view, "Add Tags");
            h.Settle(minMs: 300);
            Assert.Equal("# Plan\n\nSome  text   about #existing things.\n\nTags: #alpha\n", h.Text);
            h.Editor.Undo();
            Assert.Equal("# Plan\n\nSome  text   about #existing things.\n", h.Text);
        }
        finally { CloseAll(); }
    }

    [AvaloniaFact]
    public void Format_shows_the_change_and_Accept_makes_it()
    {
        using var h = new WikiHarness(Fill, open: "Notes/Plan.md");
        try
        {
            _ = Action(h, "Format").OnClick!();
            var view = WaitForView<DiffView>(h, v => v.GetLogicalDescendants().OfType<SelectableTextBlock>().Any());
            var lines = view.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("+ - one", lines);
            Assert.Contains("- Some  text   about #existing things.", lines);
            Click(view, "Accept");
            h.Settle(minMs: 200);
            Assert.Equal("# Title\n\n- one\n- two\n", h.Text);
        }
        finally { CloseAll(); }
    }

    [AvaloniaFact]
    public void Summarize_shows_the_summary_and_Open_in_AI_Chat_carries_it_on()
    {
        using var h = new WikiHarness(Fill, open: "Notes/Plan.md");
        try
        {
            _ = Action(h, "Summarize").OnClick!();
            var view = WaitForView<ResultView>(h, v => v.GetLogicalDescendants().OfType<Button>().Any(b => b.Content as string == "Open in AI Chat"));
            Assert.Contains("point one", view.Text);
            Click(view, "Open in AI Chat");
            h.Settle(minMs: 300);
            var chat = Assert.Single(h.Services.Ai.Chats.List());
            var thread = ChatService.Thread(chat);
            Assert.Equal(["user", "assistant"], thread.Select(m => m.Role));
            Assert.Contains("point one", thread[1].Text);
            Assert.NotNull(thread[1].ProviderSession); // the chat continues that conversation
        }
        finally { CloseAll(); }
    }

    [AvaloniaFact]
    public void Do_Research_opens_a_chat_set_to_search_the_wiki()
    {
        using var h = new WikiHarness(Fill, open: "Notes/Plan.md");
        _ = Action(h, "Do Research…").OnClick!();
        h.Settle(minMs: 100);
        var chat = Assert.Single(h.Services.Ai.Chats.List());
        Assert.Equal("Research", chat.Title);
        Assert.Equal(h.Wiki.Root, chat.Folder);
        Assert.Contains(h.Wiki.Root, chat.SystemPrompt);
        Assert.Contains("name the pages you used", chat.SystemPrompt);
    }

    [AvaloniaFact]
    public void Custom_actions_are_kept_and_offered()
    {
        using var h = new WikiHarness(Fill, open: "Notes/Plan.md");
        var store = new CustomActionStore(Path.Combine(h.Services.Paths.DataDir, "plugin-data", "ai-actions"));
        var actions = store.Load();
        actions.Add(new CustomAction { Name = "Shout", Prompt = "Shout {{selection}}", Menus = [Menus.Terminal] });
        store.Save(actions);
        var terminal = h.Services.Plugins.MenuItems(new MenuContext { Menu = Menus.Terminal, TerminalId = "t1", SelectedText = "x" });
        Assert.Contains(Assert.Single(terminal).Children, c => c.Header == "Shout");
    }
}
