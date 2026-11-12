using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avamux.Core.Logging;
using Avamux.Core.Theming;

namespace Avamux.Views;

/// <summary>Turns the theme settings into what Avalonia needs: brushes, the Fluent palette, a few resources and styles.</summary>
public static class ThemeManager
{
    private static string _applied = "";
    private static Styles? _bevel;

    /// <summary>The font windows use: the theme's own if it names one that is installed, else the default.</summary>
    public static FontFamily UiFont { get; private set; } = FontFamily.Default;

    /// <summary>Registers the token brushes as application resources (Tk.accent, Tk.dim, …) for the XAML styles.</summary>
    public static void Initialize(Application app)
    {
        foreach (var token in Themes.Simple.Colors.Keys) app.Resources["Tk." + token] = Ui.Tk(token);
        Apply(app);
    }

    public static void Apply(Application app)
    {
        var settings = App.Services.Config.Settings.Theme;
        var theme = Themes.Resolve(settings);
        var signature = $"{theme.Id}|{theme["accent"]}|{settings.WindowChrome}";
        if (signature == _applied) return;
        _applied = signature;
        try
        {
            UiFont = PickFont(theme.Font);
            app.RequestedThemeVariant = theme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
            ApplyFluent(app, theme);
            app.Resources["CardRadius"] = new CornerRadius(theme.Radius);
            if (theme.Radius <= 0)
            {
                app.Resources["ControlCornerRadius"] = new CornerRadius(0);
                app.Resources["OverlayCornerRadius"] = new CornerRadius(0);
            }
            else
            {
                app.Resources.Remove("ControlCornerRadius");
                app.Resources.Remove("OverlayCornerRadius");
            }
            if (_bevel is not null) { app.Styles.Remove(_bevel); _bevel = null; }
            if (theme.Bevel) app.Styles.Add(_bevel = BevelStyles());
            Ui.ApplyTheme(theme);
            Log.Info("theme", $"Theme: {theme.Name} (accent {theme["accent"]})");
        }
        catch (Exception ex) { Log.Error("theme", "Could not apply the theme", ex); }
    }

    private static FontFamily PickFont(string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return FontFamily.Default;
        try
        {
            var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in wanted.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (installed.Contains(name)) return new FontFamily(name);
        }
        catch { /* fall through */ }
        return FontFamily.Default;
    }

    private static void ApplyFluent(Application app, ThemeDef theme)
    {
        Color C(string token) => Color.Parse(theme[token]);
        var palette = new ColorPaletteResources { Accent = C("accent"), RegionColor = C("bg"), ErrorText = C("error") };
        if (theme.Light)
        {
            // Light themes here are grey-surfaced: fields are white, chrome is the panel grey.
            palette.AltHigh = C("cell"); palette.AltMedium = C("cell"); palette.AltMediumHigh = C("cell"); palette.AltMediumLow = C("cell"); palette.AltLow = C("cell");
            palette.BaseHigh = C("text"); palette.BaseMediumHigh = C("textSoft"); palette.BaseMedium = C("dim"); palette.BaseMediumLow = C("faint"); palette.BaseLow = C("ghost");
            palette.ChromeMedium = C("panel"); palette.ChromeMediumLow = C("panel"); palette.ChromeLow = C("panel"); palette.ChromeHigh = C("line");
            palette.ChromeAltLow = C("dim"); palette.ListLow = C("cellSel"); palette.ListMedium = C("cellSel");
        }
        var fluent = new FluentTheme();
        fluent.Palettes[theme.Light ? ThemeVariant.Light : ThemeVariant.Dark] = palette;
        if (app.Styles.Count > 0 && app.Styles[0] is FluentTheme) app.Styles[0] = fluent; else app.Styles.Insert(0, fluent);
    }

    /// <summary>Raised edges for buttons and sunken edges for fields: the look of a 1990s desktop.</summary>
    private static Styles BevelStyles()
    {
        var raised = BoxShadows.Parse("inset 1 1 0 0 #ffffff, inset -1 -1 0 0 #404040, inset -2 -2 0 0 #808080");
        var sunken = BoxShadows.Parse("inset 1 1 0 0 #404040, inset -1 -1 0 0 #ffffff, inset 2 2 0 0 #808080");
        Style Presenter(Func<Selector?, Selector> control, BoxShadows shadow, IBrush? background = null)
        {
            var style = new Style(x => control(x).Template().OfType<ContentPresenter>());
            style.Setters.Add(new Setter(ContentPresenter.BoxShadowProperty, shadow));
            style.Setters.Add(new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(0)));
            if (background is not null) style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, background));
            return style;
        }
        var styles = new Styles
        {
            Presenter(x => x.OfType<Button>().Not(y => y.Class("flat")).Not(y => y.Class("link")).Not(y => y.Class("chrome")), raised, Ui.Tk("panel")),
            Presenter(x => x.OfType<Button>().Class(":pressed").Not(y => y.Class("flat")).Not(y => y.Class("link")), sunken, Ui.Tk("panel")),
            Presenter(x => x.OfType<Button>().Class("chrome"), raised, Ui.Tk("panel")),
        };
        var chromeIcon = new Style(x => x.OfType<Button>().Class("chrome").Descendant().OfType<PathIcon>());
        chromeIcon.Setters.Add(new Setter(PathIcon.ForegroundProperty, Brushes.Black));
        styles.Add(chromeIcon);
        var card = new Style(x => x.OfType<Border>().Class("card"));
        card.Setters.Add(new Setter(Border.BoxShadowProperty, raised));
        card.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
        styles.Add(card);
        var field = new Style(x => x.OfType<TextBox>().Template().OfType<Border>().Name("PART_BorderElement"));
        field.Setters.Add(new Setter(Border.BoxShadowProperty, sunken));
        field.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
        styles.Add(field);
        return styles;
    }
}
