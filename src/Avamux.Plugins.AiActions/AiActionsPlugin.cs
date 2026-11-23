using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Avamux.Plugins.AiActions;

/// <summary>
/// AI Actions: an "AI Actions" submenu in the wiki's editor, preview and folder tree, and in terminals. Add Tags,
/// Summarize, Format, Rewrite…, Ask About This Page / Folder…, Do Research…, moving text between pages and terminals,
/// and the user's own actions. Everything goes through the plugin contract: avamux's commands for the wiki and
/// terminals, <see cref="IAiService"/> for the models, and windows of its own.
/// </summary>
public sealed partial class AiActionsPlugin : IAvamuxPlugin, IMenuContributor
{
    private IPluginContext? _context;
    private Look? _look;
    private CustomActionStore? _custom;

    public string Id => "ai-actions";
    public string Name => "AI Actions";
    public string Description => "AI Actions in the wiki's and terminals' right-click menus: add tags, summarise, format, rewrite, research the wiki, and actions of your own.";

    public void Activate(IPluginContext context)
    {
        _context = context;
        _look = new Look(context);
        _custom = new CustomActionStore(context.DataDirectory);
        context.AddMenuItems(this);
        context.AddSettingsView(() => new CustomActionsView(context, _custom, _look));
    }

    public void Deactivate() { _context = null; _custom = null; }

    private IPluginContext Ctx => _context ?? throw new InvalidOperationException("AI Actions is switched off.");

    // ---- the menus ----

    public IEnumerable<PluginMenuItem> Items(MenuContext menu)
    {
        if (_context is null) return [];
        var items = menu.Menu == Menus.Terminal ? TerminalItems(menu) : WikiItems(menu);
        var custom = (_custom?.Load() ?? []).Where(a => a.Menus.Contains(menu.Menu)).ToList();
        if (custom.Count > 0)
        {
            items.Add(PluginMenuItem.Separator());
            items.AddRange(custom.Select(a => PluginMenuItem.Command(a.Name, () => RunCustomAsync(a, menu))));
        }
        return items.Count == 0 ? [] : [PluginMenuItem.Submenu("AI Actions", items, icon: "sparkles")];
    }

    private static bool IsMarkdown(string? path) => path is not null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    private List<PluginMenuItem> WikiItems(MenuContext menu)
    {
        var items = new List<PluginMenuItem>();
        var page = !menu.IsFolder && IsMarkdown(menu.WikiPath) ? menu.WikiPath : null;
        var writable = menu.Editor is { IsReadOnly: false };
        var selected = menu.SelectedText.Length > 0;
        if (page is not null)
        {
            items.Add(Item("Add Tags", "Suggest #tags for this page (preferring ones the wiki uses) and add the ones you keep", () => AddTagsAsync(menu, page), "tag"));
            items.Add(Item(selected ? "Summarize Selection" : "Summarize", "A short summary in a window, which can go on in AI Chat", () => SummarizeAsync(menu, page)));
        }
        if (writable && menu.Editor is { } editor)
        {
            items.Add(Item(selected ? "Format Selection" : "Format", "Tidy the markdown without changing what it says; shows the change before it is made", () => ChangeAsync(editor, null)));
            items.Add(Item(selected ? "Rewrite Selection…" : "Rewrite…", "Rewrite as you ask (shorter, friendlier, as a list …); shows the change before it is made", () => RewriteAsync(editor)));
        }
        items.Add(PluginMenuItem.Separator());
        if (page is not null) items.Add(Item("Ask About This Page…", "Open AI Chat, set to answer questions about this page (and the rest of the wiki)", () => AskAsync($"the page {page}", Path.GetFileNameWithoutExtension(page)), "forum"));
        if (menu.IsFolder && menu.WikiPath is { } folder) items.Add(Item("Ask About This Folder…", "Open AI Chat, set to answer questions about the pages in this folder", () => AskAsync($"the folder {(folder.Length == 0 ? "(the whole wiki)" : folder)}", Path.GetFileName(folder)), "forum"));
        items.Add(Item("Do Research…", "Open AI Chat, set to find answers across the wiki's pages and name where they came from", () => AskAsync(null, "Research"), "magnify"));
        if (selected) items.Add(Item("Send Selection to a Terminal…", "Type the selected text into a terminal", () => SendToTerminalAsync(menu.SelectedText)));
        return items;
    }

