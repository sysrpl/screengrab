using System.Runtime.InteropServices;
using screengrab.Interop;
using screengrab.Models;

namespace screengrab.Services;

/// <summary>
/// Watches for the screenshot hotkey anywhere on the desktop, and takes the screenshot the
/// moment it's pressed.
///
/// Ordinary global shortcuts (XGrabKey, which is what Cinnamon uses) stop working while another
/// program has the keyboard grabbed, and every open menu grabs it: that's why Print Screen does
/// nothing while GIMP's File menu is open. This listens for XInput 2 raw key events on the root
/// window instead, which the X server sends to every client that asks for them (XInput 2.1 and
/// later), grab or no grab. It only listens: the key still goes where it would have gone.
///
/// The screenshot is taken on the listener's own thread and connection, before anything else
/// can happen, so the open menu is still on screen. <see cref="Captured"/> and
/// <see cref="Failed"/> are raised on that thread.
/// </summary>
public sealed class HotkeyListener
{
    private const ulong XK_Print = 0xFF61;
    private const ulong XK_Shift_L = 0xFFE1, XK_Shift_R = 0xFFE2;
    private const ulong XK_Control_L = 0xFFE3, XK_Control_R = 0xFFE4;
    private const ulong XK_Meta_L = 0xFFE7, XK_Meta_R = 0xFFE8;
    private const ulong XK_Alt_L = 0xFFE9, XK_Alt_R = 0xFFEA;
    private const ulong XK_Super_L = 0xFFEB, XK_Super_R = 0xFFEC;

    private readonly HashSet<int> _printKeycodes = [];
    private readonly Dictionary<int, HotkeyModifiers> _modifierKeycodes = [];
    private readonly HashSet<int> _modifiersDown = [];
    private IntPtr _display;
    private int _xinputOpcode;
    private bool _printDown;
    private volatile Hotkey _hotkey = Hotkey.All[0];
    private volatile bool _includePointer;

    /// <summary>A screenshot was taken because the hotkey was pressed. Raised on the listener thread.</summary>
    public event Action<CaptureImage>? Captured;

    /// <summary>The hotkey was pressed but the screenshot failed. Raised on the listener thread.</summary>
    public event Action<string>? Failed;

    public Hotkey Hotkey
    {
        get => _hotkey;
        set => _hotkey = value;
    }

    public bool IncludePointer
    {
        get => _includePointer;
        set => _includePointer = value;
    }

