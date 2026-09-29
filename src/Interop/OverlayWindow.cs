using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace screengrab.Interop;

/// <summary>
/// Makes an Avalonia window an X11 "override-redirect" window, one the window manager leaves
/// alone: no title bar, exactly the position and size asked for (so it can cover every monitor),
/// and above ordinary windows. Being on top isn't enough to get the mouse everywhere, though:
/// Cinnamon's panel is drawn by the compositor, which keeps an input area of its own over it,
/// so presses there go to the panel. <see cref="InputGrab"/> takes the mouse and keyboard, so
/// every press, drag and key comes to the window wherever it is on the screen.
/// <see cref="BringToFront"/> brings an ordinary window to the front after a hotkey screenshot,
/// and <see cref="LowerOneStep"/> moves one down a place among the desktop's windows.
///
/// Anywhere but X11 these do nothing (BringToFront falls back to Activate).
/// </summary>
internal static class OverlayWindow
{
    private const ulong CWOverrideRedirect = 1 << 9;

    /// <summary>The size of Xlib's XSetWindowAttributes, and where override_redirect is in it (64-bit layout).</summary>
    private const int AttributesSize = 112;
    private const int OverrideRedirectOffset = 88;

    /// <summary>Call before the window is first shown; it takes effect when it's mapped.</summary>
    public static void BypassWindowManager(Window window)
    {
        if (Xid(window) is not { } xid)
            return;

        WithDisplay(display =>
        {
            var attributes = Marshal.AllocHGlobal(AttributesSize);
            try
            {
                for (var i = 0; i < AttributesSize; i += sizeof(long))
                    Marshal.WriteInt64(attributes, i, 0);
                Marshal.WriteInt32(attributes, OverrideRedirectOffset, 1);
                X11.XChangeWindowAttributes(display, xid, CWOverrideRedirect, attributes);
            }
            finally
            {
                Marshal.FreeHGlobal(attributes);
            }
        });
    }

    /// <summary>XSetInputFocus: if the window goes away, the focus goes to its parent.</summary>
    private const int RevertToParent = 2;

    /// <summary>
    /// Brings a window to the front and gives it the keyboard focus, even when Screen Grab wasn't
    /// the program in use. <see cref="Window.Activate"/> only asks Cinnamon, whose focus stealing
    /// prevention refuses after a hotkey screenshot (the key press went to another program). So
    /// this raises the window and sets the focus with the X server directly, which Cinnamon
    /// can't refuse; it sees the change and treats the window as active. The window must be on
    /// screen. Anywhere but X11 it's a plain Activate.
    /// </summary>
    public static void BringToFront(Window window)
    {
        if (Xid(window) is not { } xid)
        {
            window.Activate();
            return;
        }

        WithDisplay(display =>
        {
            X11.XRaiseWindow(display, xid);
            X11.XSetInputFocus(display, xid, RevertToParent, IntPtr.Zero);
        });
    }

    /// <summary>The predefined WINDOW atom, the type of _NET_CLIENT_LIST_STACKING.</summary>
    private static readonly IntPtr XA_WINDOW = 33;

    // A ClientMessage event (64-bit layout): type, window, message_type, format, then 5 longs of data.
    private const int ClientMessage = 33;
    private const int MessageWindowOffset = 32;
    private const int MessageTypeOffset = 40;
    private const int MessageFormatOffset = 48;
    private const int MessageDataOffset = 56;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;

    /// <summary>_NET_RESTACK_WINDOW's source indication for a pager, such as the panel's window list.</summary>
    private const long SourcePager = 2;

    /// <summary>XConfigureWindow's stack mode: just below the sibling.</summary>
    private const long StackBelow = 1;

