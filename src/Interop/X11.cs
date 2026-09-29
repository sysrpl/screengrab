using System.Runtime.InteropServices;

namespace screengrab.Interop;

/// <summary>
/// The parts of Xlib, the XInput 2 extension (libXi) and XFixes that Screen Grab uses. Each
/// caller opens its own connection with <see cref="XOpenDisplay"/>, separate from Avalonia's,
/// and uses it from one thread only.
/// </summary>
internal static class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXi = "libXi.so.6";
    private const string LibXfixes = "libXfixes.so.3";

    public const int Success = 0;
    public const int ZPixmap = 2;
    public const ulong AllPlanes = ~0UL;
    public const int LsbFirst = 0;

    /// <summary>The event type of every extension event, including XInput 2's.</summary>
    public const int GenericEvent = 35;

    public const int GrabSuccess = 0;
    public const int GrabModeAsync = 1;

    public const int XIAllMasterDevices = 1;
    public const int XI_ButtonPress = 4;
    public const int XI_ButtonRelease = 5;
    public const int XI_Motion = 6;
    public const int XI_Enter = 7;
    public const int XI_Leave = 8;
    public const int XI_RawKeyPress = 13;
    public const int XI_RawKeyRelease = 14;

    /// <summary>An XEvent is a union of 24 longs.</summary>
    public const int XEventSize = 24 * 8;

    // Offsets into XGenericEventCookie (64-bit layout).
    public const int CookieTypeOffset = 0;
    public const int CookieExtensionOffset = 32;
    public const int CookieEvTypeOffset = 36;
    public const int CookieDataOffset = 48;

    // Offsets into XIRawEvent (64-bit layout).
    public const int RawDeviceIdOffset = 48;
    public const int RawDetailOffset = 56;

    /// <summary>The start of Xlib's XImage, up to the colour masks.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XImage
    {
        public int width;
        public int height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        public ulong red_mask;
        public ulong green_mask;
        public ulong blue_mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIEventMask
    {
        public int deviceid;
        public int mask_len;
        public IntPtr mask;
    }

    /// <summary>
    /// The pointer's picture. <c>pixels</c> holds width x height premultiplied ARGB values, one
    /// per unsigned long (so 8 bytes each on 64-bit, with the colour in the low 4).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XFixesCursorImage
    {
        public short x;
        public short y;
        public ushort width;
        public ushort height;
        public ushort xhot;
        public ushort yhot;
        public ulong cursor_serial;
        public IntPtr pixels;
        public ulong atom;
        public IntPtr name;
    }

    [DllImport(LibX11)]
    public static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport(LibX11)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    public static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    public static extern int XFree(IntPtr data);

    [DllImport(LibX11)]
    public static extern int XDefaultScreen(IntPtr display);

    [DllImport(LibX11)]
    public static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    public static extern int XDisplayWidth(IntPtr display, int screen);

    [DllImport(LibX11)]
    public static extern int XDisplayHeight(IntPtr display, int screen);

    [DllImport(LibX11)]
    public static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y,
        uint width, uint height, ulong planeMask, int format);

    [DllImport(LibX11)]
    public static extern int XDestroyImage(IntPtr image);

    [DllImport(LibX11)]
    public static extern int XDisplayKeycodes(IntPtr display, out int minKeycode, out int maxKeycode);

    [DllImport(LibX11)]
    public static extern IntPtr XGetKeyboardMapping(IntPtr display, byte firstKeycode, int keycodeCount,
        out int keysymsPerKeycode);

    [DllImport(LibX11)]
    public static extern int XQueryExtension(IntPtr display, string name, out int majorOpcode,
        out int firstEvent, out int firstError);

    [DllImport(LibX11)]
    public static extern int XChangeWindowAttributes(IntPtr display, IntPtr window, ulong valueMask, IntPtr attributes);

    [DllImport(LibX11)]
    public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);

    [DllImport(LibX11)]
    public static extern int XGetEventData(IntPtr display, IntPtr cookie);

    [DllImport(LibX11)]
    public static extern void XFreeEventData(IntPtr display, IntPtr cookie);

    [DllImport(LibXi)]
    public static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);

    [DllImport(LibXi)]
    public static extern int XISelectEvents(IntPtr display, IntPtr window, ref XIEventMask masks, int count);

    [DllImport(LibXi)]
    public static extern int XIGetClientPointer(IntPtr display, IntPtr window, out int deviceId);

    [DllImport(LibXi)]
    public static extern int XIGrabDevice(IntPtr display, int deviceId, IntPtr grabWindow, IntPtr time, IntPtr cursor,
        int grabMode, int pairedDeviceMode, bool ownerEvents, ref XIEventMask mask);

    [DllImport(LibXi)]
    public static extern int XIUngrabDevice(IntPtr display, int deviceId, IntPtr time);

    [DllImport(LibX11)]
    public static extern int XGrabKeyboard(IntPtr display, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode, IntPtr time);

    [DllImport(LibX11)]
    public static extern int XUngrabKeyboard(IntPtr display, IntPtr time);

    [DllImport(LibXfixes)]
    public static extern int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport(LibXfixes)]
    public static extern IntPtr XFixesGetCursorImage(IntPtr display);
}
