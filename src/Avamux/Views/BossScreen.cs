using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Avamux.Views;

/// <summary>
/// The boss screen: a nostalgic gimmick from the days of DOS games. One key swaps avamux for something that looks
/// like work (a budget spreadsheet or a long build) and the same key brings it back. Its colours are fixed, not
/// the theme's: it has to look like a different program.
/// </summary>
public static class BossScreen
{
    public static readonly string[] Kinds = ["spreadsheet", "build"];

    public static string TitleFor(string kind) => kind == "build" ? "build — release — bash" : "Q3 Forecast.xlsx — Spreadsheet";

    public static Control Build(string kind) => kind == "build" ? BuildLog() : Spreadsheet();

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Control Spreadsheet()
    {
        var font = new FontFamily("Liberation Sans, Arimo, Arial, Helvetica, DejaVu Sans, sans-serif");
        TextBlock T(string text, double size = 12, string color = "#202020", FontWeight? weight = null) =>
            new() { Text = text, FontSize = size, Foreground = B(color), FontFamily = font, FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };

        var menu = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(10, 4) };
        foreach (var m in new[] { "File", "Edit", "View", "Insert", "Format", "Data", "Tools", "Window", "Help" }) menu.Children.Add(T(m, 12.5));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 3) };
        Border Tool(string text, double width = 24, FontWeight? weight = null) => new()
        {
            Width = width, Height = 22, BorderBrush = B("#c8c8c8"), BorderThickness = new Thickness(1), Background = B("#fafafa"), CornerRadius = new CornerRadius(2),
            Child = Ui.Apply(T(text, 11.5, "#333333", weight), t => t.HorizontalAlignment = HorizontalAlignment.Center),
        };
        tools.Children.Add(Tool("Sans", 110));
        tools.Children.Add(Tool("11", 36));
        tools.Children.Add(Tool("B", 24, FontWeight.Bold));
        tools.Children.Add(Tool("I"));
        tools.Children.Add(Tool("U"));
        tools.Children.Add(new Border { Width = 10 });
        foreach (var t in new[] { "$", "%", ",", ".0", "Σ", "↕" }) tools.Children.Add(Tool(t));

        var formula = new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                new Border { Width = 70, BorderBrush = B("#c8c8c8"), BorderThickness = new Thickness(0, 0, 1, 0), Child = Ui.Apply(T("E18"), t => t.HorizontalAlignment = HorizontalAlignment.Center) }.Dock(Dock.Left),
                new Border { Width = 34, Child = Ui.Apply(T("fx", 12, "#707070", FontWeight.SemiBold), t => t.HorizontalAlignment = HorizontalAlignment.Center) }.Dock(Dock.Left),
                T("=SUM(E5:E17)"),
            },
        };

        // The sheet itself.
        string[] departments = ["Engineering", "Product", "Design", "Sales", "Marketing", "Customer Success", "Support", "Finance", "Legal", "People Ops", "Facilities", "IT & Security", "Data Platform"];
        var rng = new Random(1987);
        var rows = departments.Select(d =>
        {
            var baseline = rng.Next(40, 420) * 1000;
            var months = Enumerable.Range(0, 3).Select(_ => (int)(baseline * (0.9 + rng.NextDouble() * 0.25))).ToArray();
            var budget = (int)(months.Sum() * (0.95 + rng.NextDouble() * 0.1) / 1000) * 1000;
            return (Name: d, Months: months, Total: months.Sum(), Budget: budget);
        }).ToList();
        string Money(long v) => v < 0 ? $"({(-v).ToString("#,0", CultureInfo.InvariantCulture)})" : v.ToString("#,0", CultureInfo.InvariantCulture);

        const int columns = 11, rowCount = 42;
        var widths = new[] { 44.0, 170, 96, 96, 96, 104, 104, 96, 70, 90, 90, 90 };
        var grid = new Grid { Background = B("#ffffff") };
        foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        for (var r = 0; r <= rowCount; r++) grid.RowDefinitions.Add(new RowDefinition(22, GridUnitType.Pixel));
        void Cell(int row, int col, string text, string color = "#202020", bool bold = false, bool right = false, string? background = null, bool topRule = false, bool selected = false, int span = 1)
        {
            var border = new Border
            {
                BorderBrush = selected ? B("#1e6e3c") : B("#dadada"), BorderThickness = selected ? new Thickness(2) : new Thickness(0, topRule ? 1.5 : 0, 1, 1),
                Background = background is null ? null : B(background), Padding = new Thickness(4, 0),
                Child = Ui.Apply(T(text, 12, color, bold ? FontWeight.Bold : null), t => { t.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left; t.TextTrimming = TextTrimming.CharacterEllipsis; }),
            };
            if (topRule) border.BorderBrush = B("#404040");
            grid.Children.Add(border.At(row, col, colSpan: span));
        }
        // Column letters and row numbers.
        Cell(0, 0, "", background: "#efefef");
        for (var c = 1; c <= columns; c++) Cell(0, c, ((char)('A' + c - 1)).ToString(), "#555555", background: c == 5 ? "#d3e3d8" : "#efefef");
        for (var r = 1; r <= rowCount; r++) Cell(r, 0, r.ToString(), "#555555", background: r == 18 ? "#d3e3d8" : "#efefef", right: true);

        // Long text spills over the empty cells to its right, as it would in a real sheet.
        Cell(1, 1, "Operating Budget — Q3 Forecast", bold: true, span: 3);
        Cell(2, 1, "All figures in USD. Draft — not for distribution.", "#707070", span: 3);
        string[] heads = ["Department", "Jul", "Aug", "Sep", "Q3 Total", "Q3 Budget", "Variance", "Var %"];
        for (var i = 0; i < heads.Length; i++) Cell(4, i + 1, heads[i], "#ffffff", bold: true, right: i > 0, background: "#3d6b4f");
        for (var i = 0; i < rows.Count; i++)
        {
            var (name, months, total, budget) = rows[i];
            var r = 5 + i;
            var band = i % 2 == 1 ? "#f4f8f5" : null;
            Cell(r, 1, name, background: band);
            for (var m = 0; m < 3; m++) Cell(r, 2 + m, Money(months[m]), right: true, background: band);
            Cell(r, 5, Money(total), right: true, background: band);
            Cell(r, 6, Money(budget), right: true, background: band);
            var variance = budget - total;
            Cell(r, 7, Money(variance), variance < 0 ? "#c00000" : "#202020", right: true, background: band);
            Cell(r, 8, (variance / (double)budget).ToString("0.0%", CultureInfo.InvariantCulture), variance < 0 ? "#c00000" : "#202020", right: true, background: band);
        }
        var totalRow = 5 + rows.Count;
        Cell(totalRow, 1, "Total", bold: true, topRule: true);
        for (var m = 0; m < 3; m++) Cell(totalRow, 2 + m, Money(rows.Sum(x => (long)x.Months[m])), bold: true, right: true, topRule: true);
        var grand = rows.Sum(x => (long)x.Total);
        var budgetTotal = rows.Sum(x => (long)x.Budget);
        Cell(totalRow, 5, Money(grand), bold: true, right: true, selected: true);
        Cell(totalRow, 6, Money(budgetTotal), bold: true, right: true, topRule: true);
        Cell(totalRow, 7, Money(budgetTotal - grand), budgetTotal - grand < 0 ? "#c00000" : "#202020", bold: true, right: true, topRule: true);
        Cell(totalRow, 8, ((budgetTotal - grand) / (double)budgetTotal).ToString("0.0%", CultureInfo.InvariantCulture), bold: true, right: true, topRule: true);
        Cell(totalRow + 2, 1, "Notes", bold: true);
        Cell(totalRow + 3, 1, "Sep figures are run-rate estimates pending close.", "#707070", span: 3);
        Cell(totalRow + 4, 1, "Headcount assumptions: see Assumptions tab.", "#707070", span: 3);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(50, 0, 0, 0) };
        foreach (var (name, active) in new[] { ("Summary", false), ("Q3 Forecast", true), ("Headcount", false), ("Assumptions", false), ("+", false) })
            tabs.Children.Add(new Border
            {
                Padding = new Thickness(14, 4), Background = active ? B("#ffffff") : B("#e6e6e6"), BorderBrush = B("#c0c0c0"), BorderThickness = new Thickness(0, 0, 1, active ? 2 : 0),
                Child = T(name, 12, active ? "#1e6e3c" : "#404040", active ? FontWeight.SemiBold : null),
            });
        var status = new DockPanel
        {
            Margin = new Thickness(10, 3),
            Children =
            {
                T($"Sum: {Money(grand)}", 12, "#404040").Dock(Dock.Right),
                T("Ready", 12, "#404040"),
            },
        };

        return new DockPanel
        {
            Background = B("#f3f3f3"),
            Children =
            {
                new Border { Background = B("#f3f3f3"), Child = menu }.Dock(Dock.Top),
                new Border { Background = B("#f8f8f8"), BorderBrush = B("#d0d0d0"), BorderThickness = new Thickness(0, 1), Child = tools }.Dock(Dock.Top),
                new Border { Background = B("#ffffff"), BorderBrush = B("#d0d0d0"), BorderThickness = new Thickness(0, 0, 0, 1), Child = formula }.Dock(Dock.Top),
                new Border { Background = B("#e9e9e9"), BorderBrush = B("#d0d0d0"), BorderThickness = new Thickness(0, 1, 0, 0), Child = status }.Dock(Dock.Bottom),
                new Border { Background = B("#e6e6e6"), Child = tabs }.Dock(Dock.Bottom),
                new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = grid, Background = B("#ffffff") },
            },
        };
    }

    /// <summary>A build that never quite finishes: the classic excuse.</summary>
    private static Control BuildLog()
    {
        var mono = Ui.Mono;
        var lines = new StackPanel { Margin = new Thickness(10, 8) };
        var scroll = new ScrollViewer { Content = lines, Background = B("#101010") };
        string[] modules = ["core", "net", "storage", "auth", "billing", "search", "render", "ui", "telemetry", "scheduler", "codec", "sync"];
        string[] files = ["allocator", "buffer_pool", "connection", "hash_map", "index_writer", "lexer", "parser", "planner", "query_cache", "session", "token_bucket", "worker"];
        var rng = new Random(2003);
        var step = 0;
        const int total = 2417;
        void Add(string text, string color = "#c8c8c8")
        {
            lines.Children.Add(new TextBlock { Text = text, FontFamily = mono, FontSize = 12.5, Foreground = B(color) });
            if (lines.Children.Count > 400) lines.Children.RemoveAt(0);
        }
        Add("$ make -j8 release", "#e0e0e0");
        Add("-- Configuring done (4.2s)", "#8fbf8f");
        Add("-- Generating done (0.8s)", "#8fbf8f");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            for (var i = rng.Next(1, 4); i > 0; i--)
            {
                step = step % total + 1;
                var module = modules[rng.Next(modules.Length)];
                var file = files[rng.Next(files.Length)];
                var pct = (int)(step * 100.0 / total);
                var roll = rng.Next(40);
                if (roll == 0) Add($"src/{module}/{file}.cc:{rng.Next(20, 900)}:{rng.Next(1, 60)}: warning: unused variable 'tmp' [-Wunused-variable]", "#d7b55a");
                else if (roll == 1) Add($"[{pct,3}%] Linking CXX static library lib{module}.a", "#7fb3e0");
                else Add($"[{pct,3}%] Building CXX object src/{module}/CMakeFiles/{module}.dir/{file}.cc.o");
            }
            scroll.ScrollToEnd();
        };
        scroll.AttachedToVisualTree += (_, _) => timer.Start();
        scroll.DetachedFromVisualTree += (_, _) => timer.Stop();
        return scroll;
    }
}
