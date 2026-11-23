using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avamux.Core.Wiki;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Avamux.Views;

/// <summary>What the editor's pictures follow: whether they are on, how tall they may be, and when a file is too big.</summary>
/// <param name="BigBytes">Files larger than this are not loaded into the editor (0: no limit).</param>
public readonly record struct EditorPictureOptions(bool On, double MaxHeight, long BigBytes);

/// <summary>
/// Shows the pictures a markdown page embeds inside the editor, each under the line that embeds it, the way the
/// preview shows them. The text itself is untouched: a picture is drawn in a row of its own at the end of its line
/// and stands for no characters, so typing, selecting, cutting, pasting and undo work on the markdown exactly as
/// without it. The caret never rests on a picture's row: the arrow keys step over it, and a click on it selects the
/// markdown of the picture (so Delete or Cut removes it, and Copy copies it).
/// </summary>
public sealed class EditorPictures
{
    private readonly TextEditor _editor;
    private readonly TextView _view;
    private readonly Func<string?> _folder;
    private readonly Func<string, string?> _fullPath;
    private readonly Func<EditorPictureOptions> _options;
    private readonly WidthProbe _probe;
    private readonly Generator _generator;
    private Dictionary<DocumentLine, List<WikiPicture>> _byLine = new();
    private Dictionary<DocumentLine, string> _signatures = new();
    private EditorPictureOptions _current;
    private bool _redrawPosted, _estimatePosted, _estimateAll, _heightsChanged, _estimating;

    /// <summary>For tests: how often the whole view was redrawn because pictures finished loading.</summary>
    internal int LoadRedraws { get; private set; }
    /// <summary>For tests: how many pictures were decoded, over all editors.</summary>
    internal static int Decodes;
    private readonly HashSet<DocumentLine> _toEstimate = [];

