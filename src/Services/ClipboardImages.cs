using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using screengrab.Models;

namespace screengrab.Services;

/// <summary>Puts screenshots on the clipboard.</summary>
public static class ClipboardImages
{
    /// <summary>
    /// What's on the clipboard. X11 has no clipboard store of its own: the program that copied
    /// hands the picture over (as a PNG) when another program pastes, so it has to be kept, even
    /// after the window that copied it has closed.
    /// </summary>
    private static Bitmap? _current;

    /// <summary>Copies the picture; false if the clipboard wasn't available.</summary>
    public static async Task<bool> CopyAsync(TopLevel topLevel, CaptureImage image)
    {
        if (topLevel.Clipboard is not { } clipboard)
            return false;

        var bitmap = image.ToBitmap();
        try
        {
            await ClipboardExtensions.SetBitmapAsync(clipboard, bitmap);
        }
        catch (Exception)
        {
            // The clipboard can refuse (another program holding it, say); report it instead.
            bitmap.Dispose();
            return false;
        }
        _current?.Dispose();
        _current = bitmap;
        return true;
    }
}
