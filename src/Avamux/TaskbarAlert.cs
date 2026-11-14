using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avamux.Core.Logging;

namespace Avamux;

/// <summary>
/// Asks the desktop to draw attention to the app's taskbar or dock entry while none of its windows is in front:
/// the entry flashes (Windows), is marked "demands attention" (X11, including XWayland) or bounces (macOS), and
/// where the desktop supports it a count of what was missed is shown on the icon (macOS dock badge, and the
/// launcher-entry badge of KDE Plasma, GNOME with the Dash to Dock extension and Unity). Everything is best
/// effort: a desktop that supports none of it simply shows nothing.
/// </summary>
public static class TaskbarAlert
{
    private static int _unseen;

    /// <summary>Marks one more thing as missed and raises the alert. Call on the UI thread.</summary>
    public static void Raise(Window window)
    {
        try
        {
            _unseen++;
            Apply(window, _unseen);
        }
        catch (Exception ex) { Log.Debug("taskbar", "Could not raise the taskbar alert: " + ex.Message); }
    }

    /// <summary>Removes the alert; the window has been looked at. Call on the UI thread.</summary>
    public static void Clear(Window window)
    {
        if (_unseen == 0) return;
        _unseen = 0;
        try { Apply(window, 0); }
        catch (Exception ex) { Log.Debug("taskbar", "Could not clear the taskbar alert: " + ex.Message); }
    }

    private static void Apply(Window window, int count)
    {
        if (OperatingSystem.IsWindows()) Windows(window, count > 0);
        else if (OperatingSystem.IsMacOS()) Mac(count);
        else if (OperatingSystem.IsLinux()) { X11(window, count > 0); LauncherBadge(count); }
    }

    // ---- Windows ----

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public nint Window; public uint Flags; public uint Count; public uint Timeout; }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);

    private static void Windows(Window window, bool on)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
        // TRAY (2) flashes the taskbar button; TIMERNOFG (12) keeps doing so until the window is in front.
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = on ? 2u | 12u : 0u, Count = uint.MaxValue };
        FlashWindowEx(ref info);
    }

    // ---- X11 ----

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nint XInternAtom(nint display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(nint display, nint window, bool propagate, nint mask, nint ev);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);

    /// <summary>Sets or clears _NET_WM_STATE_DEMANDS_ATTENTION by the method the window manager specification gives.</summary>
    private static void X11(Window window, bool on)
    {
        if (window.TryGetPlatformHandle() is not { Handle: not 0 } handle || handle.HandleDescriptor != "XID") return;
        var display = XOpenDisplay(0);
        if (display == 0) return;
        var ev = Marshal.AllocHGlobal(192); // an XEvent is a union of 24 longs
        try
        {
            for (var i = 0; i < 192; i += 8) Marshal.WriteInt64(ev, i, 0);
            Marshal.WriteInt32(ev, 0, 33);                                        // ClientMessage
            Marshal.WriteInt32(ev, 16, 1);                                        // sent by a client
            Marshal.WriteIntPtr(ev, 24, display);
            Marshal.WriteIntPtr(ev, 32, handle.Handle);                           // the window
            Marshal.WriteIntPtr(ev, 40, XInternAtom(display, "_NET_WM_STATE", false));
            Marshal.WriteInt32(ev, 48, 32);                                       // format: longs
            Marshal.WriteInt64(ev, 56, on ? 1 : 0);                               // _NET_WM_STATE_ADD / _REMOVE
            Marshal.WriteInt64(ev, 64, XInternAtom(display, "_NET_WM_STATE_DEMANDS_ATTENTION", false));
            Marshal.WriteInt64(ev, 80, 1);                                        // source: a normal application
            const int substructureNotify = 0x80000, substructureRedirect = 0x100000;
            XSendEvent(display, XDefaultRootWindow(display), false, substructureNotify | substructureRedirect, ev);
            XFlush(display);
        }
        finally { Marshal.FreeHGlobal(ev); XCloseDisplay(display); }
    }

    /// <summary>The count badge of the Unity launcher API, which Plasma's task manager and Dash to Dock also draw.</summary>
    private static void LauncherBadge(int count)
    {
        try
        {
            var psi = new ProcessStartInfo("gdbus") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in new[]
                     {
                         "emit", "--session", "--object-path", "/com/avamux/launcher", "--signal", "com.canonical.Unity.LauncherEntry.Update",
                         "application://avamux.desktop", $"{{'count': <int64 {count}>, 'count-visible': <{(count > 0 ? "true" : "false")}>}}",
                     }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
        }
        catch { /* no gdbus: no badge */ }
    }

    // ---- macOS ----

    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, string arg);

    /// <summary>Bounces the dock icon once per call and writes the count on it.</summary>
    private static void Mac(int count)
    {
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        if (app == 0) return;
        var label = count > 0 ? Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), count.ToString()) : 0;
        Send(Send(app, sel_registerName("dockTile")), sel_registerName("setBadgeLabel:"), label);
        if (count > 0) Send(app, sel_registerName("requestUserAttention:"), 10); // NSInformationalRequest: one bounce
    }
}
