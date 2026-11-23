using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avamux.Core.Wiki;
using Avamux.Views;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace Avamux.UiTests;

/// <summary>
/// The wiki editor as the wiki page sets it up (word wrap, mono font, padding), with its pictures, in a headless
/// window, plus a folder of pictures for the page to embed.
/// </summary>
public sealed class EditorHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "avamux-ui-" + Guid.NewGuid().ToString("N"));
    public Window Window { get; }
    public TextEditor Editor { get; }
    public EditorPictures Pictures { get; }
    public bool On { get; set; } = true;
    public double MaxHeight { get; set; } = 120;
    public long BigBytes { get; set; }

    /// <param name="waitForPictures">Wait until the pictures in view have loaded; otherwise just until the first layout.</param>
    /// <summary>How long <c>prepare</c> took, so a measurement of the constructor can leave it out.</summary>
    public double PrepareMs { get; private set; }

    public EditorHarness(string text, double width = 640, double height = 760, Action<EditorHarness>? prepare = null, int settleMs = 3000, bool waitForPictures = true)
    {
        Directory.CreateDirectory(Dir);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        prepare?.Invoke(this);
        PrepareMs = watch.Elapsed.TotalMilliseconds;
        WritePicture("wide.png", 400, 200, Colors.SeaGreen);
        WritePicture("small.png", 32, 32, Colors.OrangeRed);
        WritePicture("tall.png", 100, 600, Colors.SteelBlue);
        Editor = new TextEditor
        {
            WordWrap = true, FontFamily = Ui.Mono, FontSize = 13, BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 10), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = Ui.Tk("bgEditor"), Foreground = Ui.Tk("textCode"),
        };
        WikiPage.TintSelection(Editor);
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.HighlightCurrentLine = false;
        Editor.TextArea.TextView.LineTransformers.Add(new MarkdownColorizer());
        Editor.Text = text;
        Pictures = new EditorPictures(Editor, () => "", p => Path.Combine(Dir, p), () => new EditorPictureOptions(On, MaxHeight, BigBytes));
        Window = new Window { Width = width, Height = height, Content = Editor };
        Window.Show();
        Editor.TextArea.Focus();
        if (waitForPictures) Settle(settleMs);
        else Window.UpdateLayout(); // the first layout only: work queued for later (loading, measuring) is left queued
    }

    public void WritePicture(string name, int width, int height, Color color)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            var pixel = (uint)(0xFF << 24 | color.R << 16 | color.G << 8 | color.B);
            unsafe
            {
                var p = (uint*)fb.Address;
                for (var i = 0; i < width * height; i++) p[i] = pixel;
            }
        }
        bitmap.Save(Path.Combine(Dir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    /// <summary>Lets layout run and pictures load, until nothing is left loading.</summary>
    /// <param name="search">Also wait for a long page's search for pictures, which waits for typing to pause.</param>
    public void Settle(int maxMs = 3000, bool search = false)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (!Loading() && !(search && Pictures.ParsePending)) return;
            if (watch.ElapsedMilliseconds > maxMs) throw new TimeoutException($"Pictures did not finish loading in {maxMs} ms.");
            Thread.Sleep(5);
        }
    }

    public TextDocument Doc => Editor.Document;
    public string Text => Editor.Text;
    public int Caret => Editor.CaretOffset;
    public AvaloniaEdit.Editing.Caret CaretObject => Editor.TextArea.Caret;

    public DocumentLine Line(int number) => Doc.GetLineByNumber(number);

    /// <summary>Picture tiles drawn in the editor right now (each one's Tag is its <see cref="WikiPicture"/>).</summary>
    public List<Control> Tiles() => Editor.GetVisualDescendants().OfType<Control>().Where(c => c.Tag is WikiPicture).ToList();

    public List<Image> Images() => Tiles().SelectMany(t => t.GetVisualDescendants().OfType<Image>()).ToList();

    /// <summary>Whether a picture in view is still loading.</summary>
    public bool Loading() => Texts().Any(t => t.StartsWith("Loading ", StringComparison.Ordinal));

    public IEnumerable<string> Texts() => Editor.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    /// <summary>Number of the rows the given document line is drawn in (its wrapped text rows plus a pictures row).</summary>
    public int Rows(int line) => Editor.TextArea.TextView.GetOrConstructVisualLine(Line(line)).TextLines.Count;

    public bool HasPictures(int line) => Editor.TextArea.TextView.GetOrConstructVisualLine(Line(line)).Elements.OfType<EditorPictures.PictureElement>().Any();

    public bool CaretOnPictureRow => Pictures.OnPictureRow(CaretObject.Position);

    /// <summary>Where on the window the caret is drawn (the top of its row).</summary>
    public Point CaretPoint()
    {
        var view = Editor.TextArea.TextView;
        var p = view.GetVisualPosition(CaretObject.Position, AvaloniaEdit.Rendering.VisualYPosition.LineTop) - view.ScrollOffset;
        return view.TranslatePoint(p, Window)!.Value;
    }

    /// <summary>Where on the window the character at a document offset is drawn (the middle of its row).</summary>
    public Point PointOf(int offset)
    {
        var view = Editor.TextArea.TextView;
        var p = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(Doc.GetLocation(offset)), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset;
        return view.TranslatePoint(p + new Vector(1, 0), Window)!.Value;
    }

    public Point Center(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;

    public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Settle();
    }

    public void Keys(PhysicalKey key, RawInputModifiers modifiers)
    {
        Window.KeyPressQwerty(key, modifiers);
        Window.KeyReleaseQwerty(key, modifiers);
        Settle();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Settle();
    }

    public void Click(Point at, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.MouseMove(at, modifiers);
        Window.MouseDown(at, button, modifiers);
        Window.MouseUp(at, button, modifiers);
        Settle();
    }

    public string? Clipboard() => Window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();

    public void Capture(string file)
    {
        Window.CaptureRenderedFrame()!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    public void Dispose()
    {
        Window.Close();
        try { Directory.Delete(Dir, true); } catch { }
    }
}
