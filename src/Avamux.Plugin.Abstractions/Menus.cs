namespace Avamux.Plugins;

/// <summary>The right-click menus plugins can add to (<see cref="MenuContext.Menu"/>).</summary>
public static class Menus
{
    /// <summary>The wiki's text editor.</summary>
    public const string WikiEditor = "wiki.editor";
    /// <summary>The wiki's rendered preview.</summary>
    public const string WikiPreview = "wiki.preview";
    /// <summary>A page, file or folder in the wiki's folder tree (or the Tags view).</summary>
    public const string WikiTree = "wiki.tree";
    /// <summary>Inside a terminal.</summary>
    public const string Terminal = "terminal";

    public static readonly IReadOnlyList<string> All = [WikiEditor, WikiPreview, WikiTree, Terminal];
}

/// <summary>What a right-click menu is about. Asked for every time a menu opens, so items can follow the selection.</summary>
public sealed class MenuContext
{
    /// <summary>Which menu: one of <see cref="Menus"/>.</summary>
    public required string Menu { get; init; }
    /// <summary>The wiki page, file or folder the menu is about (wiki-relative, "/" separated), if any.</summary>
    public string? WikiPath { get; init; }
    public bool IsFolder { get; init; }
    /// <summary>The selected text (editor, preview or terminal); empty when nothing is selected.</summary>
    public string SelectedText { get; init; } = "";
    /// <summary>Terminal menus: the terminal (pane) the menu was opened in.</summary>
    public string? TerminalId { get; init; }
    /// <summary>Wiki editor and preview menus: the page as it is open, unsaved edits included. Null elsewhere.</summary>
    public IWikiEditor? Editor { get; init; }
}

/// <summary>The page open in the wiki's editor. Call from the UI thread.</summary>
public interface IWikiEditor
{
    /// <summary>The page's wiki path.</summary>
    string Path { get; }
    /// <summary>The whole text, unsaved edits included.</summary>
    string Text { get; }
    string SelectedText { get; }
    /// <summary>True for files that cannot be edited (pictures, big files) and while the page is read only.</summary>
    bool IsReadOnly { get; }
    /// <summary>Replaces the selection (inserts at the caret when nothing is selected) as one undo step.</summary>
    void ReplaceSelection(string text);
    /// <summary>Replaces the whole page as one undo step, keeping the scroll position.</summary>
    void ReplaceText(string text);
    /// <summary>Inserts text on a new line after the selection (or the caret's line) as one undo step.</summary>
    void InsertAfterSelection(string text);
    /// <summary>Saves the page now.</summary>
    void Save();
}

/// <summary>An item a plugin adds to a menu: a command, a submenu (<see cref="Children"/>) or a separator.</summary>
public sealed class PluginMenuItem
{
    public string Header { get; set; } = "";
    /// <summary>An icon by name (as in Settings → Custom pages: "robot", "tag", "sparkles" …), or null.</summary>
    public string? Icon { get; set; }
    public string? ToolTip { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsSeparator { get; init; }
    /// <summary>Runs on the UI thread when the item is chosen. Exceptions are shown to the user and logged.</summary>
    public Func<Task>? OnClick { get; set; }
    /// <summary>A submenu.</summary>
    public List<PluginMenuItem> Children { get; set; } = [];

    public static PluginMenuItem Separator() => new() { IsSeparator = true };
    public static PluginMenuItem Command(string header, Action onClick, string? icon = null) =>
        new() { Header = header, Icon = icon, OnClick = () => { onClick(); return Task.CompletedTask; } };
    public static PluginMenuItem Command(string header, Func<Task> onClick, string? icon = null) => new() { Header = header, Icon = icon, OnClick = onClick };
    public static PluginMenuItem Submenu(string header, IEnumerable<PluginMenuItem> children, string? icon = null) => new() { Header = header, Icon = icon, Children = [.. children] };
}

/// <summary>Adds items to right-click menus. Return nothing for menus the plugin has nothing for.</summary>
public interface IMenuContributor
{
    IEnumerable<PluginMenuItem> Items(MenuContext context);
}
