using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avamux.Core;
using Avamux.Core.Wiki;
using Avamux.Views;
using AvaloniaEdit;

namespace Avamux.UiTests;

/// <summary>
/// The whole wiki page (tree, editor, toolbar, paste and drop handling) over a wiki in a temporary folder, with
/// the app's services pointed at that folder. The preview is a native web view, which the headless platform does
/// not draw; everything else is the real thing.
/// </summary>
public sealed class WikiHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "avamux-wiki-" + Guid.NewGuid().ToString("N"));
    public AppServices Services { get; }
    public WikiService Wiki => Services.Wiki;
    public Window Window { get; }
    public WikiPage Page { get; }

    /// <param name="prepare">Fills the wiki before the page opens.</param>
    /// <param name="open">Page to open, or null for the wiki's start page.</param>
    public WikiHarness(Action<WikiHarness>? prepare = null, string? open = null, double width = 1100, double height = 760)
    {
        ClipboardFiles.UseSystemTools = false; // never the clipboard of the session the tests run in
        Directory.CreateDirectory(Path.Combine(Root, "home"));
        var paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "home"), Root);
        Services = new AppServices(paths);
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, Services);
        prepare?.Invoke(this);
        Page = new WikiPage();
        Window = new Window { Width = width, Height = height, Content = Page };
        Window.Show();
        if (open is not null) Page.Open(open);
        Settle();
        Editor.TextArea.Focus();
    }

    public TextEditor Editor => Page.GetVisualDescendants().OfType<TextEditor>().First();
    public string Text => Editor.Text;

    public string Full(string wikiPath) => Path.Combine(Wiki.Root, wikiPath.Replace('/', Path.DirectorySeparatorChar));

    public void WritePicture(string wikiPath, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(wikiPath))!);
        using var bitmap = Picture(width, height);
        bitmap.Save(Full(wikiPath), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    public static Avalonia.Media.Imaging.WriteableBitmap Picture(int width, int height)
    {
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        unsafe
        {
            var p = (uint*)fb.Address;
            for (var i = 0; i < width * height; i++) p[i] = 0xFF3366AAu;
        }
        return bitmap;
    }

    public List<Image> Images() => Editor.GetVisualDescendants().OfType<Control>().Where(c => c.Tag is WikiPicture).SelectMany(t => t.GetVisualDescendants().OfType<Image>()).ToList();
    public IEnumerable<string> Texts() => Editor.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    /// <summary>Runs layout, queued work and pictures loading until all is quiet (and at least <paramref name="minMs"/> passed).</summary>
    public void Settle(int maxMs = 5000, int minMs = 0)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var loading = Texts().Any(t => t.StartsWith("Loading ", StringComparison.Ordinal));
            if (!loading && watch.ElapsedMilliseconds >= minMs) return;
            if (watch.ElapsedMilliseconds > maxMs) throw new TimeoutException($"Not settled in {maxMs} ms.");
            Thread.Sleep(5);
        }
    }

    public void Keys(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, int settleMs = 0)
    {
        Window.KeyPressQwerty(key, modifiers);
        Window.KeyReleaseQwerty(key, modifiers);
        Settle(minMs: settleMs);
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Settle();
    }

    /// <summary>A file on disk as the clipboard and drag and drop hand it over.</summary>
    public IStorageFile? FileItem(string path) => Window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)).GetAwaiter().GetResult();

    public void Dispose()
    {
        Window.Close();
        ClipboardFiles.UseSystemTools = true;
        try { Services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { Directory.Delete(Root, true); } catch { }
    }
}

