using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Avamux.Core.Interactive;
using Avamux.Core.Text;
using Avamux.Views;

namespace Avamux.Scripting;

/// <summary>
/// What code typed into C# Interactive can name directly, on top of <see cref="ReplGlobals"/>: the screen beside
/// the terminal and helpers to put controls on it.
/// </summary>
public sealed class InteractiveGlobals(AvamuxApi api, InteractivePage page) : ReplGlobals(api)
{
    /// <summary>The panel on the right that controls made in C# are added to.</summary>
    public StackPanel Screen => page.Screen;

    /// <summary>The root control built from the XAML tab's document. Null when that document is empty or does not load.</summary>
    public Control? XamlView => page.XamlView;

    /// <summary>Adds a control to the screen and returns it: <c>var b = Add(new Button { Content = "Go" });</c></summary>
    public T Add<T>(T control) where T : Control
    {
        // A lone control should not be stretched across the screen unless it was asked to be.
        if (!control.IsSet(Layoutable.HorizontalAlignmentProperty) && control is not (Panel or Border or TextBlock or ItemsControl or TextBox))
            control.HorizontalAlignment = HorizontalAlignment.Left;
        page.Screen.Children.Add(control);
        page.ShowScreen();
        return control;
    }

    /// <summary>Builds a control from XAML text (not yet on the screen): <c>Add(Xaml("&lt;Button Content='Hi'/&gt;"))</c>. The Avalonia, x: and i: namespaces are added when left out.</summary>
    public Control Xaml(string xaml) => InteractivePage.LoadXaml(xaml);

    /// <summary>The control with this Name, on the screen or in the XAML view. Null when there is none.</summary>
    public T? Find<T>(string name) where T : Control => page.FindNamed(name) as T;
    public Control? Find(string name) => page.FindNamed(name);

    /// <summary>Takes every control made in C# off the screen.</summary>
    public void ClearScreen() => page.Screen.Children.Clear();

    protected override string HelpText => base.HelpText + "\n\n" + """
        The screen on the right:
          Screen                      the panel that controls made here are added to
          Add(control)                put a control on it; returns the control
          Xaml("<Button …/>")         build a control from XAML text
          Find<Button>("name")        a control by its Name (XAML tab's document too)
          XamlView                    the root of the XAML tab's document
          ClearScreen()               remove the controls made in C#
          i:On.Click='Print("hi")'    in XAML: run C# from this session on a click
        """;
}

/// <summary>
/// For XAML shown in C# Interactive: <c>i:On.Click='Print("clicked")'</c> runs the C# in the page's session when
/// the control (a button, or anything with buttons inside) is clicked.
/// </summary>
public sealed class On
{
    public static readonly AttachedProperty<string?> ClickProperty = AvaloniaProperty.RegisterAttached<On, Interactive, string?>("Click");
    private static readonly AttachedProperty<EventHandler<RoutedEventArgs>?> HandlerProperty = AvaloniaProperty.RegisterAttached<On, Interactive, EventHandler<RoutedEventArgs>?>("Handler");

    /// <summary>Runs a piece of C#; set by the page.</summary>
    internal static Action<string>? Runner;

    static On()
    {
        ClickProperty.Changed.AddClassHandler<Interactive>((target, e) =>
        {
            if (target.GetValue(HandlerProperty) is { } old) target.RemoveHandler(Button.ClickEvent, old);
            target.SetValue(HandlerProperty, null);
            if (e.NewValue is not string { Length: > 0 } code) return;
            EventHandler<RoutedEventArgs> handler = (_, _) => Runner?.Invoke(code);
            target.SetValue(HandlerProperty, handler);
            target.AddHandler(Button.ClickEvent, handler);
        });
    }

    public static string? GetClick(Interactive target) => target.GetValue(ClickProperty);
    public static void SetClick(Interactive target, string? value) => target.SetValue(ClickProperty, value);
}

/// <summary>
/// C# Interactive: a terminal on the left that runs C# inside the running avamux, and on the right a screen of
/// live controls, built from a XAML document and from whatever the C# adds.
/// </summary>
public sealed class InteractivePage : UserControl, IPage
{
    private const string Namespaces = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:i=\"clr-namespace:Avamux.Scripting;assembly=avamux\"";

