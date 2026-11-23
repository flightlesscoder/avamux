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

    // ---- snapshots ----

    /// <summary>A rectangle in the web view's coordinates (points; WKWebView is flipped, so y grows downwards).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public record struct CGRect(double X, double Y, double Width, double Height);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (string File, TaskCompletionSource<bool> Done)> Snapshots = new();
    private static long _nextSnapshot;

    /// <summary>
    /// Writes a PNG of the web view to <paramref name="file"/>: what it shows, or <paramref name="rect"/> of it (which
    /// may reach past what is on screen, for the whole document). WebKit paints the snapshot itself, in software, so
    /// it works whatever covers the window; content drawn by the GPU (WebGL, video) can come out blank.
    /// </summary>
    public static async Task SnapshotAsync(IntPtr webView, string file, CGRect? rect)
    {
        if (!Supported || webView == IntPtr.Zero) throw new InvalidOperationException("There is no WKWebView to capture.");
        var id = ++_nextSnapshot;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Snapshots[id] = (file, done);
        var block = NewBlock(id);
        var pool = objc_autoreleasePoolPush();
        try
        {
            var configuration = Send(Send(objc_getClass("WKSnapshotConfiguration"), "alloc"), "init");
            if (rect is { } r) SendRect(configuration, Sel("setRect:"), r);
            SendSnapshot(webView, Sel("takeSnapshotWithConfiguration:completionHandler:"), configuration, block);
            Send(configuration, "release");
        }
        catch (Exception ex) { Snapshots.TryRemove(id, out _); done.TrySetException(ex); }
        finally { objc_autoreleasePoolPop(pool); }
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            Snapshots.TryRemove(id, out _);
            // WebKit lets go of the block just after calling it; the memory goes a little later, never under its feet.
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Marshal.FreeHGlobal(block), TaskScheduler.Default);
        }
    }

    /// <summary>Called by WebKit on the main thread with the picture (an NSImage) or an NSError.</summary>
    private static void OnSnapshot(IntPtr block, IntPtr image, IntPtr error)
    {
        var id = Marshal.ReadInt64(block, BlockContextOffset);
        if (!Snapshots.TryRemove(id, out var job)) return; // timed out meanwhile
        try
        {
            if (image == IntPtr.Zero)
            {
                var message = error == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(Send(error, "localizedDescription"), "UTF8String"));
                job.Done.TrySetException(new InvalidOperationException("The page could not be captured" + (message is null ? "." : ": " + message)));
                return;
            }
            var pool = objc_autoreleasePoolPush();
            try
            {
                // NSImage → TIFF data → bitmap representation → PNG data.
                var tiff = Send(image, "TIFFRepresentation");
                var bitmap = tiff == IntPtr.Zero ? IntPtr.Zero : SendObject(objc_getClass("NSBitmapImageRep"), Sel("imageRepWithData:"), tiff);
                var properties = Send(objc_getClass("NSDictionary"), "dictionary");
                var png = bitmap == IntPtr.Zero ? IntPtr.Zero : SendRepresentation(bitmap, Sel("representationUsingType:properties:"), 4 /* NSBitmapImageFileTypePNG */, properties);
                if (png == IntPtr.Zero) throw new InvalidOperationException("The picture could not be turned into a PNG.");
                var bytes = new byte[(int)SendNUInt(png, Sel("length"))];
                Marshal.Copy(Send(png, "bytes"), bytes, 0, bytes.Length);
                File.WriteAllBytes(job.File, bytes);
                job.Done.TrySetResult(true);
            }
            finally { objc_autoreleasePoolPop(pool); }
        }
        catch (Exception ex) { job.Done.TrySetException(ex); }
    }

    // A block for WebKit to call back: a global block (Block_copy and Block_release leave it alone, so it is ours to
    // free), laid out as the blocks ABI has it, with the snapshot's id where a block keeps what it captured.
    private const int BlockIsGlobal = 1 << 28;
    private const int BlockContextOffset = 32; // isa 8, flags 4, reserved 4, invoke 8, descriptor 8

    private delegate void SnapshotCallback(IntPtr block, IntPtr image, IntPtr error);
    private static readonly SnapshotCallback SnapshotInvoke = OnSnapshot; // kept alive for as long as the app runs
    private static readonly Lazy<IntPtr> GlobalBlockClass = new(() => NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock"));
    private static readonly Lazy<IntPtr> BlockDescriptor = new(() =>
    {
        var d = Marshal.AllocHGlobal(16); // reserved, size; never freed (one for the app's life)
        Marshal.WriteInt64(d, 0, 0);
        Marshal.WriteInt64(d, 8, BlockContextOffset + 8);
        return d;
    });

    private static IntPtr NewBlock(long id)
    {
        var b = Marshal.AllocHGlobal(BlockContextOffset + 8);
        Marshal.WriteIntPtr(b, 0, GlobalBlockClass.Value);
        Marshal.WriteInt32(b, 8, BlockIsGlobal);
        Marshal.WriteInt32(b, 12, 0);
        Marshal.WriteIntPtr(b, 16, Marshal.GetFunctionPointerForDelegate(SnapshotInvoke));
        Marshal.WriteIntPtr(b, 24, BlockDescriptor.Value);
        Marshal.WriteInt64(b, BlockContextOffset, id);
        return b;
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
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendObject(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendRect(IntPtr receiver, IntPtr selector, CGRect rect);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendSnapshot(IntPtr receiver, IntPtr selector, IntPtr configuration, IntPtr block);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRepresentation(IntPtr receiver, IntPtr selector, nuint type, IntPtr properties);
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendInitScript(IntPtr receiver, IntPtr selector, IntPtr source, nint injectionTime, [MarshalAs(UnmanagedType.I1)] bool mainFrameOnly);
}
