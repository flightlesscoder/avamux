using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Avamux.UiTests.TestApp))]

namespace Avamux.UiTests;

/// <summary>A bare application with the styles the editor needs, drawn by Skia so text is shaped and wrapped for real.</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avamux.UiTests/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }
}