    private List<PluginMenuItem> TerminalItems(MenuContext menu)
    {
        if (menu.SelectedText.Length == 0) return [Item("Do Research…", "Open AI Chat, set to find answers in the wiki", () => AskAsync(null, "Research"), "magnify")];
        return
        [
            Item("Explain Selection", "What the selected output means, in a window", () => ExplainAsync(menu.SelectedText)),
            Item("Ask in AI Chat…", "Open AI Chat with the selected output in the message box", () => Ctx.Ai.OpenChatAsync(new AiChatRequest { Title = "Terminal output", Draft = "```\n" + menu.SelectedText.TrimEnd() + "\n```\n\n" })),
            Item("Copy Selection into a Wiki Page…", "Add the selected text to the end of a wiki page", () => CopyToWikiAsync(menu.SelectedText)),
        ];
    }

    private static PluginMenuItem Item(string header, string tip, Func<Task> run, string? icon = null) => new() { Header = header, ToolTip = tip, OnClick = run, Icon = icon };

    // ---- helpers ----

    private async Task<string> PageTextAsync(MenuContext menu, string page) =>
        menu.Editor is { } e && e.Path == page ? e.Text : (await Ctx.CommandAsync("wiki read", new JsonObject { ["path"] = page }))?.GetValue<string>() ?? "";

    private async Task<List<string>> AllTagsAsync()
    {
        try { return (await Ctx.CommandAsync("wiki tags") as JsonArray ?? []).Select(t => t?["key"]?.GetValue<string>() ?? "").Where(t => t.Length > 0).ToList(); }
        catch { return []; }
    }

    [GeneratedRegex(@"(?<![\w&#/])#([\p{L}][\p{L}\p{N}_/-]*)")]
    private static partial Regex TagInText();

    private static string TagsIn(string text) => string.Join(" ", TagInText().Matches(text).Select(m => "#" + m.Groups[1].Value.ToLowerInvariant()).Distinct());

    private (IPluginWindow Window, T View) Show<T>(string title, T view, double width = 760, double height = 600) where T : Control => (Ctx.ShowWindow(title, view, width, height), view);

    private async Task CopyAsync(Control from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        Ctx.Toast("Copied");
    }

    private Task<AiRunResult> RunAsync(string prompt, Action<string>? onText = null, string? provider = null, string? model = null, string? effort = null, bool tools = false) =>
        Ctx.Ai.RunAsync(new AiRunRequest
        {
            Prompt = prompt, ProviderId = string.IsNullOrEmpty(provider) ? null : provider, Model = string.IsNullOrEmpty(model) ? null : model,
            Effort = string.IsNullOrEmpty(effort) ? null : effort, AllowTools = tools, WorkingDirectory = Ctx.WikiDirectory,
        }, onText is null ? null : e => { if (e is AiTextDelta d) onText(d.Text); });

    private bool NoProvider()
    {
        if (Ctx.Ai.Providers.Count > 0) return false;
        Ctx.Toast("No AI provider is set up", "Add one under Settings → AI Chat.");
        return true;
    }

    // ---- Add Tags ----

