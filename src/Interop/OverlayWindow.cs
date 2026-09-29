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
///
/// Anywhere but X11 these do nothing.
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
