using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Avamux.Plugins.AiActions;

/// <summary>An action of the user's own: a prompt with placeholders, where it shows, and what happens to the reply.</summary>
public sealed class CustomAction
{
    public string Name { get; set; } = "";
    /// <summary>Which menus it appears in (<see cref="Menus"/>).</summary>
    public List<string> Menus { get; set; } = [Plugins.Menus.WikiEditor, Plugins.Menus.WikiPreview];
    /// <summary>The prompt. Placeholders: {{selection}} {{page}} {{pagePath}} {{tags}} {{allTags}} {{input}} (asks first).</summary>
    public string Prompt { get; set; } = "";
    /// <summary>window, replace (shows the change first), below (inserts under the selection), chat, page (a new wiki page).</summary>
    public string Result { get; set; } = "window";
    /// <summary>A provider's id; empty uses the AI Actions default.</summary>
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    /// <summary>Let the agent use its tools (read other pages, run commands).</summary>
    public bool Tools { get; set; }

    public static readonly string[] Results = ["window", "replace", "below", "chat", "page"];
}

/// <summary>The custom actions, kept as JSON in the plugin's data folder (actions.json).</summary>
public sealed class CustomActionStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public string File => Path.Combine(folder, "actions.json");
    public event Action? Changed;

    public List<CustomAction> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File))
            {
                // A first example to copy from.
                Save([new CustomAction
                {
                    Name = "List the To-dos", Menus = [Plugins.Menus.WikiEditor, Plugins.Menus.WikiPreview, Plugins.Menus.WikiTree],
                    Prompt = "List every to-do, open question and promised follow-up in this page as a markdown checklist, most urgent first. If there are none, say so.\n\n{{page}}",
                }], raise: false);
            }
            return JsonSerializer.Deserialize<List<CustomAction>>(System.IO.File.ReadAllText(File), Json) ?? [];
        }
        catch { return []; }
    }

    public void Save(List<CustomAction> actions, bool raise = true)
    {
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(actions, Json));
        if (raise) Changed?.Invoke();
    }
}

/// <summary>The plugin's panel in Settings → Plugins: the custom actions, to add, change and delete.</summary>
internal sealed class CustomActionsView : UserControl
{
    private readonly IPluginContext _context;
    private readonly CustomActionStore _store;
    private readonly Look _look;
    private readonly StackPanel _list = new() { Spacing = 4 };

    public CustomActionsView(IPluginContext context, CustomActionStore store, Look look)
    {
        _context = context; _store = store; _look = look;
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                look.Text("Your own actions", 13, bold: true),
                look.Text("They appear under AI Actions in the menus you choose. In the prompt, {{selection}} is the selected text, {{page}} the page, {{pagePath}} its path, " +
                          "{{tags}} its tags, {{allTags}} every tag in the wiki and {{input}} something asked for when the action runs. " +
                          $"The defaults for every action are under Settings → AI Chat (AI Actions provider, model and effort). Kept in {store.File}.", 11.5, "dim"),
                _list,
                look.Row(look.Button("Add an action…", () => Edit(null))),
            },
        };
        store.Changed += Build;
        DetachedFromVisualTree += (_, _) => store.Changed -= Build;
        Build();
    }

    private void Build()
    {
        _list.Children.Clear();
        var actions = _store.Load();
        if (actions.Count == 0) { _list.Children.Add(_look.Text("None yet.", 12, "dim")); return; }
        for (var i = 0; i < actions.Count; i++)
        {
            var index = i;
            var a = actions[i];
            _list.Children.Add(_look.Box(new DockPanel
            {
                Children =
                {
                    _look.Row(_look.Button("Edit", () => Edit(index)), _look.Button("Delete", () => { var all = _store.Load(); all.RemoveAt(index); _store.Save(all); })).With(r => DockPanel.SetDock(r, Dock.Right)),
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            _look.Text(a.Name, 12.5, bold: true),
                            _look.Text($"{string.Join(", ", a.Menus)} → {a.Result}{(a.Provider.Length > 0 ? " · " + a.Provider : "")}{(a.Model.Length > 0 ? " · " + a.Model : "")}", 11, "dim"),
                        },
                    },
                },
            }));
        }
    }

    private void Edit(int? index)
    {
        var all = _store.Load();
        var a = index is { } i ? all[i] : new CustomAction();
        var name = new TextBox { Text = a.Name, PlaceholderText = "Shown in the menu" };
        var prompt = new TextBox { Text = a.Prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, PlaceholderText = "Translate this into French, keeping the markdown:\n\n{{selection}}" };
        var menus = Plugins.Menus.All.Select(m => new CheckBox { Content = m, IsChecked = a.Menus.Contains(m), Margin = new Thickness(0, 0, 12, 0) }).ToList();
        var result = new ComboBox { ItemsSource = CustomAction.Results, SelectedItem = a.Result, MinWidth = 140 };
        var provider = new ComboBox { MinWidth = 180 };
        provider.Items.Add(new ComboBoxItem { Content = "AI Actions default", Tag = "" });
        foreach (var p in _context.Ai.Providers) provider.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        provider.SelectedItem = provider.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag! == a.Provider) ?? provider.Items[0];
        var model = new TextBox { Text = a.Model, PlaceholderText = "Empty: the default", MinWidth = 160 };
        var effort = new TextBox { Text = a.Effort, PlaceholderText = "Empty: the default", MinWidth = 120 };
        var tools = new CheckBox { Content = "Let the agent use its tools (read other pages, run commands)", IsChecked = a.Tools };
        IPluginWindow? window = null;
        var form = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    _look.Text("Name", 12, bold: true), name,
                    _look.Text("Shows in", 12, bold: true), new WrapPanel { Children = { menus[0], menus[1], menus[2], menus[3] } },
                    _look.Text("Prompt", 12, bold: true), prompt,
                    _look.Text("The reply: window (shown in a window), replace (the selection or page, after showing the change), below (under the selection), chat (opens AI Chat), page (a new wiki page)", 11.5, "dim"),
                    _look.Row(result, provider, model, effort),
                    tools,
                    _look.Row(_look.Button("Save", () =>
                    {
                        a.Name = (name.Text ?? "").Trim();
                        if (a.Name.Length == 0 || (prompt.Text ?? "").Trim().Length == 0) { _context.Toast("Give it a name and a prompt"); return; }
                        a.Prompt = prompt.Text ?? "";
                        a.Menus = menus.Where(m => m.IsChecked == true).Select(m => (string)m.Content!).ToList();
                        a.Result = result.SelectedItem as string ?? "window";
                        a.Provider = (provider.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                        a.Model = (model.Text ?? "").Trim();
                        a.Effort = (effort.Text ?? "").Trim();
                        a.Tools = tools.IsChecked == true;
                        if (index is null) all.Add(a);
                        _store.Save(all);
                        window?.Close();
                    }, accent: true), _look.Button("Cancel", () => window?.Close())),
                },
            },
        };
        window = _context.ShowWindow(index is null ? "Add an AI action" : "Edit " + a.Name, form, 720, 620);
    }
}
