using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avamux.Views;

namespace Avamux.UiTests;

/// <summary>Whether tests may use (and then clear) the clipboard of the desktop session they run in.</summary>
public static class SystemClipboard
{
    public const string Why = "Uses the desktop's clipboard and clears it: opt in with AVAMUX_SYSTEM_CLIPBOARD_TESTS=1 (Linux with wl-clipboard)";

    public static bool On => Environment.GetEnvironmentVariable("AVAMUX_SYSTEM_CLIPBOARD_TESTS") is "1" or "true" && OperatingSystem.IsLinux() && Run("wl-paste", "--version", null) is not null;

    public static string? Run(string exe, string args, string? stdinFile)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdinFile is not null, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            if (stdinFile is not null)
            {
                using (var input = File.OpenRead(stdinFile)) input.CopyTo(p.StandardInput.BaseStream);
                p.StandardInput.Close();
                Thread.Sleep(300); // wl-copy stays behind to serve the clipboard
                return "";
            }
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// The paths through the desktop's own clipboard tools (wl-paste, wl-copy), which the toolkit's clipboard cannot
/// reach on a Wayland session. Opt in: they replace what is on the clipboard and clear it afterwards.
/// </summary>
[Trait("Category", "SystemClipboard")]
public class SystemClipboardTests
{
    [AvaloniaFact(Skip = SystemClipboard.Why, SkipUnless = nameof(SystemClipboard.On), SkipType = typeof(SystemClipboard))]
    public void A_picture_copied_in_another_program_pastes_into_the_wiki_and_shows_in_the_editor()
    {
        using var h = new WikiHarness(w => File.WriteAllText(w.Full("p.md"), "# P\n\n"), open: "p.md");
        var png = Path.Combine(h.Root, "copied.png");
        using (var picture = WikiHarness.Picture(70, 35)) picture.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        ClipboardFiles.UseSystemTools = true; // the harness turns them off
        try
        {
            SystemClipboard.Run("wl-copy", "--type image/png", png);
            h.Editor.CaretOffset = h.Text.Length;
            h.Keys(PhysicalKey.V, RawInputModifiers.Control, settleMs: 500);
            var saved = Assert.Single(Directory.GetFiles(h.Wiki.Root, "Pasted image *.png"));
            Assert.Contains($"<{Path.GetFileName(saved)}>)", h.Text);
            Assert.Equal(35, Assert.Single(h.Images()).Bounds.Height, 1);
        }
        finally
        {
            SystemClipboard.Run("wl-copy", "--clear", null);
        }
    }
}
