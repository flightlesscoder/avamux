using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avamux.Core.Wiki;

namespace Avamux.UiTests;

/// <summary>Where pictures come from and what goes wrong with them, and the less common ways of editing around them.</summary>
public class EditorEdgeTests
{
    private const string Page = "# Trip\n\nBefore\n![wide](wide.png)\nAfter\n";

    // ---- sources ----

    /// <summary>A web server on this machine serving one picture, and a 404 for anything else.</summary>
    private sealed class PictureServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public int Requests;

        public PictureServer(byte[] png)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); } catch { return; }
                    Interlocked.Increment(ref Requests);
                    if (context.Request.Url!.AbsolutePath == "/pic.png")
                    {
                        context.Response.ContentType = "image/png";
                        await context.Response.OutputStream.WriteAsync(png);
                    }
                    else context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    [AvaloniaFact]
    public void Web_pictures_load_once_and_a_missing_one_says_why()
    {
        using var h = new EditorHarness("");
        using var server = new PictureServer(File.ReadAllBytes(Path.Combine(h.Dir, "small.png")));
        h.Editor.Text = $"![web]({server.Url}pic.png)\n![again]({server.Url}pic.png)\n![gone]({server.Url}gone.png)\n";
        h.Settle(5000);
        var images = h.Images();
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal(32, i.Bounds.Height, 1));
        Assert.Same(images[0].Source, images[1].Source);
        Assert.Contains(h.Texts(), t => t.StartsWith("Cannot load gone.png: 404", StringComparison.Ordinal));
        Assert.Equal(2, server.Requests); // pic.png once, gone.png once
    }

    [AvaloniaFact]
    public void A_picture_written_into_the_page_is_drawn()
    {
        using var h = new EditorHarness("");
        var base64 = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(h.Dir, "small.png")));
        h.Editor.Text = $"Inline ![dot](data:image/png;base64,{base64}) here\n";
        h.Settle();
        Assert.Equal(32, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void Pictures_that_cannot_be_drawn_say_why()
    {
        using var h = new EditorHarness("");
        File.WriteAllText(Path.Combine(h.Dir, "logo.svg"), "<svg xmlns='http://www.w3.org/2000/svg'/>");
        File.WriteAllText(Path.Combine(h.Dir, "broken.png"), "this is not a png");
        h.Editor.Text = "![a](logo.svg)\n![b](broken.png)\n![c](../../outside.png)\n![d](ftp://host/x.png)\n";
        h.Settle();
        Assert.Empty(h.Images());
        var texts = h.Texts().ToList();
        Assert.Contains("logo.svg: SVG pictures show in the preview only", texts);
        Assert.Contains("broken.png is not a picture avamux can draw", texts);
        Assert.Contains("Cannot show outside.png", texts);
        Assert.Contains("Cannot show x.png", texts);
        Assert.Equal(4, h.Tiles().Count);
    }

    [AvaloniaFact]
    public void The_same_picture_many_times_is_loaded_once()
    {
        using var h = new EditorHarness(string.Concat(Enumerable.Repeat("![w](wide.png)\n", 6)));
        var sources = h.Images().Select(i => i.Source).Distinct().ToList();
        Assert.Single(sources);
    }

    [AvaloniaFact]
    public void Replacing_the_whole_text_shows_the_new_pages_pictures()
    {
        using var h = new EditorHarness(Page);
        h.Editor.Text = "Other page\n![s](small.png)\n![t](tall.png)\n";
        h.Settle();
        Assert.Equal(2, h.Images().Count);
        Assert.True(h.HasPictures(2) && h.HasPictures(3));
        h.Editor.Document = new AvaloniaEdit.Document.TextDocument("![w](wide.png)");
        h.Settle();
        Assert.Single(h.Images());
    }

    // ---- unusual text ----

    [AvaloniaFact]
    public void Windows_line_ends_edit_like_any_others()
    {
        using var h = new EditorHarness("Before\r\n![wide](wide.png)\r\nAfter\r\n");
        Assert.True(h.HasPictures(2));
        h.Editor.CaretOffset = 2;
        h.Key(Key.Down);
        h.Key(Key.Down);
        Assert.Equal(3, h.CaretObject.Line);
        h.Editor.CaretOffset = h.Line(2).EndOffset;
        h.Type("!");
        Assert.Equal("Before\r\n![wide](wide.png)!\r\nAfter\r\n", h.Text);
        h.Editor.Select(h.Line(2).Offset, h.Line(3).Offset - h.Line(2).Offset);
        h.Keys(PhysicalKey.X, RawInputModifiers.Control);
        Assert.Equal("Before\r\nAfter\r\n", h.Text);
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void A_line_too_long_to_show_pictures_on_still_edits()
    {
        // AvaloniaEdit lays out lines over 3000 characters as plain text, so their pictures are left out.
        var longLine = new string('x', 5000) + " ![w](wide.png)";
        using var h = new EditorHarness(longLine + "\n![s](small.png)\n");
        Assert.False(h.HasPictures(1));
        Assert.True(h.HasPictures(2));
        h.Editor.CaretOffset = h.Line(1).EndOffset;
        h.Type("y");
        Assert.Equal(longLine + "y\n![s](small.png)\n", h.Text);
        Assert.True(h.HasPictures(2)); // (scrolled out of view by the long line's wrapped rows)
    }

    // ---- less common editing ----

    [AvaloniaFact]
    public void Word_moves_cross_the_end_of_a_picture_line()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(4).EndOffset;
        h.Key(Key.Right, RawInputModifiers.Control);
        Assert.Equal(5, h.CaretObject.Line);
        Assert.False(h.CaretOnPictureRow);
        h.Editor.CaretOffset = h.Line(5).Offset;
        h.Key(Key.Left, RawInputModifiers.Control);
        Assert.Equal(4, h.CaretObject.Line);
        Assert.False(h.CaretOnPictureRow);
    }

    [AvaloniaFact]
    public void Backspace_at_the_start_of_the_next_line_joins_it_to_the_picture_line()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(5).Offset;
        h.Key(Key.Back);
        Assert.Equal("# Trip\n\nBefore\n![wide](wide.png)After\n", h.Text);
        Assert.True(h.HasPictures(4));
        Assert.Single(h.Images());
        Assert.Equal(h.Line(4).Offset + "![wide](wide.png)".Length, h.Caret);
    }

    [AvaloniaFact]
    public void Shift_click_selects_across_a_picture()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(3).Offset;
        h.Click(h.PointOf(h.Line(5).Offset + 2), modifiers: RawInputModifiers.Shift);
        Assert.Equal("Before\n![wide](wide.png)\nAf", h.Editor.SelectedText);
    }

    [AvaloniaFact]
    public void Dragging_the_mouse_from_above_a_picture_to_below_it_selects_the_lines_between()
    {
        using var h = new EditorHarness(Page);
        var from = h.PointOf(h.Line(3).Offset);
        var to = h.PointOf(h.Line(5).Offset + 3);
        h.Window.MouseMove(from);
        h.Window.MouseDown(from, MouseButton.Left);
        foreach (var f in new[] { 0.25, 0.5, 0.75, 1.0 }) h.Window.MouseMove(from + (to - from) * f, RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(to, MouseButton.Left);
        h.Settle();
        Assert.Equal("Before\n![wide](wide.png)\nAft", h.Editor.SelectedText);
    }

    [AvaloniaFact]
    public void Tab_indents_selected_picture_lines_and_shift_tab_takes_it_back()
    {
        using var h = new EditorHarness("- one\n![a](small.png)\n![b](small.png)\n");
        h.Editor.Select(h.Line(2).Offset, h.Line(3).EndOffset - h.Line(2).Offset);
        h.Key(Key.Tab);
        Assert.Equal("- one\n    ![a](small.png)\n    ![b](small.png)\n", h.Text);
        Assert.Equal(2, h.Images().Count);
        h.Key(Key.Tab, RawInputModifiers.Shift);
        Assert.Equal("- one\n![a](small.png)\n![b](small.png)\n", h.Text);
    }

    [AvaloniaFact]
    public void Ctrl_shift_end_selects_to_the_end_over_every_picture()
    {
        using var h = new EditorHarness(Page + "![s](small.png)");
        h.Editor.CaretOffset = h.Line(3).Offset;
        h.Keys(PhysicalKey.End, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal("Before\n![wide](wide.png)\nAfter\n![s](small.png)", h.Editor.SelectedText);
        h.Key(Key.Delete);
        Assert.Equal("# Trip\n\n", h.Text);
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void Clicking_one_of_several_pictures_on_a_wrapped_line_selects_that_one()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 30));
        using var h = new EditorHarness($"{words} ![a](small.png) {words} ![b](wide.png) end\n", width: 420);
        Assert.Equal(2, h.Tiles().Count);
        h.Click(h.Center(h.Tiles()[0]));
        Assert.Equal("![a](small.png)", h.Editor.SelectedText);
        h.Click(h.Center(h.Tiles()[1])); // fresh: the editor may lay the line out again after a click
        Assert.Equal("![b](wide.png)", h.Editor.SelectedText);
    }

    // ---- long pages: pictures are found off the UI thread once typing pauses ----

    private static string LongText() => string.Concat(Enumerable.Repeat("Some ordinary text on a long page.\n", 4000)); // ~140 K characters

    [AvaloniaFact]
    public void On_a_long_page_a_typed_picture_appears_once_typing_pauses()
    {
        using var h = new EditorHarness(LongText() + "![w](wide.png)\n");
        Assert.True(h.HasPictures(4001)); // found at once when the page is opened
        h.Editor.CaretOffset = h.Text.Length;
        h.Type("![s](small.png)");
        Assert.True(h.Pictures.ParsePending);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (h.Pictures.ParsePending && watch.ElapsedMilliseconds < 5000) { h.Settle(); Thread.Sleep(20); }
        h.Settle();
        Assert.True(h.HasPictures(4002));
        Assert.True(h.HasPictures(4001));
    }

    [AvaloniaFact]
    public void On_a_long_page_clicking_a_picture_straight_after_typing_above_it_selects_its_markdown()
    {
        using var h = new EditorHarness("Title\n![w](wide.png)\n" + LongText());
        h.Editor.CaretOffset = 0;
        h.Type("A longer ");      // the picture's place in the text moves; the search for it waits for a pause
        Assert.True(h.Pictures.ParsePending);
        h.Click(h.Center(Assert.Single(h.Tiles())));
        Assert.Equal("![w](wide.png)", h.Editor.SelectedText);
    }

    [AvaloniaFact]
    public void On_a_long_page_enter_before_a_picture_moves_it_down_with_its_markdown()
    {
        using var h = new EditorHarness("Title ![w](wide.png)\n" + LongText());
        Assert.True(h.HasPictures(1));
        h.Editor.CaretOffset = "Title ".Length;
        h.Key(Avalonia.Input.Key.Enter);
        // At once: the first line no longer shows the picture its text has lost.
        Assert.False(h.HasPictures(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (h.Pictures.ParsePending && watch.ElapsedMilliseconds < 5000) { h.Settle(); Thread.Sleep(20); }
        h.Settle();
        Assert.False(h.HasPictures(1));
        Assert.True(h.HasPictures(2));
    }
}
