using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avamux.Core.Logging;

namespace Avamux.Views;

public static class WebViews
{
    /// <summary>Common web view environment setup: developer tools on, where the platform supports it.</summary>
    public static void Configure(WebViewEnvironmentRequestedEventArgs e)
    {
        var enable = App.Services.Config.Settings.Dashboard.DeveloperTools;
        e.EnableDevTools = enable;
    }

    /// <summary>
    /// Waits until a page's own script is running. Navigation can complete before that, and a call made too early
    /// fails with "can't find variable".
    /// </summary>
    public static async Task<bool> WaitForAsync(NativeWebView web, string jsCondition, int seconds = 10)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            try { if (await web.InvokeScript($"({jsCondition}) ? '1' : '0'") is { } r && r.Contains('1')) return true; }
            catch { /* page not there yet */ }
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// Gives a web view the keyboard focus for real. On Linux the page lives in its own X window inside ours, and
    /// Avalonia's Focus() only moves GTK's focus within that window: X keeps the focus on our window, so the page
    /// believes it is in the background (no caret, no blinking cursor) until it is clicked. Only done while our
    /// window is the active one, so it never pulls focus away from another application.
    /// </summary>
    public static void FocusNative(NativeWebView web)
    {
        web.Focus();
        if (!OperatingSystem.IsLinux() || TopLevel.GetTopLevel(web) is not Window { IsActive: true }) return;
        try
        {
            if (web.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle || handle.Handle == IntPtr.Zero) return;
            if (s_display == IntPtr.Zero) s_display = XOpenDisplay(IntPtr.Zero);
            if (s_display == IntPtr.Zero) return;
            XSetInputFocus(s_display, handle.Handle, RevertToParent, CurrentTime);
            XFlush(s_display);
        }
        catch (Exception ex) { Log.Warn("web", "Could not focus the web view: " + ex.Message); }
    }

    private static IntPtr s_display;
    private const int RevertToParent = 2;
    private static readonly IntPtr CurrentTime = IntPtr.Zero;
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, IntPtr time);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

    /// <summary>Opens the web inspector for a view. Returns false when it has to be opened by hand (right-click → Inspect Element).</summary>
    public static bool OpenDevTools(NativeWebView web)
    {
        try
        {
            var handle = web.TryGetPlatformHandle();
            if (OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle gtk && gtk.WebKitWebView != IntPtr.Zero)
            {
                // WebKitGTK must be called on the GTK thread; g_main_context_invoke hops onto it.
                g_main_context_invoke(IntPtr.Zero, ShowInspector, gtk.WebKitWebView);
                return true;
            }
            if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple && apple.WKWebView != IntPtr.Zero)
            {
                // WKWebView only exposes its inspector through a private selector; use it when present.
                var inspectorSel = sel_registerName("_inspector");
                if (objc_msgSend_bool(apple.WKWebView, sel_registerName("respondsToSelector:"), inspectorSel))
                {
                    var inspector = objc_msgSend(apple.WKWebView, inspectorSel);
                    if (inspector != IntPtr.Zero) { objc_msgSend(inspector, sel_registerName("show")); return true; }
                }
            }
        }
        catch (Exception ex) { Log.Warn("web", "Could not open developer tools: " + ex.Message); }
        return false;
    }

    private delegate int GSourceFunc(IntPtr data);
    private static readonly GSourceFunc ShowInspector = view =>
    {
        try { webkit_web_inspector_show(webkit_web_view_get_inspector(view)); }
        catch (Exception ex) { Log.Warn("web", "Could not open developer tools: " + ex.Message); }
        return 0; // G_SOURCE_REMOVE
    };

    [DllImport("libglib-2.0.so.0")] private static extern void g_main_context_invoke(IntPtr context, GSourceFunc function, IntPtr data);
    [DllImport("libwebkit2gtk-4.1.so.0")] private static extern IntPtr webkit_web_view_get_inspector(IntPtr webView);
    [DllImport("libwebkit2gtk-4.1.so.0")] private static extern void webkit_web_inspector_show(IntPtr inspector);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector, IntPtr arg);
}
