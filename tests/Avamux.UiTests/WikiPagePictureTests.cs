using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using AvaloniaEdit.Rendering;

namespace Avamux.UiTests;

/// <summary>
/// The editor's pictures inside the real wiki page: pasting and dropping pictures, pages in different folders,
/// files that are not markdown, files changing on disk, the setting, and saving.
/// </summary>
public class WikiPagePictureTests
{
    private static void Folder(WikiHarness h)
    {
        h.WritePicture("Trips/img/sunset.png", 300, 150);
        File.WriteAllText(h.Full("Trips/Lake.md"), "# Lake\n\n![sunset](img/sunset.png)\n\nEnd\n");
    }

    [AvaloniaFact]
    public void Pasting_a_picture_saves_it_next_to_the_page_and_shows_it_where_the_caret_is()
    {
        using var h = new WikiHarness(Folder, open: "Trips/Lake.md");
        Assert.Single(h.Images());
        h.Editor.CaretOffset = h.Text.IndexOf("End", StringComparison.Ordinal);
        using var picture = WikiHarness.Picture(80, 40); // the clipboard holds on to it: not disposed before the paste
        h.Window.Clipboard!.SetBitmapAsync(picture).GetAwaiter().GetResult();
        h.Keys(PhysicalKey.V, RawInputModifiers.Control, settleMs: 100);

        var saved = Directory.GetFiles(h.Full("Trips"), "Pasted image *.png").Select(Path.GetFileName).ToList();
        var name = Assert.Single(saved)!;
        Assert.Contains($"![{Path.GetFileNameWithoutExtension(name)}](<{name}>)End", h.Text);
        Assert.Equal(2, h.Images().Count);
        Assert.Contains(h.Images(), i => Math.Abs(i.Bounds.Height - 40) < 1);
        // One undo step takes the link back out (the file stays, as with any paste).
        h.Keys(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("# Lake\n\n![sunset](img/sunset.png)\n\nEnd\n", h.Text);
        Assert.Single(h.Images());
    }

    [AvaloniaFact]
    public void Pasting_copied_picture_files_copies_them_in_and_shows_them()
    {
        using var h = new WikiHarness(Folder, open: "Trips/Lake.md");
        var outside = Path.Combine(h.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        using (var a = WikiHarness.Picture(60, 30)) a.Save(Path.Combine(outside, "a.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        using (var b = WikiHarness.Picture(50, 50)) b.Save(Path.Combine(outside, "b c.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        h.Window.Clipboard!.SetFilesAsync([h.FileItem(Path.Combine(outside, "a.png"))!, h.FileItem(Path.Combine(outside, "b c.png"))!]).GetAwaiter().GetResult();
        h.Editor.CaretOffset = h.Text.Length;
        h.Keys(PhysicalKey.V, RawInputModifiers.Control, settleMs: 100);
        Assert.EndsWith("End\n![a](a.png)\n![b c](<b c.png>)", h.Text);
        Assert.True(File.Exists(h.Full("Trips/a.png")));
        Assert.True(File.Exists(h.Full("Trips/b c.png")));
        Assert.Equal(3, h.Images().Count);
    }

    [AvaloniaFact]
    public void Dropping_a_picture_on_a_line_inserts_it_there()
    {
        using var h = new WikiHarness(Folder, open: "Trips/Lake.md");
        var outside = Path.Combine(h.Root, "drop.png");
        using (var p = WikiHarness.Picture(40, 40)) p.Save(outside, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var view = h.Editor.TextArea.TextView;
        var heading = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(1, 7), VisualYPosition.LineMiddle) - view.ScrollOffset; // after "# Lake"
        var at = view.TranslatePoint(heading + new Vector(2, 0), h.Window)!.Value;
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetFile(h.FileItem(outside)!);
        data.Add(item);
        foreach (var kind in new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver, RawDragEventType.Drop })
            h.Window.DragDrop(at, kind, data, DragDropEffects.Copy);
        h.Settle(minMs: 50);
        Assert.StartsWith("# Lake![drop](drop.png)\n", h.Text);
        Assert.True(File.Exists(h.Full("Trips/drop.png")));
        Assert.Equal(2, h.Images().Count);
    }

    [AvaloniaFact]
    public void Each_page_finds_its_pictures_in_its_own_folder()
    {
        using var h = new WikiHarness(w =>
        {
            w.WritePicture("A/pic.png", 30, 30);
            w.WritePicture("B/pic.png", 90, 90);
            File.WriteAllText(w.Full("A/p.md"), "![p](pic.png)\n");
            File.WriteAllText(w.Full("B/p.md"), "![p](pic.png)\n");
        }, open: "A/p.md");
        Assert.Equal(30, Assert.Single(h.Images()).Bounds.Height, 1);
        h.Page.Open("B/p.md");
        h.Settle();
        Assert.Equal(90, Assert.Single(h.Images()).Bounds.Height, 1);
        h.Page.Open("A/p.md");
        h.Settle();
        Assert.Equal(30, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void A_text_file_shows_its_markdown_without_pictures()
    {
        using var h = new WikiHarness(w =>
        {
            w.WritePicture("pic.png", 30, 30);
            File.WriteAllText(w.Full("notes.txt"), "![p](pic.png)\n");
        }, open: "notes.txt");
        Assert.Equal("![p](pic.png)\n", h.Text);
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void A_picture_another_program_adds_appears_without_reopening_the_page()
    {
        using var h = new WikiHarness(w => File.WriteAllText(w.Full("p.md"), "![later](later.png)\n"), open: "p.md");
        Assert.Contains(h.Texts(), t => t == "Not found: later.png");
        h.WritePicture("later.png", 70, 35);
        h.Wiki.NotifyChanged(); // what the wiki's file watching and the CLI do
        h.Settle(minMs: 400);
        Assert.Equal(35, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void Changing_the_setting_applies_to_the_open_page()
    {
        using var h = new WikiHarness(Folder, open: "Trips/Lake.md");
        Assert.Single(h.Images());
        h.Services.Config.Set("wiki.editorPictures", false);
        h.Settle(minMs: 300);
        Assert.Empty(h.Images());
        h.Services.Config.Set("wiki.editorPictureHeight", 50);
        h.Services.Config.Set("wiki.editorPictures", true);
        h.Settle(minMs: 300);
        Assert.Equal(50, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void Saving_writes_exactly_the_markdown_typed_around_pictures()
    {
        using var h = new WikiHarness(Folder, open: "Trips/Lake.md");
        h.Editor.CaretOffset = h.Text.IndexOf(")\n", StringComparison.Ordinal) + 1;
        h.Type(" caption");
        h.Keys(PhysicalKey.S, RawInputModifiers.Control);
        Assert.Equal("# Lake\n\n![sunset](img/sunset.png) caption\n\nEnd\n", File.ReadAllText(h.Full("Trips/Lake.md")));
    }
}
