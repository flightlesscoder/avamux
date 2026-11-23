using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avamux.Core.Ai;
using Avamux.Plugins;
using Avamux.Views;

namespace Avamux.UiTests;

/// <summary>Settings → AI Chat: the provider, model and effort settings are dropdowns of what is set up, not ids to type.</summary>
public sealed class SettingsAiTests
{
    private static ComboBox Row(Control page, string label) =>
        page.GetVisualDescendants().OfType<Grid>()
            .First(g => g.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()?.Text == label)
            .Children.OfType<ComboBox>().Single();

    private static List<string> Labels(ComboBox combo) => combo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!).ToList();
    private static string? Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content as string;

    [AvaloniaFact]
    public void Providers_are_listed_by_name_and_saved_by_id()
    {
        using var h = new ChatHarness(c => c.Services.Ai.Put(new AiProviderConfig { Id = "local", Kind = PiProvider.KindId, Name = "Local model", Command = ChatHarness.Shared("fake-pi.py"), Enabled = false }));
        var page = new SettingsPage();
        h.Window.Content = page;
        h.Settle(minMs: 300);

        var actions = Row(page, "AI Actions provider");
        Assert.Equal(["The first one in the list", "Claude Code", "pi", "Local model  (switched off)"], Labels(actions));
        Assert.Equal("Claude Code", Selected(actions)); // the default, ai.actionsProvider = "claude-code"
        Assert.Equal("The first one in the list", Selected(Row(page, "Provider for new chats")));

        // The model list is Claude Code's, with Haiku chosen; effort is low.
        var model = Row(page, "AI Actions model");
        h.WaitFor(() => Labels(model).Contains("Sonnet"));
        Assert.Equal("Haiku", Selected(model));
        Assert.Equal("low", Selected(Row(page, "AI Actions effort")));

        // Picking pi saves its id. Haiku is not one of pi's models, so the model goes back to pi's own; low is a pi level and stays.
        actions.SelectedItem = actions.Items.OfType<ComboBoxItem>().First(i => (string)i.Content! == "pi");
        h.WaitFor(() => h.Services.Config.Settings.Ai.ActionsModel == "");
        Assert.Equal("pi", h.Services.Config.Settings.Ai.ActionsProvider);
        Assert.Equal("low", h.Services.Config.Settings.Ai.ActionsEffort);
        h.WaitFor(() => Selected(Row(page, "AI Actions model")) == "The provider's own");
        Assert.Contains("minimal", Labels(Row(page, "AI Actions effort"))); // pi's levels now

        // A provider added later shows up; one that is gone is kept and marked.
        h.Services.Ai.Put(new AiProviderConfig { Id = "work", Kind = ClaudeCodeProvider.KindId, Name = "Work Claude", Command = ChatHarness.Shared("fake-claude.py") });
        h.WaitFor(() => Labels(Row(page, "Provider for new chats")).Contains("Work Claude"));
        h.Services.Config.Set("ai.chatProvider", System.Text.Json.Nodes.JsonValue.Create("gone"), Avamux.Core.Config.ConfigScope.Home);
        h.WaitFor(() => Selected(Row(page, "Provider for new chats")) == "gone  (not set up)");
    }

    [AvaloniaFact]
    public void A_typed_model_name_is_kept()
    {
        using var h = new ChatHarness();
        var page = new SettingsPage();
        h.Window.Content = page;
        h.Settle(minMs: 300);
        var model = Row(page, "AI Actions model");
        Assert.True(model.IsEditable);
        model.Focus();
        model.Text = "claude-haiku-5-5";
        h.Window.Focus(); // leaving the box saves it
        Row(page, "AI Actions effort").Focus();
        h.WaitFor(() => h.Services.Config.Settings.Ai.ActionsModel == "claude-haiku-5-5");
        h.WaitFor(() => Selected(Row(page, "AI Actions model")) == "claude-haiku-5-5");
    }
}