    public const string DefaultXaml = """
        <StackPanel Spacing="10">
          <TextBlock Classes="h2" Text="From the XAML tab" />
          <TextBlock Classes="dim" TextWrapping="Wrap"
                     Text="These controls come from the document in the XAML tab. i:On.Click runs C# in the session on the left." />
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Content="Say hello" i:On.Click='Print("Hello from a XAML button")' />
            <Button Content="Alert avamux" i:On.Click='avamux.Notify("C# Interactive", "A XAML button was clicked")' />
          </StackPanel>
          <StackPanel Orientation="Horizontal" Spacing="8">
            <TextBox Name="NameBox" Width="200" PlaceholderText="Your name" />
            <Button Content="Greet" i:On.Click='Print($"Hello, {Find&lt;TextBox&gt;("NameBox")!.Text}!")' />
          </StackPanel>
        </StackPanel>
        """;

    /// <summary>Run when the page opens and after Reset, one submission each, so the screen starts with working examples.</summary>
    private static readonly string[] Examples =
    [
        """
        // A button made in C#. Its handler writes to this terminal.
        var hello = Add(new Button { Content = "Print a message" });
        hello.Click += (_, _) => Print("Hello from a C# button");
        """,
        """
        // A button that alerts avamux.
        var alert = Add(new Button { Content = "Alert avamux" });
        alert.Click += (_, _) => avamux.Notify("C# Interactive", "The button was clicked");
        """,
        """
        // An async handler: it waits without freezing the window, then asks avamux a question.
        var count = Add(new Button { Content = "Count terminals in 2 seconds" });
        count.Click += async (_, _) =>
        {
            Print("Counting…");
            await Task.Delay(2000);
            var terminals = await avamux.Command("terminal list");
            Print($"{terminals!.AsArray().Count} terminal(s) open");
        };
        """,
        """
        // A slider and a label that follows it.
        var slider = Add(new Slider { Minimum = 0, Maximum = 100, Value = 40, Width = 240 });
        var label = Add(new TextBlock { Text = "40" });
        slider.ValueChanged += (_, e) => label.Text = $"{e.NewValue:0}";
        """,
    ];

    private static InteractivePage? _current;
    private static ConsoleTee? _tee;

