using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Avamux.Views;

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
                    ("Providers", "Start, stop and watch instances that serve models: llama.cpp, or anything with a start and stop script."),
                    ("Work queues", "Queues of jobs handed to providers, such as a work queue on another machine."),
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