    private async Task AddTagsAsync(MenuContext menu, string page)
    {
        if (NoProvider()) return;
        var text = await PageTextAsync(menu, page);
        var existing = await AllTagsAsync();
        IPluginWindow? window = null;
        var view = new TagView(_look!, page, existing, chosen => { _ = ApplyTagsAsync(menu, page, chosen); window?.Close(); }, () => window?.Close());
        (window, _) = Show("Add Tags — " + Path.GetFileNameWithoutExtension(page), view, 620, 420);
        var result = await RunAsync(Prompts.AddTags(page, text, existing));
        var have = TagsIn(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#')).ToHashSet();
        view.Show(Prompts.ParseTags(result.Text).Where(t => !have.Contains(t)).ToList(), result.Ok ? null : result.Error);
    }

    private async Task ApplyTagsAsync(MenuContext menu, string page, List<string> tags)
    {
        if (tags.Count == 0) return;
        try
        {
            if (menu.Editor is { IsReadOnly: false } editor && editor.Path == page)
            {
                var updated = (await Ctx.CommandAsync("wiki tags add", new JsonObject { ["page"] = page, ["tags"] = string.Join(' ', tags), ["text"] = editor.Text }))?.GetValue<string>();
                if (updated is not null) Dispatcher.UIThread.Post(() => editor.ReplaceText(updated));
            }
            else await Ctx.CommandAsync("wiki tags add", new JsonObject { ["page"] = page, ["tags"] = string.Join(' ', tags) });
            Ctx.Toast("Tags added", string.Join(" ", tags.Select(t => "#" + t)));
        }
        catch (Exception ex) { Ctx.Toast("Could not add the tags", ex.Message); }
    }

    // ---- Summarize, Explain ----

    private async Task SummarizeAsync(MenuContext menu, string page)
    {
        if (NoProvider()) return;
        var selection = menu.SelectedText.Length > 0;
        var source = selection ? menu.SelectedText : await PageTextAsync(menu, page);
        await ResultAsync("Summary — " + Path.GetFileNameWithoutExtension(page), selection ? "Selected text" : page, source, "Summary",
            Prompts.Summarize(page, source, selection), menu.Editor, $"Summary of {Path.GetFileNameWithoutExtension(page)}");
    }

    private Task ExplainAsync(string text) =>
        NoProvider() ? Task.CompletedTask : ResultAsync("Explain", "Terminal output", text, "Explanation", Prompts.Explain(text), null, "Terminal output explained");

    /// <summary>Runs a prompt into a window: the source above, the reply streaming in below, then Copy, Insert Below, Speak and Open in AI Chat.</summary>
    private async Task ResultAsync(string title, string sourceTitle, string source, string resultTitle, string prompt, IWikiEditor? editor, string chatTitle,
        string? provider = null, string? model = null, string? effort = null, bool tools = false)
    {
        var view = new ResultView(_look!, sourceTitle, source, resultTitle);
        var (window, _) = Show(title, view);
        var result = await RunAsync(prompt, view.Append, provider, model, effort, tools);
        var text = result.Text.Length > 0 ? result.Text : view.Text;
        var buttons = new List<Control>
        {
            _look!.Button("Copy", () => _ = CopyAsync(view, text)),
        };
        if (editor is { IsReadOnly: false }) buttons.Add(_look.Button("Insert Below", () => { editor.InsertAfterSelection("\n" + text.Trim() + "\n"); window.Close(); }, tip: "Put it in the page, under the selection"));
        buttons.Add(_look.Button("Speak", () => _ = Ctx.SpeakAsync(text), tip: "Read it aloud (Kokoro)"));
        buttons.Add(_look.Button("Open in AI Chat", () =>
        {
            _ = Ctx.Ai.OpenChatAsync(new AiChatRequest
            {
                Title = chatTitle, ProviderId = result.ProviderId, Model = result.Model, Effort = result.Effort, WorkingDirectory = result.WorkingDirectory,
                History = [("user", prompt), ("assistant", text)], Resume = result.ProviderSessionId,
            });
            window.Close();
        }, accent: true, tip: "Carry on the conversation in AI Chat"));
        buttons.Add(_look.Button("Close", window.Close));
        view.Done(text, result.Ok ? null : result.Error ?? "No reply.", buttons);
    }

    // ---- Format, Rewrite ----

    private async Task RewriteAsync(IWikiEditor editor)
    {
        var instruction = await Ctx.PromptAsync("Rewrite", "How should it be rewritten? (shorter, friendlier, as a list, in French …)");
        if (string.IsNullOrWhiteSpace(instruction)) return;
        await ChangeAsync(editor, instruction.Trim());
    }

    /// <summary>Formats (no instruction) or rewrites the selection or page, showing the change to accept or reject.</summary>
    private async Task ChangeAsync(IWikiEditor editor, string? instruction, string? customPrompt = null, CustomAction? action = null)
    {
        if (NoProvider()) return;
        var selection = editor.SelectedText;
        var whole = selection.Length == 0;
        var before = whole ? editor.Text : selection;
        IPluginWindow? window = null;
        string? after = null;
        var view = new DiffView(_look!, instruction is null && customPrompt is null ? "Format" : "Rewrite" + (instruction is null ? "" : ": " + instruction),
            accept: () =>
            {
                if (after is null) return;
                if (whole) editor.ReplaceText(after);
                else if (editor.SelectedText == selection) editor.ReplaceSelection(after.TrimEnd('\n'));
                else if (editor.Text.Contains(selection)) { var i = editor.Text.IndexOf(selection, StringComparison.Ordinal); editor.ReplaceText(editor.Text[..i] + after.TrimEnd('\n') + editor.Text[(i + selection.Length)..]); }
                else Ctx.Toast("The text changed meanwhile", "Nothing was replaced.");
                window?.Close();
            },
            reject: () => window?.Close());
        (window, _) = Show(whole ? "Change the page" : "Change the selection", view, 900, 640);
        var prompt = customPrompt ?? (instruction is null ? Prompts.Format(before) : Prompts.Rewrite(before, instruction));
        var result = await RunAsync(prompt, null, action?.Provider, action?.Model, action?.Effort, action?.Tools == true);
        if (result.Ok && result.Text.Length > 0) after = Prompts.Unwrap(result.Text);
        view.Show(before, after ?? before, result.Ok ? null : result.Error ?? "No reply.");
    }

    // ---- AI Chat ----

    private Task AskAsync(string? about, string title)
    {
        if (NoProvider()) return Task.CompletedTask;
        return Ctx.Ai.OpenChatAsync(new AiChatRequest
        {
            Title = about is null ? title : "About " + title, WorkingDirectory = Ctx.WikiDirectory, SystemPrompt = Prompts.Research(Ctx.WikiDirectory, about),
        });
    }

    // ---- moving text ----

    private async Task SendToTerminalAsync(string text)
    {
        var list = await Ctx.CommandAsync("terminal list") as JsonArray ?? [];
        var terminals = list.OfType<JsonObject>().Select(t => (Id: t["id"]?.ToString() ?? "", Title: t["title"]?.ToString() ?? t["name"]?.ToString() ?? "")).ToList();
        if (terminals.Count == 0) { Ctx.Toast("No terminal is open"); return; }
        var choice = await Ctx.PromptAsync("Send to a terminal", "Terminal id: " + string.Join(", ", terminals.Select(t => $"{t.Id} ({t.Title})")), terminals[0].Id);
        if (string.IsNullOrWhiteSpace(choice)) return;
        try
        {
            await Ctx.CommandAsync("terminal send", new JsonObject { ["id"] = choice.Trim(), ["text"] = text, ["paste"] = true });
            Ctx.Toast("Sent to the terminal");
        }
        catch (Exception ex) { Ctx.Toast("Could not send it", ex.Message); }
    }

    private async Task CopyToWikiAsync(string text)
    {
        var page = await Ctx.PromptAsync("Copy into a wiki page", "Page (the text is added at the end; a new page is made if there is none)", "Terminal notes");
        if (string.IsNullOrWhiteSpace(page)) return;
        try
        {
            var written = await Ctx.CommandAsync("wiki write", new JsonObject { ["path"] = page.Trim(), ["content"] = "```\n" + text.TrimEnd() + "\n```", ["append"] = true });
            Ctx.Toast("Added to the wiki", written?.ToString() ?? page);
        }
        catch (Exception ex) { Ctx.Toast("Could not add it", ex.Message); }
    }

    // ---- custom actions ----

    private async Task RunCustomAsync(CustomAction action, MenuContext menu)
    {
        if (NoProvider()) return;
        var page = !menu.IsFolder && IsMarkdown(menu.WikiPath) ? menu.WikiPath : null;
        var pageText = page is null ? "" : await PageTextAsync(menu, page);
        var values = new Dictionary<string, string>
        {
            ["selection"] = menu.SelectedText.Length > 0 ? menu.SelectedText : pageText,
            ["page"] = pageText, ["pagePath"] = page ?? menu.WikiPath ?? "", ["tags"] = TagsIn(pageText),
        };
        if (action.Prompt.Contains("{{allTags}}")) values["allTags"] = string.Join(" ", (await AllTagsAsync()).Select(t => "#" + t));
        if (action.Prompt.Contains("{{input}}"))
        {
            var input = await Ctx.PromptAsync(action.Name, "What should it use?");
            if (input is null) return;
            values["input"] = input;
        }
        var prompt = Prompts.Fill(action.Prompt, values);
        switch (action.Result)
        {
            case "replace" when menu.Editor is { IsReadOnly: false } editor:
                await ChangeAsync(editor, null, prompt, action);
                break;
            case "below" when menu.Editor is { IsReadOnly: false } editor:
                var r = await RunAsync(prompt, null, action.Provider, action.Model, action.Effort, action.Tools);
                if (r.Ok) editor.InsertAfterSelection(r.Text.Trim()); else Ctx.Toast(action.Name + " failed", r.Error ?? "");
                break;
            case "chat":
                await Ctx.Ai.OpenChatAsync(new AiChatRequest
                {
                    Title = action.Name, ProviderId = action.Provider.Length > 0 ? action.Provider : null, Model = action.Model.Length > 0 ? action.Model : null,
                    Effort = action.Effort.Length > 0 ? action.Effort : null, WorkingDirectory = Ctx.WikiDirectory, Send = prompt,
                });
                break;
            case "page":
                var name = await Ctx.PromptAsync(action.Name, "Name of the new page", $"{action.Name} {DateTime.Now:yyyy-MM-dd HHmm}");
                if (string.IsNullOrWhiteSpace(name)) return;
                var res = await RunAsync(prompt, null, action.Provider, action.Model, action.Effort, action.Tools);
                if (!res.Ok) { Ctx.Toast(action.Name + " failed", res.Error ?? ""); return; }
                var written = await Ctx.CommandAsync("wiki write", new JsonObject { ["path"] = name.Trim(), ["content"] = res.Text.Trim() + "\n" });
                await Ctx.CommandAsync("wiki open", new JsonObject { ["path"] = written?.ToString() ?? name });
                break;
            default:
                await ResultAsync(action.Name, page ?? "Selection", values["selection"], "Reply", prompt, menu.Editor, action.Name, action.Provider, action.Model, action.Effort, action.Tools);
                break;
        }
    }
}
