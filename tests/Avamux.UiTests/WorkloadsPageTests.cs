using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avamux.Core;
using Avamux.Plugins;
using Avamux.Views;

namespace Avamux.UiTests;

/// <summary>The AI Workloads page over a stand-in queue source (a plugin's, as another machine's would be).</summary>
public sealed class WorkloadsPageTests
{
    private sealed class FakeQueue : IWorkloadSource
    {
        public List<(string Id, string Action)> Done { get; } = [];
        public string Id => "queue";
        public string Name => "Fake queue";
        public string Machine => "Other machine";
        public event Action? Changed { add { } remove { } }

        public Task<WorkloadSnapshot> ListAsync(CancellationToken ct = default) => Task.FromResult(new WorkloadSnapshot(
        [
            new WorkloadInfo
            {
                Id = "job-1", Kind = WorkloadKind.Job, Name = "Summarise the news", State = WorkloadState.Running, Detail = "Writing (1,234 tokens)", Model = "my-model",
                Started = DateTime.Now.AddMinutes(-3), Progress = 0.4, Actions = [new(WorkloadActions.Cancel, "Cancel")],
            },
            new WorkloadInfo { Id = "job-0", Kind = WorkloadKind.Job, Name = "Read receipts", State = WorkloadState.Failed, Error = "The model ran out of memory.", Finished = DateTime.Now.AddMinutes(-1) },
            new WorkloadInfo { Id = "model", Kind = WorkloadKind.Server, Name = "A model on the other machine", State = WorkloadState.Running, VramMiB = 6144, Actions = [new(WorkloadActions.Unload, "Unload", "Unload it?")] },
        ], Problem: "Part of it could not be read."));

        public Task<string> ActAsync(string workloadId, string action, CancellationToken ct = default) { lock (Done) Done.Add((workloadId, action)); return Task.FromResult("done"); }
        public Task<string> LogAsync(string workloadId, int lines = 200, CancellationToken ct = default) => Task.FromResult("line one\nline two");
    }

    private static void Settle(Window w, int ms = 0)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Thread.Sleep(20); } while (watch.ElapsedMilliseconds < ms);
    }

    private static IEnumerable<string> Texts(Control c) => c.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0);

    [AvaloniaFact]
    public void Jobs_from_a_plugin_source_show_and_can_be_cancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), "avamux-workloads-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "home"));
        var services = new AppServices(new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "home"), root));
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        try
        {
            services.Workloads.SetScriptFolders([]);
            var queue = new FakeQueue();
            services.Workloads.AddSource(queue);
            var page = new WorkloadsPage();
            var window = new Window { Width = 1200, Height = 800, Content = page };
            window.Show();
            page.OpenPart("tasks");
            for (var i = 0; i < 100 && !Texts(page).Contains("Summarise the news"); i++) Settle(window, 50);
            var texts = Texts(page).ToList();
            Assert.Contains("Summarise the news", texts);
            Assert.Contains("Read receipts", texts);
            Assert.Contains("The model ran out of memory.", texts);
            Assert.Contains(texts, t => t.StartsWith("Fake queue: Part of it could not be read.", StringComparison.Ordinal));
            Assert.DoesNotContain("A model on the other machine", texts); // a server: on Running, not here

            var cancel = page.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Cancel");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && queue.Done.Count == 0; i++) Settle(window, 50);
            Assert.Equal([("job-1", WorkloadActions.Cancel)], queue.Done);

            page.OpenPart("running:queue:model");
            for (var i = 0; i < 100 && !Texts(page).Contains("A model on the other machine"); i++) Settle(window, 50);
            Assert.Contains("Other machine", Texts(page));
            for (var i = 0; i < 100 && !page.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "line one\nline two"); i++) Settle(window, 50);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "line one\nline two");
            window.Close();
        }
        finally
        {
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
