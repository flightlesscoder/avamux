using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avamux.Core.Logging;

namespace Avamux.Views;

/// <summary>Pictures and files on the clipboard, with a command-line fallback where the toolkit cannot reach them.</summary>
public static class ClipboardFiles
{
    /// <summary>
    /// Whether the session's own clipboard tools (wl-paste, xclip, wl-copy) may be used when the toolkit's
    /// clipboard has nothing. Tests turn it off, so they only ever see the clipboard they filled themselves.
    /// </summary>
    internal static bool UseSystemTools { get; set; } = true;

    /// <summary>Local paths of files on the clipboard (copied in a file manager), or an empty list.</summary>
    public static async Task<IReadOnlyList<string>> FilesAsync(TopLevel top)
    {
        try
        {
            if (top.Clipboard is { } clip && await clip.TryGetFilesAsync() is { } items)
                return items.Select(i => i.TryGetLocalPath()).Where(p => p is not null && File.Exists(p)).Select(p => p!).ToList();
        }
        catch (Exception ex) { Log.Debug("clipboard", "No files: " + ex.Message); }
        return [];
    }

    /// <summary>The picture on the clipboard as PNG bytes, or null when there is none.</summary>
    public static async Task<byte[]?> PngAsync(TopLevel top)
    {
        try
        {
            if (top.Clipboard is { } clip)
            {
                using var bitmap = await clip.TryGetBitmapAsync();
                if (bitmap is not null)
                {
                    using var ms = new MemoryStream();
                    bitmap.Save(ms, PngBitmapEncoderOptions.Default);
                    if (ms.Length > 0) return ms.ToArray();
                }
            }
        }
        catch (Exception ex) { Log.Debug("clipboard", "No bitmap through the toolkit: " + ex.Message); }
        if (!OperatingSystem.IsLinux() || !UseSystemTools) return null;
        // A Wayland session's clipboard is not always visible to an X11 client; ask the session's own tools.
        return await RunAsync("wl-paste", ["--type", "image/png"]) ?? await RunAsync("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]);
    }

    /// <summary>Puts a picture file on the clipboard as an image. Returns false when nothing worked.</summary>
    public static async Task<bool> CopyImageAsync(TopLevel top, string file)
    {
        try
        {
            if (top.Clipboard is { } clip && Path.GetExtension(file).ToLowerInvariant() is not ".svg")
            {
                using var bitmap = new Bitmap(file);
                await clip.SetBitmapAsync(bitmap);
                return true;
            }
        }
        catch (Exception ex) { Log.Debug("clipboard", "Could not set a bitmap through the toolkit: " + ex.Message); }
        if (OperatingSystem.IsLinux() && UseSystemTools)
        {
            var mime = Path.GetExtension(file).ToLowerInvariant() switch { ".svg" => "image/svg+xml", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png" };
            if (await FeedAsync("wl-copy", ["--type", mime], file) || await FeedAsync("xclip", ["-selection", "clipboard", "-t", mime, "-i"], file)) return true;
        }
        try
        {
            // Last resort: the file itself, which file managers and many editors accept.
            if (top.Clipboard is { } clip && await top.StorageProvider.TryGetFileFromPathAsync(file) is { } item) { await clip.SetFileAsync(item); return true; }
        }
        catch (Exception ex) { Log.Debug("clipboard", "Could not set a file: " + ex.Message); }
        return false;
    }

    private static async Task<byte[]?> RunAsync(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await p.WaitForExitAsync(cts.Token); await copy; }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
            // A PNG starts with these bytes; anything else is an error message or another format.
            var data = ms.ToArray();
            return p.ExitCode == 0 && data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 ? data : null;
        }
        catch { return null; }
    }

    private static async Task<bool> FeedAsync(string exe, string[] args, string file)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            await using (var input = File.OpenRead(file)) await input.CopyToAsync(p.StandardInput.BaseStream);
            p.StandardInput.Close();
            // wl-copy stays behind to serve the clipboard; do not wait for it to end.
            await Task.Delay(150);
            return !p.HasExited || p.ExitCode == 0;
        }
        catch { return false; }
    }
}
