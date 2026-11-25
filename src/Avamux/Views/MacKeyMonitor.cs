using System.Runtime.InteropServices;
using Avamux.Core.Logging;

namespace Avamux.Views;

/// <summary>
/// Every key the app receives on macOS, seen before any view gets it: an AppKit local event monitor
/// (<c>+[NSEvent addLocalMonitorForEventsMatchingMask:handler:]</c>). Keys typed into a web view (a terminal, the wiki
/// preview, a browser pane) go to its WKWebView and never reach Avalonia, so the command palette's key is watched here.
/// </summary>
internal static class MacKeyMonitor
{
    /// <summary>One key event: what it means for the palette key, and the key's name and modifiers for shortcuts.</summary>
    public readonly record struct KeyEvent(PaletteKey.Mod Mod, bool Down, bool IsKey, string Name, bool Ctrl, bool Shift, bool Alt, bool Meta);

    // NSEventType and NSEventModifierFlags.
    public const ulong KeyDown = 10, KeyUp = 11, FlagsChanged = 12;
    public const ulong ShiftFlag = 1 << 17, ControlFlag = 1 << 18, OptionFlag = 1 << 19, CommandFlag = 1 << 20;
    private const ulong MaskKeyDown = 1 << 10, MaskKeyUp = 1 << 11, MaskFlagsChanged = 1 << 12;

    /// <summary>
    /// What an NSEvent means for the palette key. A modifier's press and release both come as FlagsChanged, told
    /// apart by whether its flag is still set; any other key going down is "something else pressed".
    /// </summary>
    public static KeyEvent? Translate(ulong type, ushort keyCode, ulong flags, string characters)
    {
        bool ctrl = (flags & ControlFlag) != 0, shift = (flags & ShiftFlag) != 0, alt = (flags & OptionFlag) != 0, meta = (flags & CommandFlag) != 0;
        switch (type)
        {
            case FlagsChanged:
                var (mod, flag) = keyCode switch
                {
                    56 or 60 => (PaletteKey.Mod.Shift, ShiftFlag),     // left, right Shift
                    59 or 62 => (PaletteKey.Mod.Ctrl, ControlFlag),    // left, right Control
                    58 or 61 => (PaletteKey.Mod.Alt, OptionFlag),      // left, right Option
                    55 or 54 => (PaletteKey.Mod.Meta, CommandFlag),    // left, right Command
                    _ => (PaletteKey.Mod.None, 0ul),                    // Caps Lock, Fn
                };
                if (mod == PaletteKey.Mod.None) return new KeyEvent(PaletteKey.Mod.None, true, false, "", ctrl, shift, alt, meta);
                return new KeyEvent(mod, (flags & flag) != 0, false, "", ctrl, shift, alt, meta);
            case KeyDown: return new KeyEvent(PaletteKey.Mod.None, true, true, characters, ctrl, shift, alt, meta);
            case KeyUp: return new KeyEvent(PaletteKey.Mod.None, false, true, characters, ctrl, shift, alt, meta);
            default: return null;
        }
    }

    /// <summary>Called on the main (UI) thread for each key event; return true to swallow it.</summary>
    private static Func<KeyEvent, bool>? _onKey;
    private static IntPtr _monitor;

    /// <summary>Starts the monitor. False when it could not be (not macOS, or AppKit said no).</summary>
    public static bool Start(Func<KeyEvent, bool> onKey)
    {
        if (!OperatingSystem.IsMacOS() || _monitor != IntPtr.Zero) return _monitor != IntPtr.Zero;
        try
        {
            _onKey = onKey;
            var block = NewGlobalBlock();
            _monitor = SendMonitor(objc_getClass("NSEvent"), sel_registerName("addLocalMonitorForEventsMatchingMask:handler:"), MaskKeyDown | MaskKeyUp | MaskFlagsChanged, block);
            if (_monitor == IntPtr.Zero) { Log.Warn("palette", "AppKit did not add the key monitor."); return false; }
            objc_msgSend(_monitor, sel_registerName("retain"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("palette", "Could not watch keys on macOS: " + ex.Message);
            return false;
        }
    }

    /// <summary>The monitor's handler: NSEvent* (^)(NSEvent*). Returning the event lets it go on; null swallows it.</summary>
    private static IntPtr OnEvent(IntPtr block, IntPtr nsEvent)
    {
        try
        {
            if (nsEvent == IntPtr.Zero || _onKey is not { } onKey) return nsEvent;
            var type = SendULong(nsEvent, sel_registerName("type"));
            if (type is not (KeyDown or KeyUp or FlagsChanged)) return nsEvent;
            var keyCode = SendUShort(nsEvent, sel_registerName("keyCode"));
            var flags = SendULong(nsEvent, sel_registerName("modifierFlags"));
            var characters = "";
            if (type != FlagsChanged)
            {
                var text = objc_msgSend(nsEvent, sel_registerName("charactersIgnoringModifiers"));
                if (text != IntPtr.Zero) characters = Marshal.PtrToStringUTF8(objc_msgSend(text, sel_registerName("UTF8String"))) ?? "";
                characters = MacKeyName(keyCode) ?? characters;
            }
            return Translate(type, keyCode, flags, characters) is { } e && onKey(e) ? IntPtr.Zero : nsEvent;
        }
        catch (Exception ex)
        {
            Log.Warn("palette", "Key monitor failed: " + ex.Message);
            return nsEvent;
        }
    }

    /// <summary>Names for keys whose characters are not their name (function keys come as private-use characters).</summary>
    private static string? MacKeyName(ushort keyCode) => keyCode switch
    {
        122 => "F1", 120 => "F2", 99 => "F3", 118 => "F4", 96 => "F5", 97 => "F6", 98 => "F7", 100 => "F8", 101 => "F9", 109 => "F10",
        103 => "F11", 111 => "F12", 36 => "Enter", 48 => "Tab", 49 => "Space", 51 => "Back", 53 => "Escape",
        _ => null,
    };

    // ---- a block AppKit can call (the same blocks-ABI layout as WebKitMac's snapshot block) ----

    private delegate IntPtr EventHandler(IntPtr block, IntPtr nsEvent);
    private static readonly EventHandler Handler = OnEvent; // kept alive for as long as the app runs
    private const int BlockIsGlobal = 1 << 28;

    /// <summary>A global block (Block_copy leaves it alone) that lives as long as the app; nothing captured.</summary>
    private static IntPtr NewGlobalBlock()
    {
        var descriptor = Marshal.AllocHGlobal(16); // reserved, size
        Marshal.WriteInt64(descriptor, 0, 0);
        Marshal.WriteInt64(descriptor, 8, 32);
        var b = Marshal.AllocHGlobal(32); // isa 8, flags 4, reserved 4, invoke 8, descriptor 8
        Marshal.WriteIntPtr(b, 0, NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock"));
        Marshal.WriteInt32(b, 8, BlockIsGlobal);
        Marshal.WriteInt32(b, 12, 0);
        Marshal.WriteIntPtr(b, 16, Marshal.GetFunctionPointerForDelegate(Handler));
        Marshal.WriteIntPtr(b, 24, descriptor);
        return b;
    }

    private const string Objc = "/usr/lib/libobjc.dylib";
    [DllImport(Objc)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern ulong SendULong(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern ushort SendUShort(IntPtr receiver, IntPtr selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendMonitor(IntPtr receiver, IntPtr selector, ulong mask, IntPtr block);
}
