using System.Runtime.InteropServices;
using Avamux.Core.Logging;

namespace Avamux.Browser;

/// <summary>What a WebKitGTK view tells its pane. Called on the GLib thread: implementations hop to the UI thread.</summary>
internal interface IWebKitEvents
{
    /// <summary>The page opened an alert, confirm, prompt or before-unload dialog. The dialog stays open until answered.</summary>
    void OnDialog(IntPtr dialog, string type, string message, string? defaultText);
    void OnProperty(string name, string? text, double number, bool flag);
    void OnCrashed(string reason);
    void OnInspectorClosed();
}

/// <summary>
/// Direct calls into WebKitGTK for what Avalonia's web view does not offer: scripts at document start, the Web
/// Inspector, zoom, screenshots, the page's own dialogs, and title/address/progress changes. All of it has to run on
/// the GLib thread, which <see cref="Avalonia.X11.Interop.GtkInteropHelper"/> provides.
/// </summary>
internal static class WebKitGtk
{
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string Cairo = "libcairo.so.2";

    public static bool Supported => OperatingSystem.IsLinux();

    public static Task<T> OnGlib<T>(Func<T> f) => Avalonia.X11.Interop.GtkInteropHelper.RunOnGlibThread(f);
    public static Task OnGlib(Action a) => OnGlib(() => { a(); return 0; });

    // ---- scripts at document start ----

    /// <summary>Adds a script that runs at the start of every document in every frame. Returns it, for removal.</summary>
    public static Task<IntPtr> AddUserScriptAsync(IntPtr view, string source) => OnGlib(() =>
    {
        var manager = webkit_web_view_get_user_content_manager(view);
        var script = webkit_user_script_new(source, 0 /* all frames */, 0 /* document start */, IntPtr.Zero, IntPtr.Zero);
        webkit_user_content_manager_add_script(manager, script);
        return script; // keeps our reference, released in RemoveUserScriptAsync
    });

    public static Task RemoveUserScriptAsync(IntPtr view, IntPtr script) => OnGlib(() =>
    {
        if (script == IntPtr.Zero) return;
        webkit_user_content_manager_remove_script(webkit_web_view_get_user_content_manager(view), script);
        webkit_user_script_unref(script);
    });

    // ---- signals ----

    private sealed class Connection(IWebKitEvents events)
    {
        public IWebKitEvents Events { get; } = events;
        public bool InspectorConnected;
    }

    private static readonly Dictionary<IntPtr, GCHandle> Connections = new();

    /// <summary>Hooks dialogs, crashes and property changes of a view up to <paramref name="events"/>.</summary>
    public static Task ConnectAsync(IntPtr view, IWebKitEvents events) => OnGlib(() =>
    {
        if (Connections.ContainsKey(view)) return;
        var handle = GCHandle.Alloc(new Connection(events));
        Connections[view] = handle;
        var data = GCHandle.ToIntPtr(handle);
        g_signal_connect_data(view, "script-dialog", DialogPtr, data, IntPtr.Zero, 0);
        g_signal_connect_data(view, "web-process-terminated", CrashPtr, data, IntPtr.Zero, 0);
        foreach (var prop in new[] { "title", "uri", "estimated-load-progress", "is-loading", "is-playing-audio", "is-muted" })
            g_signal_connect_data(view, "notify::" + prop, NotifyPtr, data, IntPtr.Zero, 0);
    });

