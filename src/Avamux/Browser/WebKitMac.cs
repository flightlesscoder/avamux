using System.Runtime.InteropServices;
using Avamux.Core.Logging;

namespace Avamux.Browser;

/// <summary>
/// Scripts at document start for WKWebView (macOS), which Avalonia's web view does not offer. A WKUserContentController
/// can add scripts but only remove them all at once, so the whole list is rebuilt every time it changes. The list is
/// not avamux's alone: Avalonia (and WebKit) may have put scripts there before us, such as the bridge behind
/// WebMessageReceived. Those are captured once per content controller, before avamux adds any, and put back first on
/// every rebuild. Everything here runs on the main thread (AppKit), which is Avalonia's UI thread on macOS.
/// </summary>
internal static class WebKitMac
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    public static bool Supported => OperatingSystem.IsMacOS();

    /// <summary>A content controller and the scripts it had before avamux touched it; both retained by us.</summary>
    private sealed record Baseline(IntPtr Controller, IntPtr Scripts);

    private static readonly Dictionary<IntPtr, Baseline> Baselines = new(); // by content controller
    private static readonly Dictionary<IntPtr, IntPtr> ControllerOf = new(); // web view → content controller

    /// <summary>
    /// Makes <paramref name="scripts"/> (in order, after the ones that were there before avamux) the view's scripts at
    /// the start of every document. The view's content controller is asked for afresh each time.
    /// </summary>
    public static void SetUserScripts(IntPtr webView, IReadOnlyList<(string Source, bool MainFrameOnly)> scripts)
    {
        if (!Supported || webView == IntPtr.Zero) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var configuration = Send(webView, "configuration"); // a copy, but it shares the view's content controller
            var controller = configuration == IntPtr.Zero ? IntPtr.Zero : Send(configuration, "userContentController");
            if (controller == IntPtr.Zero) throw new InvalidOperationException("The web view has no user content controller.");

            if (!Baselines.TryGetValue(controller, out var baseline))
            {
                // First time for this controller: what is there now is not ours, and must survive every rebuild.
                var existing = Send(controller, "userScripts");
                var copy = existing == IntPtr.Zero ? IntPtr.Zero : Send(existing, "copy"); // +1, ours to release
                baseline = new Baseline(Send(controller, "retain"), copy);
                Baselines[controller] = baseline;
            }
            ControllerOf[webView] = controller;

            Send(controller, "removeAllUserScripts");
            var count = baseline.Scripts == IntPtr.Zero ? 0 : (long)SendNUInt(baseline.Scripts, Sel("count"));
            for (long i = 0; i < count; i++)
                SendVoid(controller, Sel("addUserScript:"), SendIndex(baseline.Scripts, Sel("objectAtIndex:"), (nuint)i));

            var scriptClass = objc_getClass("WKUserScript");
            var stringClass = objc_getClass("NSString");
            foreach (var (source, mainFrameOnly) in scripts)
            {
                var text = SendString(Send(stringClass, "alloc"), Sel("initWithUTF8String:"), source);
                var script = SendInitScript(Send(scriptClass, "alloc"), Sel("initWithSource:injectionTime:forMainFrameOnly:"),
                    text, 0 /* WKUserScriptInjectionTimeAtDocumentStart */, mainFrameOnly);
                SendVoid(controller, Sel("addUserScript:"), script); // the controller keeps its own reference
                Send(script, "release");
                Send(text, "release");
            }
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Lets go of what was kept for a view that is going away (its controller's baseline, when no other view uses it).</summary>
    public static void Forget(IntPtr webView)
    {
        if (!Supported || !ControllerOf.Remove(webView, out var controller)) return;
        if (ControllerOf.ContainsValue(controller) || !Baselines.Remove(controller, out var baseline)) return;
        try
        {
            if (baseline.Scripts != IntPtr.Zero) Send(baseline.Scripts, "release");
            Send(baseline.Controller, "release");
        }
        catch (Exception ex) { Log.Warn("browser", "Could not release a WKWebView's scripts: " + ex.Message); }
    }

    // ---- Objective-C runtime ----

    private static readonly Dictionary<string, IntPtr> Selectors = new();

    private static IntPtr Sel(string name)
    {
        if (!Selectors.TryGetValue(name, out var sel)) Selectors[name] = sel = sel_registerName(name);
        return sel;
    }

    private static IntPtr Send(IntPtr receiver, string selector) => objc_msgSend(receiver, Sel(selector));

    [DllImport(Objc)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Objc)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(Objc)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendInitScript(IntPtr receiver, IntPtr selector, IntPtr source, nint injectionTime, [MarshalAs(UnmanagedType.I1)] bool mainFrameOnly);
}
