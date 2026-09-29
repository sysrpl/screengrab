using System.Numerics;
using System.Runtime.InteropServices;
using screengrab.Interop;
using screengrab.Models;

namespace screengrab.Services;

/// <summary>Something went wrong taking a screenshot; the message says what, for the user.</summary>
public sealed class CaptureException(string message) : Exception(message);

/// <summary>
/// Takes screenshots of the whole X11 screen (every monitor) by reading the root window with
/// XGetImage. This works while another program has the keyboard and mouse grabbed, such as while
/// one of its menus is open, as grabs only decide who gets input; any client can still read the
/// screen.
/// </summary>
public static class ScreenCapture
{
    /// <summary>Takes a screenshot on a connection of its own. Can be called from any thread.</summary>
    public static CaptureImage Capture(bool includePointer)
    {
        IntPtr display;
        try
        {
            display = X11.XOpenDisplay(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new CaptureException("Screen Grab needs the X11 libraries (libX11), which weren't found.");
        }
        if (display == IntPtr.Zero)
            throw new CaptureException("Couldn't connect to the X server. Screen Grab works in X11 sessions only.");

        try
        {
            return Capture(display, includePointer);
        }
        finally
        {
            X11.XCloseDisplay(display);
        }
    }

    /// <summary>Takes a screenshot on <paramref name="display"/>, from the thread that owns it.</summary>
    internal static CaptureImage Capture(IntPtr display, bool includePointer)
    {
        var time = DateTime.Now;
        var screen = X11.XDefaultScreen(display);
        var width = X11.XDisplayWidth(display, screen);
        var height = X11.XDisplayHeight(display, screen);
        var root = X11.XDefaultRootWindow(display);

        var imagePointer = X11.XGetImage(display, root, 0, 0, (uint)width, (uint)height, X11.AllPlanes, X11.ZPixmap);
        if (imagePointer == IntPtr.Zero)
            throw new CaptureException("The X server didn't return a picture of the screen.");

        byte[] pixels;
        try
        {
            var image = Marshal.PtrToStructure<X11.XImage>(imagePointer);
            pixels = ToBgra(image, width, height);
        }
        finally
        {
            X11.XDestroyImage(imagePointer);
        }

        if (includePointer)
            DrawPointer(display, pixels, width, height);

        return new CaptureImage(width, height, pixels, time);
    }

    /// <summary>Converts an XImage to BGRA with full alpha.</summary>
    private static byte[] ToBgra(X11.XImage image, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var rowBytes = width * 4;

        // The usual case, 24-bit colour in 32-bit little-endian pixels, is already BGRX.
        if (image.bits_per_pixel == 32 && image.byte_order == X11.LsbFirst &&
            image.red_mask == 0xFF0000 && image.green_mask == 0xFF00 && image.blue_mask == 0xFF)
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(image.data + y * image.bytes_per_line, pixels, y * rowBytes, rowBytes);
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return pixels;
        }

        // Anything else (16-bit, 30-bit, big-endian): pull each channel out with its mask.
        var bytesPerPixel = image.bits_per_pixel / 8;
        if (bytesPerPixel is < 2 or > 4 || image.bits_per_pixel % 8 != 0)
            throw new CaptureException($"The screen uses {image.bits_per_pixel} bits per pixel, which Screen Grab can't read.");

        var red = new Channel(image.red_mask);
        var green = new Channel(image.green_mask);
        var blue = new Channel(image.blue_mask);
        var row = new byte[image.bytes_per_line];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(image.data + y * image.bytes_per_line, row, 0, row.Length);
            for (var x = 0; x < width; x++)
            {
                ulong value = 0;
                var start = x * bytesPerPixel;
                for (var b = 0; b < bytesPerPixel; b++)
                {
                    var shift = image.byte_order == X11.LsbFirst ? b * 8 : (bytesPerPixel - 1 - b) * 8;
                    value |= (ulong)row[start + b] << shift;
                }
                var i = y * rowBytes + x * 4;
                pixels[i] = blue.Read(value);
                pixels[i + 1] = green.Read(value);
                pixels[i + 2] = red.Read(value);
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>One colour channel of a pixel, found by its mask and scaled to 0-255.</summary>
    private readonly struct Channel(ulong mask)
    {
        private readonly int _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
        private readonly ulong _max = mask == 0 ? 1 : mask >> BitOperations.TrailingZeroCount(mask);

        public byte Read(ulong pixel) => (byte)(((pixel & mask) >> _shift) * 255 / _max);
    }

    /// <summary>
    /// Draws the mouse pointer into the picture. X never includes it in XGetImage, so its
    /// picture comes from XFixes. If XFixes isn't there, the screenshot has no pointer.
    /// </summary>
    private static void DrawPointer(IntPtr display, byte[] pixels, int width, int height)
    {
        try
        {
            if (X11.XFixesQueryExtension(display, out _, out _) == 0)
                return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return;
        }

        var cursorPointer = X11.XFixesGetCursorImage(display);
        if (cursorPointer == IntPtr.Zero)
            return;

        try
        {
            var cursor = Marshal.PtrToStructure<X11.XFixesCursorImage>(cursorPointer);
            var left = cursor.x - cursor.xhot;
            var top = cursor.y - cursor.yhot;
            for (var cy = 0; cy < cursor.height; cy++)
            {
                var y = top + cy;
                if (y < 0 || y >= height)
                    continue;
                for (var cx = 0; cx < cursor.width; cx++)
                {
                    var x = left + cx;
                    if (x < 0 || x >= width)
                        continue;

                    // One premultiplied ARGB value per unsigned long.
                    var argb = (uint)Marshal.ReadInt64(cursor.pixels, (cy * cursor.width + cx) * sizeof(long));
                    var alpha = argb >> 24;
                    if (alpha == 0)
                        continue;

                    var i = (y * width + x) * 4;
                    var keep = 255 - alpha;
                    pixels[i] = (byte)((argb & 0xFF) + pixels[i] * keep / 255);
                    pixels[i + 1] = (byte)(((argb >> 8) & 0xFF) + pixels[i + 1] * keep / 255);
                    pixels[i + 2] = (byte)(((argb >> 16) & 0xFF) + pixels[i + 2] * keep / 255);
                }
            }
        }
        finally
        {
            X11.XFree(cursorPointer);
        }
    }
}
