using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Core.Ai;
using Avamux.Plugins;

namespace Avamux.Views;

/// <summary>
/// Settings → AI Chat: the providers AI Chat and AI Actions use (Claude Code, pi, other command lines, kinds from
/// plugins), each with its program, model, effort and permission settings. Nothing is restricted unless set here.
/// </summary>
public sealed class AiProvidersCard : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 4 };
    private static AiService Ai => App.Services.Ai;

    public AiProvidersCard()
    {
        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Text("Providers", "h3"),
                Ui.Text("The coding agents AI Chat and AI Actions talk to. Each runs with the account and settings it already has (claude, pi). " +
                        "Permission limits are off unless you set them: Claude Code then asks before editing or running anything, and its questions and requests show in the chat. " +
                        $"Kept in {App.Services.Paths.StateFile("ai-providers")}; `avamux ai providers` lists them.", "dim", 12.5),
                _list,
                Ui.Apply(Ui.IconButton(Icons.Plus, "Add a provider", () => _ = EditAsync(null), "Add a provider"), b => b.HorizontalAlignment = HorizontalAlignment.Left),
            },
        };
        var refresh = Ui.Debounced(Build);
        Ai.Changed += refresh;
        Ai.KindsChanged += refresh;
        DetachedFromVisualTree += (_, _) => { Ai.Changed -= refresh; Ai.KindsChanged -= refresh; };
        Build();
    }

    public bool Matches(string query) =>
        "ai chat provider providers claude code pi model effort permissions agent codex gemini opencode aider".Contains(query, StringComparison.OrdinalIgnoreCase)
        || Ai.Configs.Any(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string Permissions(AiProviderConfig c)
    {
        if (c.FullAccess) return "Full access: never asks";
        var parts = new List<string>();
        parts.Add(c.PermissionMode.Length > 0 ? "Permission mode " + c.PermissionMode : c.Kind == PiProvider.KindId ? (c.Option("confirmTools") is { Length: > 0 } t ? "Asks before " + t : "pi's own rules") : "Asks before edits and commands");
        if (c.AllowedTools.Count > 0) parts.Add("only " + string.Join(", ", c.AllowedTools));
        if (c.DisallowedTools.Count > 0) parts.Add("never " + string.Join(", ", c.DisallowedTools));
        if (c.ExtraFolders.Count > 0) parts.Add("also " + string.Join(", ", c.ExtraFolders));
        return string.Join("; ", parts);
    }

    private void Build()
    {
        _list.Children.Clear();
        var configs = Ai.Configs;
        if (configs.Count == 0) { _list.Children.Add(Ui.Text("No providers. Add one to use AI Chat and AI Actions.", "dim", 12.5)); return; }
        foreach (var c in configs)
        {
            var kind = Ai.Kind(c.Kind);
            var on = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, IsChecked = c.Enabled, VerticalAlignment = VerticalAlignment.Center };
            var id = c.Id;
            on.IsCheckedChanged += (_, _) => Ui.Try(this, () => { var x = Ai.GetConfig(id); x.Enabled = on.IsChecked == true; Ai.Put(x); });
            var title = Ui.Row(8, new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold }, Ui.Chip(kind?.DisplayName ?? c.Kind + " (missing)", Ui.Tk(kind is null ? "error" : "purple")));
            // The id is what the command line takes (avamux ai run --provider <id>).
            var detail = $"id {c.Id} · {(c.Command.Length > 0 ? c.Command : kind?.Kind switch { ClaudeCodeProvider.KindId => "claude", PiProvider.KindId => "pi", _ => "" })}" +
                         $"{(c.Model.Length > 0 ? " · model " + c.Model : "")}{(c.Effort.Length > 0 ? " · effort " + c.Effort : "")}";
            var text = Ui.Column(2, title,
                new TextBlock { Text = detail, Foreground = Ui.Dim, FontSize = 11.5, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = Permissions(c), Foreground = Ui.Tk(c.FullAccess ? "warn" : "faint"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Opacity = c.Enabled ? 1 : 0.55;
            var buttons = Ui.Row(0, on,
                Ui.IconButton(Icons.Play, "Test: send a short prompt and show the reply", () => _ = TestAsync(id)),
                Ui.IconButton(Icons.Pencil, "Edit", () => _ = EditAsync(Ai.GetConfig(id))),
                Ui.IconButton(Icons.Delete, "Remove", () => _ = RemoveAsync(id)));
            _list.Children.Add(new Border
            {
                Background = Ui.Tk("bgDeep"), CornerRadius = Ui.R(6), Padding = new Thickness(10, 6),
                Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { text, buttons.At(0, 1) } },
            });
        }
    }

    private async Task TestAsync(string id)
    {
        Ui.Toast(this, "Testing " + Ai.GetConfig(id).Name, "Sending a short prompt…");
        var r = await Ai.RunAsync(new AiRunRequest { Prompt = "Reply with one short sentence saying you are ready.", ProviderId = id, Effort = null });
        Ui.Toast(this, r.Ok ? "It answered" : "It did not answer", r.Ok ? r.Text : r.Error ?? "No reply.");
    }

    private async Task RemoveAsync(string id)
    {
        var c = Ai.GetConfig(id);
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Remove provider", $"Remove {c.Name}? Chats that use it stay, and can be moved to another provider.", "Remove")) return;
        Ui.Try(this, () => Ai.Remove(id));
    }

    private static TextBox Box(string text, string placeholder, bool multi = false, bool mono = false) => Ui.Apply(new TextBox
    {
        Text = text, PlaceholderText = placeholder, AcceptsReturn = multi, Height = multi ? 72 : double.NaN, TextWrapping = multi ? TextWrapping.NoWrap : TextWrapping.NoWrap,
    }, t => { if (mono) t.Classes.Add("mono"); });

    private static List<string> Lines(string? text, bool commas = true) =>
        (text ?? "").Split(commas ? ['\n', ','] : ['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async Task EditAsync(AiProviderConfig? existing)
    {
        var c = existing?.Clone() ?? new AiProviderConfig { Kind = ClaudeCodeProvider.KindId };
        var kinds = Ai.Kinds.ToList();
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = existing is null };
        foreach (var k in kinds) kind.Items.Add(new ComboBoxItem { Content = $"{k.DisplayName}: {k.Description}", Tag = k.Kind });
        kind.SelectedIndex = Math.Max(0, kinds.FindIndex(k => k.Kind == c.Kind));
        var name = Box(c.Name, "Shown in AI Chat");
        var command = Box(c.Command, "Empty: the usual program (claude, pi) from PATH", mono: true);
        var model = Box(c.Model, "Empty: the tool's own default (Claude Code: haiku, sonnet, opus; pi: provider/model)");
        var effort = Box(c.Effort, "Empty: the tool's own default (low, medium, high …)");
        var args = Box(string.Join("\n", c.Args), "Extra arguments, one per line", multi: true, mono: true);
        var env = Box(string.Join("\n", c.Env.Select(e => $"{e.Key}={e.Value}")), "KEY=value, one per line", multi: true, mono: true);
        var full = new CheckBox { Content = "Full access: never ask before editing files or running commands", IsChecked = c.FullAccess };
        var mode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var m in new[] { ("", "The tool's own default (asks before edits and commands)"), ("acceptEdits", "acceptEdits: edits without asking"), ("plan", "plan: plans, changes nothing"), ("default", "default") })
            mode.Items.Add(new ComboBoxItem { Content = m.Item2, Tag = m.Item1 });
        mode.SelectedItem = mode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == c.PermissionMode) ?? mode.Items[0];
        var allowed = Box(string.Join(", ", c.AllowedTools), "Empty: no limit. e.g. Read, Grep, Glob, Bash(git *)");
        var denied = Box(string.Join(", ", c.DisallowedTools), "Empty: none. e.g. Bash(rm *), WebFetch");
        var folders = Box(string.Join("\n", c.ExtraFolders), "Folders besides the working folder, one per line", multi: true);
        var confirm = Box(c.Option("confirmTools"), "Empty: never ask. e.g. bash, write, edit (or * for every tool)");
        var template = Box(c.Option("template"), "exec --json [--model {model}] {prompt}", mono: true);
        var output = new ComboBox { Items = { "text", "jsonl" }, SelectedItem = c.Option("output", "text") };
        var presets = new ComboBox { PlaceholderText = "Fill in from a preset…", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in CommandProvider.Presets) presets.Items.Add(p.Name);
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedIndex < 0) return;
            var p = CommandProvider.Presets[presets.SelectedIndex];
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = p.Name;
            command.Text = p.Command; template.Text = p.Template; output.SelectedItem = p.Output;
        };

        var claude = Ui.Column(12,
            Ui.Field("Permission mode", mode, "Claude Code's --permission-mode. Its questions and permission requests are answered in the chat."),
            Ui.Field("Only these tools", allowed, "--allowedTools: tools it may use without asking."),
            Ui.Field("Never these tools", denied, "--disallowedTools."),
            Ui.Field("Extra folders", folders, "--add-dir: folders it may use besides its working folder."));
        var piAllowed = Box(string.Join(", ", c.AllowedTools), "Empty: no limit. e.g. read, grep, find, ls");
        var piDenied = Box(string.Join(", ", c.DisallowedTools), "Empty: none. e.g. bash, write");
        var pi = Ui.Column(12,
            Ui.Field("Ask before these tools run", confirm, "Through avamux's pi extension, the question shows in the chat."),
            Ui.Field("Only these tools", piAllowed, "--tools."),
            Ui.Field("Never these tools", piDenied, "--exclude-tools."));
        var other = Ui.Column(12,
            Ui.Field("Preset", presets, "Best effort: these command lines are not tested by avamux."),
            Ui.Field("Arguments", template, "{prompt} is the message (with the conversation so far); [--model {model}] is left out when there is no model."),
            Ui.Field("Output", output, "text: the reply is what it prints. jsonl: one JSON object a line; the reply's text is picked out."));
        void ShowKind()
        {
            var k = (kind.SelectedItem as ComboBoxItem)?.Tag as string;
            claude.IsVisible = k == ClaudeCodeProvider.KindId;
            pi.IsVisible = k == PiProvider.KindId;
            other.IsVisible = k == CommandProvider.KindId;
            full.IsVisible = k != CommandProvider.KindId;
        }
        kind.SelectionChanged += (_, _) => ShowKind();
        ShowKind();

        var body = Ui.Column(14,
            Ui.Field("Kind", kind), Ui.Field("Name", name), Ui.Field("Program", command), Ui.Field("Model", model), Ui.Field("Effort", effort),
            Ui.Text("Permissions (off unless set)", "h3"), full, claude, pi, other,
            new Expander { Header = "More: arguments and environment", Content = Ui.Column(12, Ui.Field("Extra arguments", args), Ui.Field("Environment", env)) });
        await Ui.FormAsync(Ui.Owner(this), existing is null ? "Add a provider" : $"Edit {existing.Name}", new ScrollViewer { MaxHeight = 640, Content = body }, () =>
        {
            c.Kind = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? c.Kind;
            c.Name = (name.Text ?? "").Trim();
            if (c.Name.Length == 0) return Task.FromResult<string?>("Give it a name.");
            c.Command = (command.Text ?? "").Trim();
            c.Model = (model.Text ?? "").Trim();
            c.Effort = (effort.Text ?? "").Trim();
            c.Args = Lines(args.Text, commas: false);
            c.Env = Lines(env.Text, commas: false).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1]);
            c.FullAccess = full.IsChecked == true;
            c.PermissionMode = c.Kind == ClaudeCodeProvider.KindId ? (mode.SelectedItem as ComboBoxItem)?.Tag as string ?? "" : "";
            var isPi = c.Kind == PiProvider.KindId;
            c.AllowedTools = Lines(isPi ? piAllowed.Text : allowed.Text);
            c.DisallowedTools = Lines(isPi ? piDenied.Text : denied.Text);
            c.ExtraFolders = c.Kind == ClaudeCodeProvider.KindId ? Lines(folders.Text, commas: false) : [];
            if (c.Kind == PiProvider.KindId) c.Options["confirmTools"] = (confirm.Text ?? "").Trim(); else c.Options.Remove("confirmTools");
            if (c.Kind == CommandProvider.KindId) { c.Options["template"] = (template.Text ?? "").Trim(); c.Options["output"] = output.SelectedItem as string ?? "text"; }
            Ai.Put(c);
            return Task.FromResult<string?>(null);
        }, existing is null ? "Add" : "Save", width: 640);
    }
}