    private readonly ReplSession _session;
    private readonly StackPanel _lines = new() { Margin = new Thickness(12, 8, 12, 8), Spacing = 3 };
    private readonly ScrollViewer _scroll;
    private readonly StringBuilder _transcript = new();
    private readonly TextEditor _input = NewEditor();
    private readonly TextEditor _xaml = NewEditor();
    private readonly SyntaxColorizer _inputColors = new() { Language = "csharp" }, _xamlColors = new() { Language = "xml" };
    private readonly TextBlock _prompt = new() { Text = ">", FontFamily = Ui.Mono, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Ui.Tk("accent"), Margin = new Thickness(12, 9, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _xamlError = new() { Foreground = Ui.Tk("error"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(12, 6), IsVisible = false };
    private readonly ContentControl _xamlHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _stop;
    private readonly Button[] _tabs;
    private readonly Control _screenView, _xamlView;
    private readonly List<string> _history = [];
    private int _historyAt;
    private string _draft = "";

    public InteractivePage()
    {
        _session = new ReplSession(new InteractiveGlobals(new AvamuxApi(App.Services), this),
            imports: ["Avalonia", "Avalonia.Controls", "Avalonia.Controls.Primitives", "Avalonia.Input", "Avalonia.Interactivity", "Avalonia.Layout", "Avalonia.Media", "Avalonia.Threading"]);
        _session.Output += (kind, text) => OnUi(() => Append(kind, text));
        _session.Cleared += () => OnUi(ClearTerminal);
        _session.BusyChanged += () => OnUi(() => { _stop!.IsVisible = _session.Busy; _prompt.Opacity = _session.Busy ? 0.35 : 1; });

        _current = this;
        On.Runner = code => _ = _session.RunAsync(code);
        if (_tee is null) Console.SetOut(_tee = new ConsoleTee(Console.Out));

        // ---- left: the terminal ---------------------------------------------------------------------------
        _scroll = new ScrollViewer { Content = _lines, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _input.TextArea.TextView.LineTransformers.Add(_inputColors);
        _input.TextChanged += (_, _) => _inputColors.Invalidate();
        _input.TextArea.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        _input.MaxHeight = 260;
        var inputRow = new Border
        {
            BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 1, 0, 0), Background = Ui.Tk("bgEditor"),
            Child = new DockPanel { Children = { _prompt.Dock(Dock.Left), _input } },
        };
        var hint = Ui.Apply(Ui.Text("Enter runs (or continues an unfinished statement) · Shift+Enter new line · Ctrl+Enter always runs · Up/Down history · Help()", "dim", 11),
            t => { t.Margin = new Thickness(12, 4, 12, 6); t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; });
        var terminal = new Border
        {
            Background = Ui.Tk("bgEditor"),
            Child = new DockPanel { Children = { hint.Dock(Dock.Bottom), inputRow.Dock(Dock.Bottom), _scroll } },
        };
        // Clicking the empty part of the terminal puts the caret in the input, as in a real one.
        _scroll.PointerReleased += (_, e) => { if (e.Source is not SelectableTextBlock) _input.Focus(); };

        // ---- right: the screen and its XAML -----------------------------------------------------------------
        Screen = new StackPanel { Spacing = 10 };
        _screenView = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18, 14), Spacing = 14,
                Children = { _xamlHost, new Border { Height = 1, Background = Ui.Tk("lineSoft") }, Ui.Text("From C#", "h2"), Screen },
            },
        };
        _xaml.Text = DefaultXaml;
        _xaml.TextArea.TextView.LineTransformers.Add(_xamlColors);
        _xaml.TextChanged += (_, _) => _xamlColors.Invalidate();
        _xaml.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; ApplyXaml(show: true); }
        }, RoutingStrategies.Tunnel);
        var xamlBar = new DockPanel
        {
            Margin = new Thickness(10, 6),
            Children =
            {
                Ui.Row(6, Ui.TextButton("Apply", () => ApplyXaml(show: true), "Build the screen from this XAML (Ctrl+Enter)"),
                    Ui.TextButton("Defaults", () => { _xaml.Text = DefaultXaml; ApplyXaml(show: true); }, "Put the example XAML back")).Dock(Dock.Right),
                Ui.Apply(Ui.Text("i:On.Click='…' runs C# from the session when the control is clicked.", "dim", 11.5), t => t.VerticalAlignment = VerticalAlignment.Center),
            },
        };
        _xamlView = new DockPanel { IsVisible = false, Children = { xamlBar.Dock(Dock.Bottom), _xamlError.Dock(Dock.Bottom), _xaml } };
        _tabs = new[] { "Screen", "XAML" }.Select((label, i) =>
        {
            var b = new Button { Content = label, Classes = { "tab" } };
            b.Click += (_, _) => ShowTab(i);
            return b;
        }).ToArray();
        var right = new DockPanel
        {
            Children =
            {
                new Border { BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(0, _tabs) }.Dock(Dock.Top),
                new Panel { Children = { _screenView, _xamlView } },
            },
        };

        _stop = Ui.IconButton(Icons.Stop, "Stop waiting for the running code and cancel its Cancel token", _session.Stop, "Stop");
        _stop.IsVisible = false;
        var header = Ui.Header("C# Interactive", "Run C# inside the running avamux, with a screen of live controls beside it.",
            _stop,
            Ui.IconButton(Icons.ContentCopy, "Copy everything in the terminal", () => _ = App.Services.Host.SetClipboardAsync(_transcript.ToString()), "Copy"),
            Ui.IconButton(Icons.Close, "Empty the terminal (Ctrl+L)", ClearTerminal, "Clear"),
            Ui.IconButton(Icons.Refresh, "Forget every variable, empty the screen and run the examples again", Reset, "Reset"));

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,5,2*") };
        split.ColumnDefinitions[0].MinWidth = 260;
        split.ColumnDefinitions[2].MinWidth = 200;
        split.Children.Add(terminal);
        split.Children.Add(new GridSplitter { Background = Ui.Line, ResizeDirection = GridResizeDirection.Columns }.At(0, 1));
        split.Children.Add(right.At(0, 2));
        Content = new DockPanel { Children = { header.Dock(Dock.Top), new Border { BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 1, 0, 0), Child = split } } };

        Ui.ThemeChanged += () => { _input.TextArea.TextView.Redraw(); _xaml.TextArea.TextView.Redraw(); };
        ShowTab(0);
        Start();
    }

    public StackPanel Screen { get; }
    public Control? XamlView => _xamlHost.Content as Control;

    public void OnShown() => Dispatcher.UIThread.Post(() => { if (_xamlView.IsVisible) _xaml.Focus(); else _input.Focus(); }, DispatcherPriority.Background);

    internal void ShowScreen() { if (!_screenView.IsVisible) ShowTab(0); }

    internal Control? FindNamed(string name) =>
        new Control?[] { Screen, XamlView }.Where(c => c is not null)
            .SelectMany(root => new[] { root! }.Concat(root!.GetLogicalDescendantsOfType()))
            .FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// An exception that escaped code typed here (an event handler, say) belongs in the terminal rather than in a
    /// crash report. True when it was one of ours and has been shown.
    /// </summary>
    public static bool TryReport(Exception ex)
    {
        if (_current is not { } page || ex.ToString().Contains("Submission#", StringComparison.Ordinal) != true) return false;
        page.Append(ReplOutputKind.Error, ReplSession.Describe(ex));
        return true;
    }

    private static TextEditor NewEditor()
    {
        var editor = new TextEditor
        {
            FontFamily = Ui.Mono, FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(8, 8, 12, 8),
            Background = Ui.Tk("bgEditor"), Foreground = Ui.Tk("textCode"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        editor.Options.ConvertTabsToSpaces = true;
        return editor;
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action);
    }

    private void ShowTab(int index)
    {
        _screenView.IsVisible = index == 0;
        _xamlView.IsVisible = index == 1;
        for (var i = 0; i < _tabs.Length; i++) _tabs[i].Classes.Set("on", i == index);
        if (index == 1) _xaml.Focus();
    }

    // ---- session ------------------------------------------------------------------------------------------

    private void Start()
    {
        ApplyXaml(show: false);
        Append(ReplOutputKind.Info, "C# Interactive. Code runs inside this avamux, on its UI thread. Help() lists what is here. The examples below built the screen on the right:");
        foreach (var example in Examples) _ = Submit(example, remember: false);
    }

    private void Reset()
    {
        _session.Reset();
        Screen.Children.Clear();
        ClearTerminal();
        Start();
        _input.Focus();
    }

    private Task Submit(string code, bool remember = true)
    {
        code = code.Trim('\r', '\n').TrimEnd();
        if (code.Length == 0) return Task.CompletedTask;
        if (remember && (_history.Count == 0 || _history[^1] != code)) _history.Add(code);
        _historyAt = _history.Count;
        _draft = "";
        AppendInput(code);
        return _session.RunAsync(code);
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Enter when mods.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                _input.TextArea.PerformTextInput("\n");
                break;
            case Key.Enter when mods.HasFlag(KeyModifiers.Control) || (mods == KeyModifiers.None && ReplSession.IsComplete(_input.Text)):
                e.Handled = true;
                var code = _input.Text;
                _input.Text = "";
                _ = Submit(code);
                break;
            case Key.Up when mods == KeyModifiers.None && _input.TextArea.Caret.Line == 1 && _historyAt > 0:
                e.Handled = true;
                if (_historyAt == _history.Count) _draft = _input.Text;
                ShowHistory(_historyAt - 1);
                break;
            case Key.Down when mods == KeyModifiers.None && _input.TextArea.Caret.Line == _input.Document.LineCount && _historyAt < _history.Count:
                e.Handled = true;
                ShowHistory(_historyAt + 1);
                break;
            case Key.L when mods == KeyModifiers.Control:
                e.Handled = true;
                ClearTerminal();
                break;
            case Key.Escape when _input.Text.Length > 0:
                e.Handled = true;
                _input.Text = "";
                _historyAt = _history.Count;
                break;
        }
    }

    private void ShowHistory(int index)
    {
        _historyAt = index;
        _input.Text = index == _history.Count ? _draft : _history[index];
        _input.CaretOffset = _input.Text.Length;
    }

    // ---- terminal output ----------------------------------------------------------------------------------

    private void ClearTerminal()
    {
        _lines.Children.Clear();
        _transcript.Clear();
    }

    private void AppendInput(string code)
    {
        var block = NewBlock();
        var lines = Syntax.Highlight(code.Replace("\r\n", "\n"), "csharp");
        for (var i = 0; i < lines.Count; i++)
        {
            block.Inlines!.Add(new Run(i == 0 ? "> " : "\n. ") { Foreground = Ui.Tk("accent"), FontWeight = FontWeight.Bold });
            foreach (var span in lines[i])
                block.Inlines.Add(new Run(span.Text) { Foreground = Ui.Tk(span.Kind == Tok.Text ? "textCode" : Syntax.ThemeToken(span.Kind)) });
        }
        block.Margin = new Thickness(0, 6, 0, 0);
        AddBlock(block, "> " + code.Replace("\n", "\n. "));
    }

    private void Append(ReplOutputKind kind, string text)
    {
        var block = NewBlock();
        block.Text = text;
        block.Foreground = Ui.Tk(kind switch { ReplOutputKind.Error => "error", ReplOutputKind.Info => "dim", ReplOutputKind.Output => "textSoft", _ => "text" });
        if (kind == ReplOutputKind.Info) block.FontStyle = FontStyle.Italic;
        AddBlock(block, text);
    }

    private static SelectableTextBlock NewBlock() => new() { FontFamily = Ui.Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap, Inlines = [] };

    private void AddBlock(Control block, string text)
    {
        _lines.Children.Add(block);
        while (_lines.Children.Count > 2000) _lines.Children.RemoveAt(0);
        _transcript.Append(text).Append('\n');
        if (_transcript.Length > 2_000_000) _transcript.Remove(0, _transcript.Length - 1_000_000);
        Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    // ---- XAML ---------------------------------------------------------------------------------------------

    /// <summary>Builds a control from XAML text, supplying the usual namespaces when the root element leaves them out.</summary>
    public static Control LoadXaml(string xaml)
    {
        var root = Regex.Match(xaml, @"<(?![?!])[\w:.]+");
        if (!root.Success) throw new ArgumentException("That is not XAML: no element found.");
        var missing = Namespaces.Split(' ').Where(ns => !xaml.Contains(ns[..(ns.IndexOf('=') + 1)], StringComparison.Ordinal));
        xaml = xaml.Insert(root.Index + root.Length, string.Concat(missing.Select(ns => " " + ns)));
        return AvaloniaRuntimeXamlLoader.Parse<Control>(xaml, typeof(InteractivePage).Assembly);
    }

    private void ApplyXaml(bool show)
    {
        try
        {
            _xamlHost.Content = string.IsNullOrWhiteSpace(_xaml.Text) ? null : LoadXaml(_xaml.Text);
            _xamlError.IsVisible = false;
            if (show) ShowTab(0);
        }
        catch (Exception ex)
        {
            _xamlError.Text = ex.GetBaseException().Message;
            _xamlError.IsVisible = true;
            if (!show) Append(ReplOutputKind.Error, "The XAML did not load: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Console output goes where it always went and, as it is most likely code typed here, to the terminal too.</summary>
    private sealed class ConsoleTee(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _pending = new();
        private bool _posted;

        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) { inner.Write(value); Add(value.ToString()); }
        public override void Write(string? value) { inner.Write(value); if (value is not null) Add(value); }
        public override void Write(char[] buffer, int index, int count) { inner.Write(buffer, index, count); Add(new string(buffer, index, count)); }
        public override void Flush() => inner.Flush();

        private void Add(string text)
        {
            lock (_pending)
            {
                _pending.Append(text);
                if (_posted) return;
                _posted = true;
            }
            Dispatcher.UIThread.Post(() =>
            {
                string all;
                lock (_pending) { all = _pending.ToString(); _pending.Clear(); _posted = false; }
                all = all.ReplaceLineEndings("\n").TrimEnd('\n');
                if (all.Length > 0) _current?.Append(ReplOutputKind.Output, all);
            }, DispatcherPriority.Background);
        }
    }
}

internal static class LogicalTreeHelpers
{
    public static IEnumerable<Control> GetLogicalDescendantsOfType(this Control root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Control>();
}
