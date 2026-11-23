using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avamux.Core.Wiki;

namespace Avamux.UiTests;

/// <summary>
/// Pictures drawn inside the wiki editor stand for no text: everything typed, selected, cut, copied, pasted and
/// undone around them must give the same markdown as an editor without them, and the caret must never get stuck on
/// (or be drawn beside) a picture.
/// </summary>
public class EditorPictureTests
{
    private const string Page = "# Trip\n\nBefore\n![wide](wide.png)\nAfter\n";

    [AvaloniaFact]
    public void A_picture_is_drawn_in_a_row_of_its_own_under_its_line()
    {
        using var h = new EditorHarness(Page);
        Assert.True(h.HasPictures(4));
        Assert.False(h.HasPictures(3));
        Assert.Equal(2, h.Rows(4));
        var image = Assert.Single(h.Images());
        // 400×200 shrunk to the 120 pixel limit, keeping its shape.
        Assert.Equal(120, image.Bounds.Height, 1);
        Assert.Equal(240, image.Bounds.Width, 1);
        // The text is untouched.
        Assert.Equal(Page, h.Text);
    }

    [AvaloniaFact]
    public void Small_pictures_keep_their_size_and_several_on_a_line_share_its_row()
    {
        using var h = new EditorHarness("Icons ![a](small.png) and ![b](small.png) here\n");
        Assert.Equal(2, h.Rows(1));
        var images = h.Images();
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal(32, i.Bounds.Height, 1));
        Assert.Equal(h.Center(images[0]).Y, h.Center(images[1]).Y, 1);
    }

    [AvaloniaFact]
    public void Typing_at_the_end_of_a_picture_line_goes_into_the_text_and_the_caret_stays_on_the_text_row()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(4).EndOffset;
        h.Settle();
        var before = h.CaretPoint();
        h.Type(" caption");
        Assert.Equal("# Trip\n\nBefore\n![wide](wide.png) caption\nAfter\n", h.Text);
        Assert.Equal(h.Line(4).EndOffset, h.Caret);
        Assert.False(h.CaretOnPictureRow);
        // Still on the first row of the line, not down beside the picture.
        Assert.Equal(before.Y, h.CaretPoint().Y, 1);
        Assert.True(h.HasPictures(4));
    }

    [AvaloniaFact]
    public void Down_and_up_step_over_the_picture_row()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(3).Offset + 2; // "Be|fore"
        h.Key(Key.Down);
        Assert.Equal(4, h.CaretObject.Line);
        Assert.False(h.CaretOnPictureRow);
        h.Key(Key.Down);
        Assert.Equal(5, h.CaretObject.Line);
        Assert.Equal(3, h.CaretObject.Column); // the column it started in is kept
        h.Key(Key.Up);
        Assert.Equal(4, h.CaretObject.Line);
        Assert.False(h.CaretOnPictureRow);
        h.Key(Key.Up);
        Assert.Equal(3, h.CaretObject.Line);
        Assert.Equal(3, h.CaretObject.Column);
    }

    [AvaloniaFact]
    public void Shift_down_selects_whole_lines_across_a_picture()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(4).Offset;
        h.Key(Key.Down, RawInputModifiers.Shift);
        Assert.Equal(5, h.CaretObject.Line);
        Assert.Equal("![wide](wide.png)\n", h.Editor.SelectedText);
    }

    [AvaloniaFact]
    public void The_last_line_with_a_picture_keeps_the_caret_in_its_text_on_down()
    {
        using var h = new EditorHarness("Top\n![wide](wide.png)");
        h.Editor.CaretOffset = 1;
        h.Key(Key.Down);
        Assert.Equal(2, h.CaretObject.Line);
        h.Key(Key.Down);
        Assert.Equal(2, h.CaretObject.Line);
        Assert.False(h.CaretOnPictureRow);
        h.Type("!");
        Assert.Equal("Top\n![wide](wide.png)!", h.Text);
    }

    [AvaloniaFact]
    public void Right_and_left_cross_the_end_of_a_picture_line_in_one_step()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(4).EndOffset;
        h.Key(Key.Right);
        Assert.Equal(h.Line(5).Offset, h.Caret);
        h.Key(Key.Left);
        Assert.Equal(h.Line(4).EndOffset, h.Caret);
        Assert.False(h.CaretOnPictureRow);
        h.Key(Key.Left);
        Assert.Equal(h.Line(4).EndOffset - 1, h.Caret);
    }

    [AvaloniaFact]
    public void End_and_home_stay_in_the_text()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(4).Offset + 3;
        h.Key(Key.End);
        Assert.Equal(h.Line(4).EndOffset, h.Caret);
        Assert.False(h.CaretOnPictureRow);
        h.Key(Key.Home);
        Assert.Equal(h.Line(4).Offset, h.Caret);
    }

    [AvaloniaFact]
    public void Cut_and_paste_a_picture_line_moves_the_picture_with_it()
    {
        using var h = new EditorHarness(Page);
        h.Editor.Select(h.Line(4).Offset, h.Line(5).Offset - h.Line(4).Offset);
        h.Keys(PhysicalKey.X, RawInputModifiers.Control);
        Assert.Equal("# Trip\n\nBefore\nAfter\n", h.Text);
        Assert.Equal("![wide](wide.png)\n", h.Clipboard());
        Assert.Empty(h.Images());

        h.Editor.CaretOffset = h.Line(3).Offset; // before "Before"
        h.Keys(PhysicalKey.V, RawInputModifiers.Control);
        Assert.Equal("# Trip\n\n![wide](wide.png)\nBefore\nAfter\n", h.Text);
        Assert.True(h.HasPictures(3));
        Assert.Single(h.Images());
    }

    [AvaloniaFact]
    public void Copy_keeps_the_text_and_paste_duplicates_the_picture()
    {
        using var h = new EditorHarness(Page);
        h.Editor.Select(h.Line(4).Offset, h.Line(4).Length);
        h.Keys(PhysicalKey.C, RawInputModifiers.Control);
        Assert.Equal(Page, h.Text);
        Assert.Equal("![wide](wide.png)", h.Clipboard());
        h.Editor.CaretOffset = h.Line(5).EndOffset;
        h.Key(Key.Enter);
        h.Keys(PhysicalKey.V, RawInputModifiers.Control);
        Assert.Equal(Page.Replace("After\n", "After\n![wide](wide.png)\n"), h.Text);
        Assert.Equal(2, h.Images().Count);
    }

    [AvaloniaFact]
    public void Undo_and_redo_bring_pictures_back_and_take_them_away()
    {
        using var h = new EditorHarness(Page);
        h.Editor.Select(h.Line(4).Offset, h.Line(4).Length);
        h.Key(Key.Delete);
        Assert.Empty(h.Images());
        h.Keys(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal(Page, h.Text);
        Assert.Single(h.Images());
        h.Keys(PhysicalKey.Y, RawInputModifiers.Control);
        Assert.Equal("# Trip\n\nBefore\n\nAfter\n", h.Text);
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void Enter_in_the_middle_of_a_picture_line_and_backspace_join_it_again()
    {
        using var h = new EditorHarness("See ![wide](wide.png) here\n");
        h.Editor.CaretOffset = 4; // "See |![wide]…"
        h.Key(Key.Enter);
        Assert.Equal("See \n![wide](wide.png) here\n", h.Text);
        Assert.False(h.HasPictures(1));
        Assert.True(h.HasPictures(2));
        h.Key(Key.Back);
        Assert.Equal("See ![wide](wide.png) here\n", h.Text);
        Assert.True(h.HasPictures(1));
        Assert.Single(h.Images());
    }

    [AvaloniaFact]
    public void Editing_the_address_changes_the_picture_and_breaking_it_shows_why_it_is_missing()
    {
        using var h = new EditorHarness("![p](wide.png)\n");
        Assert.Equal(120, Assert.Single(h.Images()).Bounds.Height, 1);
        h.Editor.Document.Replace(h.Text.IndexOf("wide", StringComparison.Ordinal), 4, "small");
        h.Settle();
        Assert.Equal(32, Assert.Single(h.Images()).Bounds.Height, 1);
        h.Editor.Document.Replace(h.Text.IndexOf("small", StringComparison.Ordinal), 5, "nothere");
        h.Settle();
        Assert.Empty(h.Images());
        Assert.Contains(h.Texts(), t => t == "Not found: nothere.png");
        Assert.Equal("![p](nothere.png)\n", h.Text);
    }

    [AvaloniaFact]
    public void A_missing_picture_appears_once_its_file_does()
    {
        using var h = new EditorHarness("![later](later.png)\n");
        Assert.Contains(h.Texts(), t => t == "Not found: later.png");
        h.WritePicture("later.png", 50, 40, Avalonia.Media.Colors.Gold);
        h.Pictures.Refresh();
        h.Settle();
        Assert.Equal(40, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void Opening_a_code_fence_above_a_picture_hides_it_and_closing_it_shows_it_again()
    {
        using var h = new EditorHarness("Intro\n\nText\n![wide](wide.png)\n\nEnd\n");
        Assert.Single(h.Images());
        h.Editor.CaretOffset = h.Line(2).Offset;
        h.Type("```");
        Assert.Empty(h.Images());
        Assert.False(h.HasPictures(4));
        h.Key(Key.Back);
        Assert.Single(h.Images());
        Assert.True(h.HasPictures(4));
    }

    [AvaloniaFact]
    public void Clicking_a_picture_selects_its_markdown_so_it_can_be_cut()
    {
        using var h = new EditorHarness("Before ![wide](wide.png) after\nNext\n");
        h.Editor.CaretOffset = h.Line(2).Offset;
        h.Click(h.Center(Assert.Single(h.Tiles())));
        Assert.Equal("![wide](wide.png)", h.Editor.SelectedText);
        Assert.True(h.Editor.TextArea.IsKeyboardFocusWithin);
        h.Keys(PhysicalKey.X, RawInputModifiers.Control);
        Assert.Equal("Before  after\nNext\n", h.Text);
        Assert.Equal("![wide](wide.png)", h.Clipboard());
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void Clicking_beside_a_picture_puts_the_caret_at_the_end_of_its_line()
    {
        using var h = new EditorHarness(Page);
        var image = Assert.Single(h.Images());
        var beside = h.Center(image) + new Point(image.Bounds.Width / 2 + 120, 0);
        h.Click(beside);
        Assert.Equal(h.Line(4).EndOffset, h.Caret);
        Assert.False(h.CaretOnPictureRow);
        Assert.Equal(0, h.Editor.SelectionLength);
    }

    [AvaloniaFact]
    public void Select_all_and_typing_replaces_everything_pictures_included()
    {
        using var h = new EditorHarness(Page);
        h.Keys(PhysicalKey.A, RawInputModifiers.Control);
        Assert.Equal(Page, h.Editor.SelectedText);
        h.Type("x");
        Assert.Equal("x", h.Text);
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void Pasting_markdown_with_pictures_shows_them()
    {
        using var h = new EditorHarness("Start\n");
        h.Window.Clipboard!.SetTextAsync("![a](small.png)\ntext\n![b](wide.png)\n").GetAwaiter().GetResult();
        h.Editor.CaretOffset = h.Text.Length;
        h.Keys(PhysicalKey.V, RawInputModifiers.Control);
        Assert.Equal("Start\n![a](small.png)\ntext\n![b](wide.png)\n", h.Text);
        Assert.Equal(2, h.Images().Count);
        Assert.True(h.HasPictures(2));
        Assert.True(h.HasPictures(4));
    }

    [AvaloniaFact]
    public void Turning_the_option_off_and_on_takes_effect_at_once()
    {
        using var h = new EditorHarness(Page);
        h.On = false;
        h.Pictures.Refresh();
        h.Settle();
        Assert.Empty(h.Images());
        Assert.Equal(1, h.Rows(4));
        h.On = true;
        h.MaxHeight = 60;
        h.Pictures.Refresh();
        h.Settle();
        Assert.Equal(60, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void A_picture_is_never_wider_than_the_editor_and_follows_its_width()
    {
        using var h = new EditorHarness("![tall](tall.png) ![wide](wide.png)\n", width: 300);
        h.MaxHeight = 1000;
        h.Pictures.Refresh();
        h.Settle();
        var images = h.Images();
        Assert.All(images, i => Assert.True(i.Bounds.Width <= 300));
        h.Window.Width = 900;
        h.Settle();
        Assert.Equal(400, h.Images().Single(i => i.Bounds.Height < 300).Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void A_picture_in_an_indented_list_item_lines_up_with_the_item()
    {
        using var h = new EditorHarness("- one\n    ![wide](wide.png)\n- two\n");
        Assert.True(h.HasPictures(2));
        Assert.Equal(2, h.Rows(2));
        h.Editor.CaretOffset = h.Line(1).Offset + 2;
        h.Key(Key.Down);
        h.Key(Key.Down);
        Assert.Equal(3, h.CaretObject.Line);
    }

    [AvaloniaFact]
    public void Pictures_too_big_for_the_editor_say_so_instead_of_loading()
    {
        using var h = new EditorHarness("![wide](wide.png)\n");
        h.BigBytes = 10;
        h.Pictures.Refresh();
        h.Settle();
        Assert.Empty(h.Images());
        Assert.Contains(h.Texts(), t => t.StartsWith("wide.png (", StringComparison.Ordinal) && t.EndsWith("is too big to show here", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Long_wrapped_lines_keep_wrapping_and_the_picture_follows_the_last_row()
    {
        var words = string.Join(' ', Enumerable.Repeat("lorem", 60));
        using var h = new EditorHarness(words + " ![wide](wide.png)\nNext\n", width: 400);
        var rows = h.Rows(1);
        Assert.True(rows >= 4);
        h.Editor.CaretOffset = h.Line(1).Offset;
        for (var i = 0; i < rows - 1; i++) h.Key(Key.Down);
        Assert.Equal(2, h.CaretObject.Line);
    }

    [AvaloniaFact]
    public void Up_into_a_wrapped_picture_line_lands_on_its_last_row_of_text_at_the_same_place()
    {
        var words = string.Join(' ', Enumerable.Repeat("lorem", 40));
        using var h = new EditorHarness(words + " ![wide](wide.png)\nNext line here\n", width: 400);
        h.Editor.CaretOffset = h.Line(2).Offset + 3;
        var x = h.CaretPoint().X;
        h.Key(Key.Up);
        Assert.Equal(1, h.CaretObject.Line);
        Assert.False(h.CaretOnPictureRow);
        Assert.Equal(x, h.CaretPoint().X, 0.5);
        var rows = h.Rows(1);
        // On the last row of text: one more Up is still on line 1, and Down goes straight back to line 2.
        h.Key(Key.Down);
        Assert.Equal(2, h.CaretObject.Line);
        Assert.Equal(h.Line(2).Offset + 3, h.Caret);
        Assert.True(rows >= 3);
    }

    [AvaloniaFact]
    public void Shift_up_selects_back_over_a_picture()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(5).Offset;
        h.Key(Key.Up, RawInputModifiers.Shift);
        h.Key(Key.Up, RawInputModifiers.Shift);
        Assert.Equal(3, h.CaretObject.Line);
        Assert.Equal("Before\n![wide](wide.png)\n", h.Editor.SelectedText);
        h.Keys(PhysicalKey.X, RawInputModifiers.Control);
        Assert.Equal("# Trip\n\nAfter\n", h.Text);
        Assert.Empty(h.Images());
    }

    [AvaloniaFact]
    public void Down_through_a_run_of_picture_lines_visits_each_line_once()
    {
        using var h = new EditorHarness("top\n![a](small.png)\n![b](wide.png)\n![c](small.png)\nend\n");
        h.Editor.CaretOffset = 0;
        var lines = new List<int>();
        for (var i = 0; i < 4; i++) { h.Key(Key.Down); lines.Add(h.CaretObject.Line); }
        Assert.Equal([2, 3, 4, 5], lines);
        lines.Clear();
        for (var i = 0; i < 4; i++) { h.Key(Key.Up); lines.Add(h.CaretObject.Line); }
        Assert.Equal([4, 3, 2, 1], lines);
    }

    [AvaloniaFact]
    public void Right_clicking_a_picture_selects_it_and_opens_the_editing_menu()
    {
        using var h = new EditorHarness(Page);
        var menu = new Avalonia.Controls.ContextMenu();
        menu.Items.Add(new Avalonia.Controls.MenuItem { Header = "Cut" });
        h.Editor.TextArea.ContextMenu = menu;
        h.Click(h.Center(Assert.Single(h.Tiles())), MouseButton.Right);
        Assert.Equal("![wide](wide.png)", h.Editor.SelectedText);
        Assert.True(menu.IsOpen);
        menu.Close();
    }

    [AvaloniaFact]
    public void Delete_after_clicking_a_picture_removes_just_its_markdown()
    {
        using var h = new EditorHarness("A ![a](small.png) B ![b](wide.png) C\n");
        var tiles = h.Tiles();
        Assert.Equal(2, tiles.Count);
        h.Click(h.Center(tiles[1]));
        Assert.Equal("![b](wide.png)", h.Editor.SelectedText);
        h.Key(Key.Delete);
        Assert.Equal("A ![a](small.png) B  C\n", h.Text);
        Assert.Equal(32, Assert.Single(h.Images()).Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void Typing_inside_the_markdown_of_a_picture_edits_the_text_only()
    {
        using var h = new EditorHarness(Page);
        h.Editor.CaretOffset = h.Line(4).Offset + 2; // "![|wide]"
        h.Type("very ");
        Assert.Equal("# Trip\n\nBefore\n![very wide](wide.png)\nAfter\n", h.Text);
        Assert.Single(h.Images());
        h.Keys(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal(Page, h.Text);
    }

    [AvaloniaFact]
    public void Ctrl_end_and_ctrl_home_reach_the_ends_of_the_text()
    {
        using var h = new EditorHarness("![wide](wide.png)\ntext\n![wide](wide.png)");
        h.Keys(PhysicalKey.End, RawInputModifiers.Control);
        Assert.Equal(h.Text.Length, h.Caret);
        Assert.False(h.CaretOnPictureRow);
        h.Type("!");
        Assert.EndsWith("(wide.png)!", h.Text);
        h.Keys(PhysicalKey.Home, RawInputModifiers.Control);
        Assert.Equal(0, h.Caret);
    }

    [AvaloniaFact]
    public void Ctrl_end_shows_the_end_of_a_long_page_full_of_pictures()
    {
        var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"Line {i}\n![w](wide.png)")) + "\nThe end";
        using var h = new EditorHarness(text, height: 500);
        h.Keys(PhysicalKey.End, RawInputModifiers.Control);
        Assert.Equal(text.Length, h.Caret);
        var y = h.CaretPoint().Y;
        Assert.InRange(y, 0, h.Editor.Bounds.Height - 10);
        h.Keys(PhysicalKey.Home, RawInputModifiers.Control);
        Assert.InRange(h.CaretPoint().Y, 0, 40);
    }

    [AvaloniaFact]
    public void Page_down_and_up_never_leave_the_caret_beside_a_picture()
    {
        var text = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"Line {i}\n![w](wide.png)"));
        using var h = new EditorHarness(text, height: 400);
        for (var i = 0; i < 6; i++)
        {
            h.Key(Key.PageDown);
            Assert.False(h.CaretOnPictureRow);
        }
        for (var i = 0; i < 6; i++)
        {
            h.Key(Key.PageUp);
            Assert.False(h.CaretOnPictureRow);
        }
        Assert.Equal(text, h.Text);
    }

    [AvaloniaFact]
    public void An_img_tag_is_drawn_at_the_size_it_asks_for()
    {
        using var h = new EditorHarness("<img src=\"wide.png\" width=\"100\">\n");
        var image = Assert.Single(h.Images());
        Assert.Equal(100, image.Bounds.Width, 1);
        Assert.Equal(50, image.Bounds.Height, 1);
    }
}
