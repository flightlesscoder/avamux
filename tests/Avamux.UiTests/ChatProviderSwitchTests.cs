using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Avamux.UiTests;

public sealed class ChatProviderSwitchTests
{
    private static ComboBox Provider(ChatHarness h) =>
        h.Page.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag as string == "pi"));

    /// <summary>The items the open dropdown actually shows (realized and laid out in the popup).</summary>
    private static List<string> Shown(ComboBox combo) =>
        combo.GetLogicalDescendants().OfType<ComboBoxItem>().Concat(combo.Items.OfType<ComboBoxItem>())
            .Distinct().Where(i => i.IsVisible && TopLevel.GetTopLevel(i) is not null && i.Bounds.Height > 0).Select(i => (string)i.Content!).ToList();

    private static ComboBox Effort(ChatHarness h) =>
        h.Page.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Content as string == "Default effort"));

    private static void Pick(ChatHarness h, string name, Func<ChatHarness, ComboBox>? which = null)
    {
        var combo = (which ?? Provider)(h);
        combo.IsDropDownOpen = true;
        h.Settle(minMs: 100);
        Assert.Contains(name, Shown(combo));
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (string)i.Content! == name);
        combo.IsDropDownOpen = false;
        h.Settle(minMs: 200);
    }

    [AvaloniaFact]
    public void Switching_back_and_forth_before_a_chat_starts()
    {
        using var h = new ChatHarness();
        Pick(h, "pi");
        Pick(h, "Claude Code");
        Pick(h, "pi");
    }

    private static string Empty(ChatHarness h) => h.Texts().First(t => t.StartsWith("Type a message below"));

    [AvaloniaFact]
    public void The_text_before_the_first_message_follows_the_choices()
    {
        using var h = new ChatHarness();
        Assert.Contains("with Claude Code.", Empty(h));
        Assert.Contains("It works in " + h.Services.Ai.DefaultFolder, Empty(h));
        Pick(h, "pi");
        h.WaitFor(() => Empty(h).Contains("with pi."));
        Pick(h, "Claude Code");
        h.WaitFor(() => Empty(h).Contains("with Claude Code."));
        Pick(h, "Haiku", hh => hh.Page.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Content as string == "Haiku")));
        h.WaitFor(() => Empty(h).Contains("with Claude Code (Haiku)."));
        var folder = Path.Combine(h.Root, "elsewhere");
        Directory.CreateDirectory(folder);
        h.Page.ConfigureAsync(folder: folder).GetAwaiter().GetResult();
        h.Settle();
        Assert.Contains("It works in " + folder, Empty(h));
        Assert.Contains("with Claude Code (Haiku).", Empty(h));
    }

    [AvaloniaFact]
    public void Switching_back_and_forth_in_a_chat()
    {
        using var h = new ChatHarness();
        h.Send("hello");
        var chats = h.Services.Ai.Chats;
        h.WaitFor(() => chats.List().Count == 1 && !chats.IsBusy(chats.List()[0].Id));
        Pick(h, "pi");
        h.WaitFor(() => chats.List()[0].ProviderId == "pi");
        Pick(h, "Claude Code");
        h.WaitFor(() => chats.List()[0].ProviderId == "claude-code");
        Pick(h, "pi");
        h.WaitFor(() => chats.List()[0].ProviderId == "pi");
        // pi's thinking levels, picked twice over, keep working too.
        Pick(h, "minimal", Effort);
        h.WaitFor(() => chats.List()[0].Effort == "minimal");
        Pick(h, "high", Effort);
        h.WaitFor(() => chats.List()[0].Effort == "high");
        Pick(h, "Claude Code");
    }
}