    /// <summary>
    /// Connects to the X server and starts listening. Returns null when it's listening, or why
    /// it can't (not X11, or no XInput 2.1).
    /// </summary>
    public string? Start()
    {
        try
        {
            _display = X11.XOpenDisplay(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "the X11 libraries (libX11, libXi) weren't found";
        }
        if (_display == IntPtr.Zero)
            return "couldn't connect to the X server; Screen Grab works in X11 sessions only";

        if (X11.XQueryExtension(_display, "XInputExtension", out _xinputOpcode, out _, out _) == 0)
            return Close("the X server has no XInput extension");

        // Asking for 2.2 tells the server we understand 2.1's rule that raw events come through grabs.
        int major = 2, minor = 2;
        if (X11.XIQueryVersion(_display, ref major, ref minor) != X11.Success || (major == 2 && minor < 1))
            return Close($"the X server has XInput {major}.{minor}; Screen Grab needs 2.1 or later");

        ReadKeyboardMapping();
        if (_printKeycodes.Count == 0)
            return Close("the keyboard has no Print Screen key");

        SelectRawKeyEvents();

        var thread = new Thread(Run) { IsBackground = true, Name = "Hotkey listener" };
        thread.Start();
        return null;
    }

    private string Close(string reason)
    {
        X11.XCloseDisplay(_display);
        _display = IntPtr.Zero;
        return reason;
    }

    /// <summary>Finds the keycodes of Print Screen and the modifier keys (on the first shift level).</summary>
    private void ReadKeyboardMapping()
    {
        X11.XDisplayKeycodes(_display, out var min, out var max);
        var count = max - min + 1;
        var keysyms = X11.XGetKeyboardMapping(_display, (byte)min, count, out var perKeycode);
        if (keysyms == IntPtr.Zero)
            return;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var keycode = min + i;
                var keysym = (ulong)Marshal.ReadInt64(keysyms, i * perKeycode * sizeof(long));
                switch (keysym)
                {
                    case XK_Print:
                        _printKeycodes.Add(keycode);
                        break;
                    case XK_Shift_L or XK_Shift_R:
                        _modifierKeycodes[keycode] = HotkeyModifiers.Shift;
                        break;
                    case XK_Control_L or XK_Control_R:
                        _modifierKeycodes[keycode] = HotkeyModifiers.Control;
                        break;
                    case XK_Alt_L or XK_Alt_R or XK_Meta_L or XK_Meta_R:
                        _modifierKeycodes[keycode] = HotkeyModifiers.Alt;
                        break;
                    case XK_Super_L or XK_Super_R:
                        _modifierKeycodes[keycode] = HotkeyModifiers.Super;
                        break;
                }
            }
        }
        finally
        {
            X11.XFree(keysyms);
        }
    }

    private void SelectRawKeyEvents()
    {
        // One bit per event type.
        var mask = new byte[4];
        mask[X11.XI_RawKeyPress >> 3] |= (byte)(1 << (X11.XI_RawKeyPress & 7));
        mask[X11.XI_RawKeyRelease >> 3] |= (byte)(1 << (X11.XI_RawKeyRelease & 7));

        var handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
        try
        {
            var eventMask = new X11.XIEventMask
            {
                deviceid = X11.XIAllMasterDevices,
                mask_len = mask.Length,
                mask = handle.AddrOfPinnedObject(),
            };
            X11.XISelectEvents(_display, X11.XDefaultRootWindow(_display), ref eventMask, 1);
            X11.XFlush(_display);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>The listener thread: waits for raw key events for the rest of the program's life.</summary>
    private void Run()
    {
        var xevent = Marshal.AllocHGlobal(X11.XEventSize);
        while (true)
        {
            X11.XNextEvent(_display, xevent);
            if (Marshal.ReadInt32(xevent, X11.CookieTypeOffset) != X11.GenericEvent ||
                Marshal.ReadInt32(xevent, X11.CookieExtensionOffset) != _xinputOpcode ||
                X11.XGetEventData(_display, xevent) == 0)
                continue;

            try
            {
                var type = Marshal.ReadInt32(xevent, X11.CookieEvTypeOffset);
                var data = Marshal.ReadIntPtr(xevent, X11.CookieDataOffset);
                var keycode = Marshal.ReadInt32(data, X11.RawDetailOffset);
                if (type == X11.XI_RawKeyPress)
                    KeyPressed(keycode);
                else if (type == X11.XI_RawKeyRelease)
                    KeyReleased(keycode);
            }
            finally
            {
                X11.XFreeEventData(_display, xevent);
            }
        }
    }

    private void KeyPressed(int keycode)
    {
        if (_modifierKeycodes.ContainsKey(keycode))
        {
            _modifiersDown.Add(keycode);
            return;
        }
        if (!_printKeycodes.Contains(keycode) || _printDown)
            return;

        // Held down, Print Screen repeats; one screenshot per press.
        _printDown = true;
        if (ModifiersDown() == _hotkey.Modifiers)
            CaptureNow();
    }

    private void KeyReleased(int keycode)
    {
        _modifiersDown.Remove(keycode);
        if (_printKeycodes.Contains(keycode))
            _printDown = false;
    }

    private HotkeyModifiers ModifiersDown()
    {
        var modifiers = HotkeyModifiers.None;
        foreach (var keycode in _modifiersDown)
            modifiers |= _modifierKeycodes[keycode];
        return modifiers;
    }

    private void CaptureNow()
    {
        CaptureImage image;
        try
        {
            image = ScreenCapture.Capture(_display, _includePointer);
        }
        catch (CaptureException ex)
        {
            Failed?.Invoke(ex.Message);
            return;
        }
        Captured?.Invoke(image);
    }
}