    /// <param name="folder">Wiki folder of the open page, or null when it is not a markdown page (no pictures).</param>
    /// <param name="fullPath">Full path of a wiki path, or null when it is outside the wiki.</param>
    public EditorPictures(TextEditor editor, Func<string?> folder, Func<string, string?> fullPath, Func<EditorPictureOptions> options)
    {
        _editor = editor;
        _view = editor.TextArea.TextView;
        _folder = folder;
        _fullPath = fullPath;
        _options = options;
        _current = options();
        _generator = new Generator(this);
        _probe = new WidthProbe(() => EstimateSoon());
        _view.InsertLayer(_probe, KnownLayer.Background, LayerInsertionPosition.Above);
        _view.ElementGenerators.Add(_generator);
        void Watch(TextDocument? doc)
        {
            if (doc is null) return;
            doc.Changing += (_, e) => _wholeText = e.Offset == 0 && e.RemovalLength == doc.TextLength;
        }
        Watch(editor.Document);
        editor.TextChanged += (_, _) => Reparse();
        editor.DocumentChanged += (_, _) => { Watch(editor.Document); _wholeText = true; Reparse(); };

        // Up and Down: AvaloniaEdit treats a picture's row like any wrapped row of its line, and since no text can
        // sit there it ends up at the end of the line's text, never getting past. These bindings come before its
        // own (key bindings run in order, before the key event is routed), let it move, and then put the caret
        // where it should be: on the next or previous line's text, as far across as it was.
        var area = editor.TextArea;
        foreach (var (key, up, extend) in new[] { (Key.Down, false, false), (Key.Down, false, true), (Key.Up, true, false), (Key.Up, true, true) })
            area.KeyBindings.Insert(0, new KeyBinding
            {
                Gesture = new KeyGesture(key, extend ? KeyModifiers.Shift : KeyModifiers.None),
                Command = new Act(() => _byLine.Count > 0, () => MoveVertically(up, extend)),
            });
        // Anything else that would leave the caret on a picture's row puts it at the end of the line's text.
        area.Caret.PositionChanged += (_, _) =>
        {
            if (!OnPictureRow(area.Caret.Position)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (OnPictureRow(area.Caret.Position)) ToTextEnd(area.Caret.Position);
            }, DispatcherPriority.Send);
        };
        ParseNow(); // what the editor holds already, at once whatever its length
    }

    /// <summary>Reads the settings and the files again: shows what is on disk now, with the options as they are now.</summary>
    public void Refresh()
    {
        _current = _options();
        _byLine = new();
        _signatures = new();
        ParseNow();
        _view.Redraw();
        EstimateSoon();
    }

    /// <summary>The pictures of the page as last read, by the line under which they are drawn (for tests and tools).</summary>
    public IReadOnlyDictionary<DocumentLine, List<WikiPicture>> Lines => _byLine;

    /// <summary>Pages longer than this are searched for pictures off the UI thread, once typing pauses.</summary>
    private const int BackgroundChars = 100_000;
    private int _version;            // counts changes to the text, to tell whether a search's result is still current
    private bool _waiting;           // a search is waiting for typing to pause
    private Dictionary<DocumentLine, string[]> _sources = new(); // the markdown of each line's pictures, as found
    private bool _wholeText;         // the last change replaced the whole text (a page was opened)

    /// <summary>
    /// Finds the pictures again after a change. A short page is read at once. A long one (reading it takes tens of
    /// milliseconds a megabyte) is read on another thread once typing pauses, except when it has just been opened;
    /// meanwhile lines keep the pictures they had.
    /// </summary>
    private void Reparse()
    {
        _version++;
        var doc = _editor.Document;
        if (doc is null || doc.TextLength < BackgroundChars || _wholeText || !_current.On || _folder() is null)
        {
            _wholeText = false;
            ParseNow();
            return;
        }
        WaitThenParse();
    }

    /// <summary>Searches once the text has not changed for a moment.</summary>
    private void WaitThenParse()
    {
        _waiting = true;
        var version = _version;
        Task.Delay(200).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (version == _version && _waiting) ParseInBackground();
        }), TaskScheduler.Default);
    }

    /// <summary>Whether the pictures found are older than the text (a long page's search waits for typing to pause).</summary>
    internal bool ParsePending => _waiting || _parsing;
    private bool _parsing;

    private void ParseInBackground()
    {
        _waiting = false;
        if (_editor.Document is not { } doc) return;
        var version = _version;
        var text = doc.CreateSnapshot().Text;
        _parsing = true;
        Task.Run(() => WikiPictures.Find(text)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _parsing = false;
            if (!ReferenceEquals(doc, _editor.Document)) return;
            if (version != _version) { if (!_waiting) WaitThenParse(); return; }
            Apply(t.IsCompletedSuccessfully ? t.Result : [], text);
        }), TaskScheduler.Default);
    }

    private void ParseNow()
    {
        _waiting = false;
        _parsing = false;
        _version++; // a search still running in the background is out of date now
        var doc = _editor.Document;
        var text = doc?.Text ?? "";
        Apply(doc is not null && _current.On && _folder() is not null ? WikiPictures.Find(text) : [], text);
    }

    /// <summary>
    /// Whether a line still holds the pictures found on it. While a long page's search waits, an edit may have moved
    /// a picture to another line (Enter typed before it): the line it was on must not go on showing it.
    /// </summary>
    private bool StillHolds(DocumentLine line)
    {
        if (!ParsePending) return true;
        if (line.IsDeleted || !_sources.TryGetValue(line, out var sources)) return false;
        var text = _editor.Document.GetText(line);
        return sources.All(src => text.Contains(src, StringComparison.Ordinal));
    }

    /// <summary>
    /// Takes a fresh list of the page's pictures. AvaloniaEdit redraws only the lines that were edited; a line whose
    /// pictures changed without being edited itself (a code fence opened above it) is redrawn here.
    /// </summary>
    /// <param name="text">The text <paramref name="found"/> was found in: the document as it is now.</param>
    private void Apply(IReadOnlyList<WikiPicture> found, string text)
    {
        var doc = _editor.Document;
        var next = new Dictionary<DocumentLine, List<WikiPicture>>();
        if (doc is not null)
            foreach (var picture in found)
            {
                if (picture.End > doc.TextLength) continue;
                var line = doc.GetLineByOffset(Math.Max(picture.Start, picture.End - 1));
                if (line.Length > 3000) continue; // AvaloniaEdit shows such long lines as plain text, without generators
                if (!next.TryGetValue(line, out var list)) next[line] = list = [];
                list.Add(picture);
            }
        var signatures = next.ToDictionary(p => p.Key, p => string.Join("\n", p.Value.Select(x => x.Url)));
        var changed = new List<DocumentLine>();
        foreach (var (line, sig) in signatures)
            if (!_signatures.TryGetValue(line, out var before) || before != sig) { Redraw(line); changed.Add(line); }
        foreach (var line in _signatures.Keys)
            if (!signatures.ContainsKey(line)) Redraw(line);
        _byLine = next;
        _signatures = signatures;
        _sources = next.ToDictionary(p => p.Key, p => p.Value.Select(x => text.Substring(x.Start, x.Length)).ToArray());
        if (changed.Count > 0) EstimateSoon(changed);
    }

    /// <summary>
    /// Lays out every line with pictures, also those out of view, so the editor knows how tall they are. It only
    /// learns a line's height by laying it out and takes one row for the others, so without this the scroll bar is
    /// wrong and a jump to a far place (Ctrl+End, a search result) stops short of it by the pictures in between.
    /// </summary>
    /// <param name="lines">Just these lines; none means all of them (the width or the pictures themselves changed).</param>
    private void EstimateSoon(IEnumerable<DocumentLine>? lines = null, bool retry = true)
    {
        if (lines is null) _estimateAll = true;
        else _toEstimate.UnionWith(lines);
        if (_estimatePosted) return;
        _estimatePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _estimatePosted = false;
            if (_probe.Width0 <= 0 || _editor.Document is not { } doc) return;
            var todo = _estimateAll ? _byLine.Keys.ToList() : _toEstimate.Where(_byLine.ContainsKey).ToList();
            _estimateAll = false;
            _toEstimate.Clear();
            // In slices of a few dozen milliseconds, so a page of thousands of pictures does not hold up typing.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var done = 0;
            try
            {
                _estimating = true;
                for (; done < todo.Count && watch.ElapsedMilliseconds < 30; done++)
                    if (!todo[done].IsDeleted && doc.Lines.Contains(todo[done])) _view.GetOrConstructVisualLine(todo[done]);
            }
            catch (InvalidOperationException)
            {
                // In the middle of a layout: try once more after it.
                if (retry) EstimateSoon(todo.Skip(done), retry: false);
                return;
            }
            finally { _estimating = false; }
            if (done < todo.Count) EstimateSoon(todo.Skip(done));
            // The lines laid out here only to be measured hold stand-ins for their pictures: drop them, so the lines
            // in view are laid out again with the real ones.
            if (done > 0) _view.Redraw();
        }, DispatcherPriority.Background);
    }

    private void Redraw(DocumentLine line)
    {
        if (!line.IsDeleted) _view.Redraw(line);
    }

    /// <summary>Draws the lines in view again once pictures have loaded (several loads make one redraw).</summary>
    /// <param name="heightsChanged">A picture whose size was not known came in: lines out of view may have changed height too.</param>
    private void RedrawSoon(bool heightsChanged)
    {
        if (heightsChanged) _heightsChanged = true;
        if (_redrawPosted) return;
        _redrawPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawPosted = false;
            LoadRedraws++;
            _view.Redraw();
            if (_heightsChanged) { _heightsChanged = false; EstimateSoon(); }
        }, DispatcherPriority.Background);
    }

    // ---- the caret around pictures ----

    /// <summary>Whether a caret position is on the row of a line's pictures rather than in its text.</summary>
    internal bool OnPictureRow(TextViewPosition pos)
    {
        try
        {
            var doc = _editor.Document;
            if (doc is null || pos.Line < 1 || pos.Line > doc.LineCount || pos.VisualColumn < 0) return false;
            var line = doc.GetLineByNumber(pos.Line);
            if (!_byLine.ContainsKey(line)) return false;
            var visual = _view.GetOrConstructVisualLine(line);
            if (visual.Elements.OfType<PictureElement>().FirstOrDefault() is not { } element || visual.TextLines.Count < 2) return false;
            var row = visual.GetTextLine(pos.VisualColumn, pos.IsAtEndOfLine);
            return row == visual.TextLines[^1] && visual.GetTextLineVisualStartColumn(row) > element.VisualColumn;
        }
        catch (InvalidOperationException) { return false; } // asked while the view is laying out
    }

    /// <summary>One line up or down, as AvaloniaEdit does it, but over the pictures' rows instead of onto them.</summary>
    private void MoveVertically(bool up, bool extend)
    {
        var area = _editor.TextArea;
        var caret = area.Caret;
        var from = caret.Position;
        var selection = area.Selection;
        (up ? (extend ? EditingCommands.SelectUpByLine : EditingCommands.MoveUpByLine) : (extend ? EditingCommands.SelectDownByLine : EditingCommands.MoveDownByLine)).Execute(null, area);
        var x = caret.DesiredXPos;
        if (double.IsNaN(x)) return;
        var doc = _editor.Document;
        TextViewPosition? target = null;
        try
        {
            var fromLine = doc.GetLineByNumber(from.Line);
            var fromVisual = _view.GetOrConstructVisualLine(fromLine);
            var fromRow = fromVisual.TextLines.IndexOf(fromVisual.GetTextLine(Math.Max(0, from.VisualColumn), from.IsAtEndOfLine));
            if (!up && _byLine.ContainsKey(fromLine) && HasPictureRow(fromVisual) && fromRow == fromVisual.TextLines.Count - 2)
            {
                // From the last row of text over the pictures: to the first row of the next line.
                if (fromLine.NextLine is { } next) target = At(_view.GetOrConstructVisualLine(next), 0, x);
            }
            else if (up && fromRow == 0 && fromLine.PreviousLine is { } previous && _byLine.ContainsKey(previous))
            {
                // From the top row of a line up past the pictures of the line above: to that line's last row of text.
                var visual = _view.GetOrConstructVisualLine(previous);
                if (HasPictureRow(visual)) target = At(visual, visual.TextLines.Count - 2, x);
            }
        }
        catch (InvalidOperationException) { return; }
        if (target is not { } to) return;
        if (extend) area.Selection = selection.StartSelectionOrSetEndpoint(from, to);
        else area.ClearSelection();
        caret.Position = to;
        caret.DesiredXPos = x;
        caret.BringCaretToView();
    }

    private static bool HasPictureRow(VisualLine visual) =>
        visual.TextLines.Count >= 2 && visual.Elements.OfType<PictureElement>().FirstOrDefault() is { } element
        && visual.GetTextLineVisualStartColumn(visual.TextLines[^1]) > element.VisualColumn;

    /// <summary>The caret position in a row of a line nearest to a distance across, never past the row's text.</summary>
    private static TextViewPosition At(VisualLine visual, int row, double x)
    {
        var line = visual.TextLines[row];
        var start = visual.GetTextLineVisualStartColumn(line);
        var y = visual.GetTextLineVisualYPosition(line, VisualYPosition.LineMiddle);
        var column = visual.GetVisualColumn(new Point(x, y));
        // Not past the row's end (that is the start of the next row), nor onto the space before the pictures.
        var last = start + line.Length - 1;
        if (visual.Elements.OfType<PictureElement>().FirstOrDefault() is { } element && element.VisualColumn >= start && element.VisualColumn <= last) last = element.VisualColumn;
        else if (row == visual.TextLines.Count - 1) last = visual.VisualLength;
        var position = visual.GetTextViewPosition(Math.Clamp(column, start, Math.Max(start, last)));
        position.IsAtEndOfLine = false;
        return position;
    }

    /// <summary>A command made of two functions.</summary>
    private sealed class Act(Func<bool> can, Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => can();
        public void Execute(object? parameter) => run();
    }

    private void ToTextEnd(TextViewPosition pos)
    {
        var line = _editor.Document.GetLineByNumber(pos.Line);
        var caret = _editor.TextArea.Caret;
        // The visual column left empty means "from the offset", which is the end of the text, before the picture.
        caret.Position = new TextViewPosition(pos.Line, line.Length + 1);
    }

    private void Select(DocumentLine line, int index)
    {
        WikiPicture? picture = null;
        if (ParsePending)
        {
            // The offsets found are from before the last keystrokes: search again now, and take the same markdown
            // nearest to where it was.
            var source = _sources.TryGetValue(line, out var s) && index < s.Length ? s[index] : null;
            var near = line.IsDeleted ? 0 : line.Offset;
            ParseNow();
            var text = _editor.Document.Text;
            picture = _byLine.Values.SelectMany(p => p).Where(p => text.Substring(p.Start, p.Length) == source)
                .OrderBy(p => Math.Abs(p.Start - near)).FirstOrDefault();
        }
        else if (!line.IsDeleted && _byLine.TryGetValue(line, out var list) && index < list.Count) picture = list[index];
        if (picture is null) return;
        _editor.Focus();
        _editor.Select(picture.Start, picture.Length);
    }

    // ---- drawing ----

    /// <summary>A layer that draws nothing: it is measured with the width the text is wrapped to, before the lines are built.</summary>
    private sealed class WidthProbe(Action widthChanged) : Control
    {
        public double Width0 { get; private set; }
        protected override Size MeasureOverride(Size availableSize)
        {
            if (!double.IsInfinity(availableSize.Width) && Math.Abs(availableSize.Width - Width0) > 0.5)
            {
                Width0 = availableSize.Width;
                widthChanged(); // pictures out of view change height with the width too
            }
            return default;
        }
    }

    private double WrapWidth()
    {
        var width = _probe.Width0 > 0 ? _probe.Width0 : _view.Bounds.Width;
        return Math.Max(80, width);
    }

    private sealed class Generator(EditorPictures owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            return owner._byLine.ContainsKey(line) && startOffset <= line.EndOffset && owner.StillHolds(line) ? line.EndOffset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            if (offset != line.EndOffset || !owner._byLine.TryGetValue(line, out var pictures)) return null;
            return new PictureElement(owner._estimating ? owner.StandIn(line, pictures) : owner.BuildRow(line, pictures));
        }
    }

    /// <summary>
    /// The pictures of one line: a space and then the pictures, standing for no text. The space gives the text a
    /// place to wrap before the pictures and keeps the end of the text apart from the start of the pictures' row,
    /// so the caret at the end of the line is drawn after the text.
    /// </summary>
    internal sealed class PictureElement(Control row) : VisualLineElement(2, 0)
    {
        public Control Row { get; } = row;

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            startVisualColumn == VisualColumn
                ? new TextCharacters(" ", TextRunProperties)
                : new InlineObjectRun(1, TextRunProperties, Row);

        // No caret stops of its own, and none at the end of the line after it: the text's end is the line's end.
        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) => -1;
        public override bool HandlesLineBorders => true;
    }

    /// <summary>
    /// How wide a line's pictures row is: as wide as a wrapped row, so the pictures always start a row of their own
    /// (wrapped rows of an indented line are indented the same; see TextEditorOptions.InheritWordWrapIndentation).
    /// </summary>
    private double RowWidth(DocumentLine line)
    {
        var indent = 0;
        var text = _editor.Document.GetText(line.Offset, Math.Min(line.Length, 200));
        foreach (var c in text) { if (c == ' ') indent++; else if (c == '\t') indent += _editor.Options.IndentationSize; else break; }
        return Math.Max(60, WrapWidth() - (_editor.Options.InheritWordWrapIndentation ? indent * _view.WideSpaceWidth : 0) - 2);
    }

    /// <summary>
    /// A plain box as big as a line's pictures row will be, for measuring lines out of view: no controls per picture
    /// and nothing loaded. The sizes come from what is loaded already or from the files' headers.
    /// </summary>
    private Control StandIn(DocumentLine line, List<WikiPicture> pictures)
    {
        var width = RowWidth(line);
        double x = 0, rowHeight = 0, height = 0;
        foreach (var picture in pictures)
        {
            var name = NameOf(picture.Url);
            var source = WikiPictures.Resolve(picture.Url, _folder() ?? "");
            var prepared = source is null ? null : Prepare(source, name);
            PixelSize? pixels = prepared is null || prepared.Problem is not null ? null
                : Cached(prepared.Key) is { IsCompletedSuccessfully: true } t ? (t.Result.Bitmap is null ? null : t.Result.Pixels) : prepared.Pixels;
            var tile = pixels is { } p ? DisplaySize(picture, p, width) + new Size(2, 2) : new Size(Math.Min(width, 260), ChipHeight);
            tile += new Size(8, 4); // its margin
            if (x > 0 && x + tile.Width > width) { height += rowHeight; x = 0; rowHeight = 0; }
            x += tile.Width;
            rowHeight = Math.Max(rowHeight, tile.Height);
        }
        return new Border { Width = width, Height = height + rowHeight + 12 };
    }

    /// <summary>Height of a chip ("Not found: …"), measured once.</summary>
    private static double ChipHeight
    {
        get
        {
            if (_chipHeight > 0) return _chipHeight;
            var chip = Chip(Icons.Image, "Not found", "", warn: false);
            chip.Measure(Size.Infinity);
            return _chipHeight = chip.DesiredSize.Height;
        }
    }
    private static double _chipHeight;

    private Control BuildRow(DocumentLine line, List<WikiPicture> pictures)
    {
        var width = RowWidth(line);
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < pictures.Count; i++) panel.Children.Add(Tile(line, i, pictures[i], width));
        return new Border { Width = width, Padding = new Thickness(0, 4, 0, 8), Child = panel };
    }

    private Control Tile(DocumentLine line, int index, WikiPicture picture, double width)
    {
        var name = NameOf(picture.Url);
        Control tile;
        var source = WikiPictures.Resolve(picture.Url, _folder() ?? "");
        var prepared = source is null ? null : Prepare(source, name);
        if (source is null) tile = Chip(Icons.Image, "Cannot show " + name, "Only pictures inside the wiki, on the web or written into the page can be shown.", warn: true);
        else if (prepared is null) tile = Chip(Icons.Image, "Not found: " + name, picture.Url, warn: true);
        else if (prepared.Problem is { } problem) tile = Chip(Icons.Image, problem, picture.Url, warn: true);
        else if (Cached(prepared.Key) is { IsCompleted: true } done)
        {
            if (done.Result.Bitmap is { } bitmap)
            {
                var size = DisplaySize(picture, done.Result.Pixels, width);
                tile = new Border
                {
                    BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
                    Margin = new Thickness(0, 0, 8, 4), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image { Source = bitmap, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill },
                };
                ToolTip.SetTip(tile, (picture.Alt.Length > 0 ? picture.Alt + "\n" : "") + picture.Url + $"\n{done.Result.Pixels.Width} × {done.Result.Pixels.Height}" + "\nClick to select its markdown.");
            }
            else tile = Chip(Icons.Image, done.Result.Problem ?? "Cannot show " + name, picture.Url, warn: true);
        }
        else
        {
            // Not loaded yet. Lines laid out only to learn their height (out of view) do not load anything: a page
            // of thousands of pictures loads just the ones in view.
            if (!_estimating)
            {
                var sizeKnown = prepared.Pixels is not null;
                Load(prepared).ContinueWith(_ => RedrawSoon(heightsChanged: !sizeKnown), TaskScheduler.Default);
            }
            if (prepared.Pixels is { } pixels)
            {
                // Its size is known from the file's header: hold its place, so nothing moves when it arrives.
                var size = DisplaySize(picture, pixels, width);
                tile = new Border
                {
                    Width = size.Width + 2, Height = size.Height + 2, Margin = new Thickness(0, 0, 8, 4), HorizontalAlignment = HorizontalAlignment.Left,
                    Background = Ui.Tk("panel"), BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
                    Child = new TextBlock { Text = "Loading " + name + "…", FontSize = 11, Foreground = Ui.Dim, Margin = new Thickness(6, 4), TextTrimming = TextTrimming.CharacterEllipsis },
                };
            }
            else tile = Chip(Icons.Image, "Loading " + name + "…", picture.Url, warn: false);
        }
        tile.Cursor = new Cursor(StandardCursorType.Hand);
        tile.Tag = picture;
        tile.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Select(line, index);
            if (e.GetCurrentPoint(tile).Properties.IsRightButtonPressed) _editor.TextArea.ContextMenu?.Open(_editor.TextArea);
        };
        return tile;
    }

    /// <summary>
    /// The size a picture is drawn at: its own size (the bitmap may have been made smaller than that), or the size
    /// an &lt;img&gt; tag gives it, as the preview does (one of width and height keeps the picture's shape); then made
    /// smaller to fit the height limit and the editor's width.
    /// </summary>
    private Size DisplaySize(WikiPicture picture, PixelSize pixels, double width)
    {
        var size = new Size(pixels.Width, pixels.Height);
        if (picture.Width is { } w && picture.Height is { } ht) size = new Size(w, ht);
        else if (picture.Width is { } w1) size = new Size(w1, size.Height * w1 / Math.Max(1, size.Width));
        else if (picture.Height is { } h1) size = new Size(size.Width * h1 / Math.Max(1, size.Height), h1);
        var scale = Math.Min(1, Math.Min(Math.Max(40, _current.MaxHeight) / Math.Max(1, size.Height), (width - 4) / Math.Max(1, size.Width)));
        return new Size(Math.Max(1, size.Width * scale), Math.Max(1, size.Height * scale));
    }

    private static string NameOf(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "picture written into the page";
        var path = url.Split('?', '#')[0].TrimEnd('/');
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        try { name = Uri.UnescapeDataString(name); } catch { }
        return name.Length == 0 ? url : name;
    }

    private static Control Chip(Geometry icon, string text, string tip, bool warn)
    {
        var chip = new Border
        {
            Background = Ui.Tk("panel"), BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 8, 4), HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Row(6,
                new PathIcon { Data = icon, Width = 12, Height = 12, Foreground = warn ? Ui.Tk("warn") : Ui.Dim },
                new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center }),
        };
        ToolTip.SetTip(chip, tip);
        return chip;
    }

    // ---- loading ----

    /// <summary>A decoded picture, or why there is none. <see cref="Pixels"/> is its own size, before any shrinking.</summary>
    public sealed record Loaded(Bitmap? Bitmap, PixelSize Pixels, string? Problem);

    /// <summary>A picture ready to load: its cache key, its size if the file's header tells it, and how to load it.</summary>
    private sealed record Prepared(string Key, PixelSize? Pixels, Func<Task<Loaded>> Load, string? Problem = null);

    private sealed class Entry(Task<Loaded> task)
    {
        public Task<Loaded> Task { get; } = task;
        public long Bytes;
        public long Used;
    }

    /// <summary>Decoded pictures, shared by every editor, kept within <see cref="CacheBudget"/> bytes; the least recently drawn go first.</summary>
    private static readonly ConcurrentDictionary<string, Entry> Cache = new();
    private static long _cacheBytes, _clock;
    private const long CacheBudget = 256L * 1024 * 1024;
    /// <summary>Picture sizes from file headers, by file version: small, so kept for every file seen.</summary>
    private static readonly ConcurrentDictionary<string, PixelSize?> Sizes = new();
    /// <summary>At most this many pictures decode at once, so a page of thousands does not take every core.</summary>
    private static readonly SemaphoreSlim Decoding = new(Math.Max(2, Environment.ProcessorCount / 2));
    /// <summary>Pictures are kept no larger than this many pixels high or wide; the editor shows them much smaller.</summary>
    private const int MaxPixels = 1600;
    private const long MaxRemoteBytes = 25L * 1024 * 1024;
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("avamux-wiki");
        return client;
    });

    /// <summary>What it takes to load a picture; null when it is a wiki file that does not exist.</summary>
    private Prepared? Prepare(PictureSource source, string name)
    {
        if (source.Data is { } data)
        {
            var size = PictureHeader.Read(data) is var (w, h) ? new PixelSize(w, h) : (PixelSize?)null;
            return new Prepared($"data:{data.Length}:{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data))}", size, () => DecodeAsync(() => data, name, size));
        }
        if (source.Remote is { } url)
        {
            var known = Cache.TryGetValue(url, out var e) && e.Task.IsCompletedSuccessfully ? e.Task.Result.Pixels : (PixelSize?)null;
            return new Prepared(url, known, async () =>
            {
                try
                {
                    using var response = await Http.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode) return new Loaded(null, default, $"Cannot load {name}: {(int)response.StatusCode} {response.ReasonPhrase}");
                    if (response.Content.Headers.ContentLength > MaxRemoteBytes) return new Loaded(null, default, name + " is too big to show here");
                    if (response.Content.Headers.ContentType?.MediaType is "image/svg+xml") return new Loaded(null, default, name + ": SVG pictures show in the preview only");
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    return await DecodeAsync(() => bytes, name, null);
                }
                catch (Exception ex) { return new Loaded(null, default, $"Cannot load {name}: {ex.GetBaseException().Message}"); }
            });
        }
        if (source.WikiPath is not { } path || _fullPath(path) is not { } full) return null;
        FileInfo info;
        try { info = new FileInfo(full); if (!info.Exists) return null; } catch { return null; }
        var key = $"{full}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        if (info.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return new Prepared(key, null, null!, info.Name + ": SVG pictures show in the preview only");
        if (_current.BigBytes > 0 && info.Length > _current.BigBytes)
            return new Prepared(key, null, null!, $"{info.Name} ({info.Length / (1024.0 * 1024):0.#} MB) is too big to show here");
        if (Sizes.Count > 200_000) Sizes.Clear();
        var pixels = Sizes.GetOrAdd(key, _ => PictureHeader.Read(full) is var (w2, h2) ? new PixelSize(w2, h2) : null);
        return new Prepared(key, pixels, () => DecodeAsync(() => File.ReadAllBytes(full), info.Name, pixels));
    }

    /// <summary>The cached load of a picture, if one was started (and notes that it is in use).</summary>
    private static Task<Loaded>? Cached(string key)
    {
        if (!Cache.TryGetValue(key, out var entry)) return null;
        entry.Used = Interlocked.Increment(ref _clock);
        return entry.Task;
    }

    /// <summary>Loads a picture once; later calls share the same load.</summary>
    private static Task<Loaded> Load(Prepared prepared)
    {
        if (Cached(prepared.Key) is { } have) return have;
        var entry = Cache.GetOrAdd(prepared.Key, _ => new Entry(prepared.Load()));
        entry.Used = Interlocked.Increment(ref _clock);
        entry.Task.ContinueWith(t =>
        {
            if (t.Result.Bitmap is not { } b) return;
            entry.Bytes = (long)b.PixelSize.Width * b.PixelSize.Height * 4;
            if (Interlocked.Add(ref _cacheBytes, entry.Bytes) > CacheBudget) Evict();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
        return entry.Task;
    }

    /// <summary>Forgets the least recently drawn pictures until the cache is within its budget again.</summary>
    private static void Evict()
    {
        lock (Cache)
        {
            foreach (var (key, entry) in Cache.Where(p => p.Value.Task.IsCompleted).OrderBy(p => p.Value.Used).ToList())
            {
                if (Interlocked.Read(ref _cacheBytes) <= CacheBudget * 3 / 4) break;
                if (Cache.TryRemove(key, out _)) Interlocked.Add(ref _cacheBytes, -entry.Bytes);
            }
        }
    }

    private static Task<Loaded> DecodeAsync(Func<byte[]> read, string name, PixelSize? size) => Task.Run(async () =>
    {
        await Decoding.WaitAsync();
        try
        {
            byte[] bytes;
            try { bytes = read(); }
            catch (Exception ex) { return new Loaded(null, default, $"Cannot read {name}: {ex.GetBaseException().Message}"); }
            return Decode(bytes, name, size);
        }
        finally { Decoding.Release(); }
    });

    /// <summary>
    /// Decodes a picture, making big ones smaller: the editor never shows them near their full size. When the size
    /// is known beforehand a big picture is decoded straight to the smaller size, which is quicker and never holds
    /// the full picture in memory.
    /// </summary>
    internal static Loaded Decode(byte[] bytes, string name, PixelSize? size = null)
    {
        Interlocked.Increment(ref Decodes);
        try
        {
            using var stream = new MemoryStream(bytes);
            if (size is { } known && Math.Max(known.Width, known.Height) > MaxPixels)
            {
                var shrunk = known.Width >= known.Height
                    ? Bitmap.DecodeToWidth(stream, MaxPixels, BitmapInterpolationMode.MediumQuality)
                    : Bitmap.DecodeToHeight(stream, MaxPixels, BitmapInterpolationMode.MediumQuality);
                return new Loaded(shrunk, known, null);
            }
            var bitmap = new Bitmap(stream);
            var pixels = bitmap.PixelSize;
            var factor = Math.Min(1, (double)MaxPixels / Math.Max(pixels.Width, pixels.Height));
            if (factor < 1)
            {
                var smaller = bitmap.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(pixels.Width * factor)), Math.Max(1, (int)(pixels.Height * factor))), BitmapInterpolationMode.HighQuality);
                bitmap.Dispose();
                bitmap = smaller;
            }
            return new Loaded(bitmap, pixels, null);
        }
        catch (Exception) { return new Loaded(null, default, $"{name} is not a picture avamux can draw"); }
    }
}