    public static void Disconnect(IntPtr view)
    {
        _ = OnGlib(() =>
        {
            if (!Connections.Remove(view, out var handle)) return;
            g_signal_handlers_disconnect_matched(view, 16 /* G_SIGNAL_MATCH_DATA */, 0, 0, IntPtr.Zero, IntPtr.Zero, GCHandle.ToIntPtr(handle));
            var inspector = webkit_web_view_get_inspector(view);
            if (inspector != IntPtr.Zero) g_signal_handlers_disconnect_matched(inspector, 16, 0, 0, IntPtr.Zero, IntPtr.Zero, GCHandle.ToIntPtr(handle));
            handle.Free();
        });
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DialogFn(IntPtr view, IntPtr dialog, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CrashFn(IntPtr view, int reason, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NotifyFn(IntPtr obj, IntPtr pspec, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InspectorFn(IntPtr inspector, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadyFn(IntPtr source, IntPtr result, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PressFn(IntPtr widget, IntPtr gdkEvent, IntPtr data);
    private static readonly PressFn PressCallback = OnPress;
    private static readonly IntPtr PressPtr = Marshal.GetFunctionPointerForDelegate(PressCallback);
    private static readonly Dictionary<IntPtr, GCHandle> PressWatchers = new();

    /// <summary>Calls <paramref name="onPress"/> (on the GLib thread) whenever a mouse button is pressed in the view.</summary>
    public static Task WatchPressesAsync(IntPtr view, Action onPress) => OnGlib(() =>
    {
        if (PressWatchers.ContainsKey(view)) return;
        var handle = GCHandle.Alloc(onPress);
        PressWatchers[view] = handle;
        g_signal_connect_data(view, "button-press-event", PressPtr, GCHandle.ToIntPtr(handle), IntPtr.Zero, 0);
    });

    /// <summary>Stops watching a view that is going away.</summary>
    public static void UnwatchPresses(IntPtr view) => _ = OnGlib(() =>
    {
        if (!PressWatchers.Remove(view, out var handle)) return;
        g_signal_handlers_disconnect_matched(view, 16 /* G_SIGNAL_MATCH_DATA */, 0, 0, IntPtr.Zero, IntPtr.Zero, GCHandle.ToIntPtr(handle));
        handle.Free();
    });

    /// <summary>Returns false, so the page still gets the click.</summary>
    private static int OnPress(IntPtr widget, IntPtr gdkEvent, IntPtr data)
    {
        try { (GCHandle.FromIntPtr(data).Target as Action)?.Invoke(); } catch { }
        return 0;
    }

    private static readonly DialogFn DialogCallback = OnDialog;
    private static readonly CrashFn CrashCallback = OnCrash;
    private static readonly NotifyFn NotifyCallback = OnNotify;
    private static readonly InspectorFn InspectorClosedCallback = OnInspectorClosed;
    private static readonly ReadyFn SnapshotCallback = OnSnapshot;
    private static readonly IntPtr DialogPtr = Marshal.GetFunctionPointerForDelegate(DialogCallback);
    private static readonly IntPtr CrashPtr = Marshal.GetFunctionPointerForDelegate(CrashCallback);
    private static readonly IntPtr NotifyPtr = Marshal.GetFunctionPointerForDelegate(NotifyCallback);
    private static readonly IntPtr InspectorClosedPtr = Marshal.GetFunctionPointerForDelegate(InspectorClosedCallback);
    private static readonly IntPtr SnapshotPtr = Marshal.GetFunctionPointerForDelegate(SnapshotCallback);

    private static IWebKitEvents? EventsOf(IntPtr data)
    {
        try { return (GCHandle.FromIntPtr(data).Target as Connection)?.Events; } catch { return null; }
    }

    private static int OnDialog(IntPtr view, IntPtr dialog, IntPtr data)
    {
        try
        {
            if (EventsOf(data) is not { } events) return 0;
            var type = webkit_script_dialog_get_dialog_type(dialog) switch { 0 => "alert", 1 => "confirm", 2 => "prompt", 3 => "beforeunload", _ => "dialog" };
            var message = Marshal.PtrToStringUTF8(webkit_script_dialog_get_message(dialog)) ?? "";
            var def = type == "prompt" ? Marshal.PtrToStringUTF8(webkit_script_dialog_prompt_get_default_text(dialog)) : null;
            webkit_script_dialog_ref(dialog); // stays open (and the page waits) until AnswerDialog
            events.OnDialog(dialog, type, message, def);
            return 1;
        }
        catch (Exception ex) { Log.Error("browser", "Page dialog failed", ex); return 0; }
    }

    /// <summary>Answers a dialog from <see cref="IWebKitEvents.OnDialog"/> and lets the page carry on.</summary>
    public static Task AnswerDialogAsync(IntPtr dialog, string type, bool accept, string? text) => OnGlib(() =>
    {
        if (type is "confirm" or "beforeunload") webkit_script_dialog_confirm_set_confirmed(dialog, accept);
        else if (type == "prompt" && accept) webkit_script_dialog_prompt_set_text(dialog, text ?? Marshal.PtrToStringUTF8(webkit_script_dialog_prompt_get_default_text(dialog)) ?? "");
        webkit_script_dialog_close(dialog);
        webkit_script_dialog_unref(dialog);
    });

    private static void OnCrash(IntPtr view, int reason, IntPtr data)
    {
        try { EventsOf(data)?.OnCrashed(reason switch { 0 => "crashed", 1 => "ran out of memory", _ => "was stopped" }); }
        catch (Exception ex) { Log.Error("browser", "Crash handler failed", ex); }
    }

    private static void OnNotify(IntPtr obj, IntPtr pspec, IntPtr data)
    {
        try
        {
            if (EventsOf(data) is not { } events) return;
            var name = Marshal.PtrToStringUTF8(g_param_spec_get_name(pspec)) ?? "";
            // History changes come with every load and address change; there is no signal of its own worth hooking.
            if (name is "uri" or "is-loading")
            {
                events.OnProperty("can-go-back", null, 0, webkit_web_view_can_go_back(obj));
                events.OnProperty("can-go-forward", null, 0, webkit_web_view_can_go_forward(obj));
            }
            switch (name)
            {
                case "title": events.OnProperty(name, Marshal.PtrToStringUTF8(webkit_web_view_get_title(obj)), 0, false); break;
                case "uri": events.OnProperty(name, Marshal.PtrToStringUTF8(webkit_web_view_get_uri(obj)), 0, false); break;
                case "estimated-load-progress": events.OnProperty(name, null, webkit_web_view_get_estimated_load_progress(obj), false); break;
                case "is-loading": events.OnProperty(name, null, 0, webkit_web_view_is_loading(obj)); break;
                case "is-playing-audio": events.OnProperty(name, null, 0, webkit_web_view_is_playing_audio(obj)); break;
                case "is-muted": events.OnProperty(name, null, 0, webkit_web_view_get_is_muted(obj)); break;
            }
        }
        catch (Exception ex) { Log.Warn("browser", "Property notification failed: " + ex.Message); }
    }

    private static void OnInspectorClosed(IntPtr inspector, IntPtr data)
    {
        try { EventsOf(data)?.OnInspectorClosed(); } catch { }
    }

    // ---- inspector, zoom, loading ----

    /// <summary>open, close, attach, detach or status. Returns whether the inspector is open afterwards (as far as WebKit says).</summary>
    public static Task<bool> InspectorAsync(IntPtr view, string action, bool wasOpen) => OnGlib(() =>
    {
        var inspector = webkit_web_view_get_inspector(view);
        if (inspector == IntPtr.Zero) return false;
        if (Connections.TryGetValue(view, out var handle) && handle.Target is Connection { InspectorConnected: false } c)
        {
            g_signal_connect_data(inspector, "closed", InspectorClosedPtr, GCHandle.ToIntPtr(handle), IntPtr.Zero, 0);
            c.InspectorConnected = true;
        }
        switch (action)
        {
            case "open": webkit_web_inspector_show(inspector); return true;
            case "close": webkit_web_inspector_close(inspector); return false;
            case "attach": webkit_web_inspector_show(inspector); webkit_web_inspector_attach(inspector); return true;
            case "detach": webkit_web_inspector_show(inspector); webkit_web_inspector_detach(inspector); return true;
        }
        return wasOpen;
    });

    public static Task<bool> InspectorAttachedAsync(IntPtr view) => OnGlib(() =>
    {
        var inspector = webkit_web_view_get_inspector(view);
        return inspector != IntPtr.Zero && webkit_web_inspector_is_attached(inspector);
    });

    /// <summary>Back or forward without blocking: Avalonia's own GoBack waits on the GLib thread, which can be waiting on us.</summary>
    public static Task<bool> GoAsync(IntPtr view, bool back) => OnGlib(() =>
    {
        if (back ? !webkit_web_view_can_go_back(view) : !webkit_web_view_can_go_forward(view)) return false;
        if (back) webkit_web_view_go_back(view); else webkit_web_view_go_forward(view);
        return true;
    });

    public static Task SetZoomAsync(IntPtr view, double zoom) => OnGlib(() => webkit_web_view_set_zoom_level(view, zoom));
    public static Task HardReloadAsync(IntPtr view) => OnGlib(() => webkit_web_view_reload_bypass_cache(view));
    public static Task SetMutedAsync(IntPtr view, bool muted) => OnGlib(() => webkit_web_view_set_is_muted(view, muted));

    // ---- screenshots ----

    private sealed record SnapshotJob(string File, TaskCompletionSource<bool> Done);

    /// <summary>Writes a PNG of what is in view, or of the whole document.</summary>
    public static async Task SnapshotAsync(IntPtr view, string file, bool fullDocument)
    {
        var job = new SnapshotJob(file, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        var handle = GCHandle.Alloc(job);
        try
        {
            await OnGlib(() => webkit_web_view_get_snapshot(view, fullDocument ? 1 : 0, 0, IntPtr.Zero, SnapshotPtr, GCHandle.ToIntPtr(handle)));
            await job.Done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { handle.Free(); }
    }

    private static void OnSnapshot(IntPtr source, IntPtr result, IntPtr data)
    {
        if (GCHandle.FromIntPtr(data).Target is not SnapshotJob job) return;
        try
        {
            var error = IntPtr.Zero;
            var surface = webkit_web_view_get_snapshot_finish(source, result, ref error);
            if (surface == IntPtr.Zero)
            {
                var message = error != IntPtr.Zero ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) : null;
                if (error != IntPtr.Zero) g_error_free(error);
                job.Done.TrySetException(new InvalidOperationException("The page could not be captured" + (message is null ? "." : ": " + message)));
                return;
            }
            var status = cairo_surface_write_to_png(surface, job.File);
            cairo_surface_destroy(surface);
            if (status != 0) job.Done.TrySetException(new IOException($"Could not write {job.File} (cairo status {status})."));
            else job.Done.TrySetResult(true);
        }
        catch (Exception ex) { job.Done.TrySetException(ex); }
    }

    // ---- imports ----

    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_get_user_content_manager(IntPtr view);
    [DllImport(WebKit)] private static extern IntPtr webkit_user_script_new([MarshalAs(UnmanagedType.LPUTF8Str)] string source, int frames, int time, IntPtr allow, IntPtr block);
    [DllImport(WebKit)] private static extern void webkit_user_script_unref(IntPtr script);
    [DllImport(WebKit)] private static extern void webkit_user_content_manager_add_script(IntPtr manager, IntPtr script);
    [DllImport(WebKit)] private static extern void webkit_user_content_manager_remove_script(IntPtr manager, IntPtr script);
    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_get_inspector(IntPtr view);
    [DllImport(WebKit)] private static extern void webkit_web_inspector_show(IntPtr inspector);
    [DllImport(WebKit)] private static extern void webkit_web_inspector_close(IntPtr inspector);
    [DllImport(WebKit)] private static extern void webkit_web_inspector_attach(IntPtr inspector);
    [DllImport(WebKit)] private static extern void webkit_web_inspector_detach(IntPtr inspector);
    [DllImport(WebKit)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool webkit_web_inspector_is_attached(IntPtr inspector);
    [DllImport(WebKit)] private static extern void webkit_web_view_set_zoom_level(IntPtr view, double zoom);
    [DllImport(WebKit)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool webkit_web_view_can_go_back(IntPtr view);
    [DllImport(WebKit)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool webkit_web_view_can_go_forward(IntPtr view);
    [DllImport(WebKit)] private static extern void webkit_web_view_go_back(IntPtr view);
    [DllImport(WebKit)] private static extern void webkit_web_view_go_forward(IntPtr view);
    [DllImport(WebKit)] private static extern void webkit_web_view_reload_bypass_cache(IntPtr view);
    [DllImport(WebKit)] private static extern void webkit_web_view_set_is_muted(IntPtr view, [MarshalAs(UnmanagedType.Bool)] bool muted);
    [DllImport(WebKit)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool webkit_web_view_get_is_muted(IntPtr view);
    [DllImport(WebKit)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool webkit_web_view_is_playing_audio(IntPtr view);
    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_get_title(IntPtr view);
    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_get_uri(IntPtr view);
    [DllImport(WebKit)] private static extern double webkit_web_view_get_estimated_load_progress(IntPtr view);
    [DllImport(WebKit)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool webkit_web_view_is_loading(IntPtr view);
    [DllImport(WebKit)] private static extern void webkit_web_view_get_snapshot(IntPtr view, int region, int options, IntPtr cancellable, IntPtr callback, IntPtr data);
    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_get_snapshot_finish(IntPtr view, IntPtr result, ref IntPtr error);
    [DllImport(WebKit)] private static extern int webkit_script_dialog_get_dialog_type(IntPtr dialog);
    [DllImport(WebKit)] private static extern IntPtr webkit_script_dialog_get_message(IntPtr dialog);
    [DllImport(WebKit)] private static extern IntPtr webkit_script_dialog_prompt_get_default_text(IntPtr dialog);
    [DllImport(WebKit)] private static extern void webkit_script_dialog_prompt_set_text(IntPtr dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(WebKit)] private static extern void webkit_script_dialog_confirm_set_confirmed(IntPtr dialog, [MarshalAs(UnmanagedType.Bool)] bool confirmed);
    [DllImport(WebKit)] private static extern IntPtr webkit_script_dialog_ref(IntPtr dialog);
    [DllImport(WebKit)] private static extern void webkit_script_dialog_unref(IntPtr dialog);
    [DllImport(WebKit)] private static extern void webkit_script_dialog_close(IntPtr dialog);
    [DllImport(GObject)] private static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    [DllImport(GObject)] private static extern uint g_signal_handlers_disconnect_matched(IntPtr instance, int mask, uint signalId, uint detail, IntPtr closure, IntPtr func, IntPtr data);
    [DllImport(GObject)] private static extern IntPtr g_param_spec_get_name(IntPtr pspec);
    [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
    [DllImport(Cairo)] private static extern int cairo_surface_write_to_png(IntPtr surface, [MarshalAs(UnmanagedType.LPUTF8Str)] string file);
    [DllImport(Cairo)] private static extern void cairo_surface_destroy(IntPtr surface);
}