    /// <summary>
    /// Moves a window one place down among the desktop's windows: just below the window that's
    /// directly under it (from the window manager's _NET_CLIENT_LIST_STACKING, bottom to top).
    /// Nothing happens if it's already at the bottom. The move is asked of the window manager
    /// with _NET_RESTACK_WINDOW, as a pager (the panel's window list) would. Anywhere but X11 it
    /// does nothing.
    /// </summary>
    public static void LowerOneStep(Window window)
    {
        if (Xid(window) is not { } xid)
            return;

        WithDisplay(display =>
        {
            var root = X11.XDefaultRootWindow(display);
            var stackingAtom = X11.XInternAtom(display, "_NET_CLIENT_LIST_STACKING", false);
            if (X11.XGetWindowProperty(display, root, stackingAtom, 0, 4096, false, XA_WINDOW,
                    out _, out var format, out var count, out _, out var data) != X11.Success || data == IntPtr.Zero)
                return;

            IntPtr below = IntPtr.Zero;
            try
            {
                // Format 32 comes as one long per window.
                for (var i = 1; format == 32 && i < (int)count; i++)
                {
                    if (Marshal.ReadIntPtr(data, i * sizeof(long)) == xid)
                    {
                        below = Marshal.ReadIntPtr(data, (i - 1) * sizeof(long));
                        break;
                    }
                }
            }
            finally
            {
                X11.XFree(data);
            }
            if (below == IntPtr.Zero)
                return;

            var message = Marshal.AllocHGlobal(X11.XEventSize);
            try
            {
                for (var i = 0; i < X11.XEventSize; i += sizeof(long))
                    Marshal.WriteInt64(message, i, 0);
                Marshal.WriteInt32(message, 0, ClientMessage);
                Marshal.WriteIntPtr(message, MessageWindowOffset, xid);
                Marshal.WriteIntPtr(message, MessageTypeOffset, X11.XInternAtom(display, "_NET_RESTACK_WINDOW", false));
                Marshal.WriteInt32(message, MessageFormatOffset, 32);
                // Source, then the sibling, then where to go relative to it.
                Marshal.WriteInt64(message, MessageDataOffset, SourcePager);
                Marshal.WriteIntPtr(message, MessageDataOffset + sizeof(long), below);
                Marshal.WriteInt64(message, MessageDataOffset + 2 * sizeof(long), StackBelow);
                X11.XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, message);
            }
            finally
            {
                Marshal.FreeHGlobal(message);
            }
        });
    }

    /// <summary>
    /// Whether the window, or one of its dialogs, has the focus: the window manager's active
    /// window (_NET_ACTIVE_WINDOW) is this one, or its parent links (WM_TRANSIENT_FOR) lead back
    /// here. That covers dialogs of dialogs, and the Save dialog, which the desktop shows for
    /// Avalonia but tied to this window. Anywhere but X11 it's the window's own IsActive.
    /// </summary>
    public static bool HasFocusWithin(Window window)
    {
        if (Xid(window) is not { } xid)
            return window.IsActive;

        var focused = false;
        WithDisplay(display =>
        {
            var root = X11.XDefaultRootWindow(display);
            var activeAtom = X11.XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
            if (X11.XGetWindowProperty(display, root, activeAtom, 0, 1, false, XA_WINDOW,
                    out _, out var format, out var count, out _, out var data) != X11.Success || data == IntPtr.Zero)
                return;

            IntPtr active;
            try
            {
                active = format == 32 && count == 1 ? Marshal.ReadIntPtr(data) : IntPtr.Zero;
            }
            finally
            {
                X11.XFree(data);
            }

            // A few steps up is plenty (capture window, Cloud, Settings, a message); the limit
            // guards against a loop of parent links.
            for (var step = 0; step < 8 && active != IntPtr.Zero; step++)
            {
                if (active == xid)
                {
                    focused = true;
                    return;
                }
                if (X11.XGetTransientForHint(display, active, out var parent) == 0)
                    return;
                active = parent;
            }
        });
        return focused;
    }

    /// <summary>
    /// An active grab of the mouse and keyboard for one window, made on Avalonia's own
    /// connection to the X server, so the events arrive through Avalonia's usual path: the mouse
    /// through XInput 2 (which Avalonia reads it with), the keys as core events. Another program
    /// may already hold a grab (its menu is open), in which case <see cref="TryGrab"/> fails
    /// until that program lets go; call it again. Dispose releases whatever was grabbed.
    /// </summary>
    public sealed class InputGrab(Window window) : IDisposable
    {
        private readonly IntPtr? _display = AvaloniaDisplay();
        private int? _pointer;
        private bool _keyboard;

        /// <summary>Grabs what isn't grabbed yet; true once both are (or there's nothing to grab: not X11).</summary>
        public bool TryGrab()
        {
            if (_display is not { } display || Xid(window) is not { } xid)
                return true;

            if (_pointer is null)
            {
                if (X11.XIGetClientPointer(display, IntPtr.Zero, out var pointer) == 0)
                    return true;
                var bits = new byte[4];
                foreach (var type in new[] { X11.XI_ButtonPress, X11.XI_ButtonRelease, X11.XI_Motion, X11.XI_Enter, X11.XI_Leave })
                    bits[type >> 3] |= (byte)(1 << (type & 7));
                var handle = GCHandle.Alloc(bits, GCHandleType.Pinned);
                try
                {
                    var mask = new X11.XIEventMask { deviceid = pointer, mask_len = bits.Length, mask = handle.AddrOfPinnedObject() };
                    // Not owner-events: every pointer event is reported to this window, whatever is under the pointer.
                    if (X11.XIGrabDevice(display, pointer, xid, IntPtr.Zero, IntPtr.Zero,
                            X11.GrabModeAsync, X11.GrabModeAsync, false, ref mask) == X11.GrabSuccess)
                        _pointer = pointer;
                }
                finally
                {
                    handle.Free();
                }
            }

            if (!_keyboard)
                _keyboard = X11.XGrabKeyboard(display, xid, false, X11.GrabModeAsync, X11.GrabModeAsync, IntPtr.Zero) == X11.GrabSuccess;

            return _pointer is not null && _keyboard;
        }

        public void Dispose()
        {
            if (_display is not { } display)
                return;
            if (_pointer is { } pointer)
                X11.XIUngrabDevice(display, pointer, IntPtr.Zero);
            if (_keyboard)
                X11.XUngrabKeyboard(display, IntPtr.Zero);
            X11.XFlush(display);
            _pointer = null;
            _keyboard = false;
        }
    }

    /// <summary>
    /// Avalonia's connection to the X server. Avalonia doesn't make it public, so it's read from
    /// its X11 platform object; null if that ever changes (the grab is then skipped).
    /// </summary>
    private static IntPtr? AvaloniaDisplay()
    {
        try
        {
            var platformType = Type.GetType("Avalonia.X11.AvaloniaX11Platform, Avalonia.X11");
            var locator = typeof(Avalonia.AvaloniaLocator).GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var platform = platformType is null || locator is null
                ? null
                : locator.GetType().GetMethod("GetService", [typeof(Type)])?.Invoke(locator, [platformType]);
            return platformType?.GetProperty("Display")?.GetValue(platform) is IntPtr display && display != IntPtr.Zero
                ? display
                : null;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or AmbiguousMatchException)
        {
            return null;
        }
    }

    private static IntPtr? Xid(Window window) =>
        OperatingSystem.IsLinux() && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle
            ? handle.Handle
            : null;

    /// <summary>Runs <paramref name="action"/> on a connection of our own; window attributes and focus are server-wide.</summary>
    private static void WithDisplay(Action<IntPtr> action)
    {
        try
        {
            var display = X11.XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return;
            try
            {
                action(display);
                X11.XFlush(display);
            }
            finally
            {
                X11.XCloseDisplay(display);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No Xlib: not X11.
        }
    }
}
