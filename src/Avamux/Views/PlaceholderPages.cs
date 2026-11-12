using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Avamux.Views;

/// <summary>Placeholder for the AI Chat page. Shown when "AI Chat" is switched on under Settings → AI Workloads.</summary>
public sealed class ChatPage : UserControl
{
    public ChatPage()
    {
        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("AI Chat", "Talk to a model running in one of your workloads.").Dock(Dock.Top),
                PlaceholderPages.Body(Icons.Forum, "AI Chat is not built yet",
                    "This page is a placeholder. It will hold conversations with models served by the providers you manage under Workloads.",
                    ("Conversations", "A list of chats, each tied to a provider and model."),
                    ("Model picker", "Choose among the models your running providers offer."),
                    ("Context", "Attach wiki pages, terminal output and repository changes to a message.")),
            },
        };
    }
}

/// <summary>Placeholder for the Workloads page. Shown when "Workloads" is switched on under Settings → AI Workloads.</summary>
public sealed class WorkloadsPage : UserControl
{
    public WorkloadsPage()
    {
        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Workloads", "Running providers, work queues and models on disk.").Dock(Dock.Top),
                PlaceholderPages.Body(Icons.Server, "Workloads is not built yet",
                    "This page is a placeholder. It will manage the things that run models and the work waiting for them.",
                    ("Providers", "Start, stop and watch instances that serve models: llama.cpp, Strata, or anything with a start and stop script."),
                    ("Work queues", "Queues of jobs handed to providers, such as an HTPC work queue or a terminal-web-admin work queue."),
                    ("Models on disk", "The model files you have, their size and which provider can load them.")),
            },
        };
    }
}

internal static class PlaceholderPages
{
    public static Control Body(Geometry icon, string title, string text, params (string Title, string Text)[] planned)
    {
        var cards = new StackPanel { Spacing = 10 };
        foreach (var (t, d) in planned)
            cards.Children.Add(Ui.Card(Ui.Column(3, Ui.Text(t, "h2"), Ui.Text(d, "dim", 12.5))));
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20, 8, 20, 28), Spacing = 14, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    Ui.Row(12, new PathIcon { Data = icon, Width = 30, Height = 30, Foreground = Ui.Tk("faint") },
                        Ui.Column(2, Ui.Text(title, "h2"), Ui.Text(text, "dim", 12.5))),
                    Ui.Apply(Ui.Text("Planned", "dim", 11.5), t => t.Margin = new Thickness(0, 6, 0, 0)),
                    cards,
                },
            },
        };
    }
}
