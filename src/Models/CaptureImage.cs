using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace screengrab.Models;

/// <summary>
/// A screenshot as 32-bit BGRA pixels, top row first, with no padding between rows (the layout
/// of Avalonia's <see cref="PixelFormat.Bgra8888"/>). The alpha is always 255.
/// </summary>
public sealed class CaptureImage
{
    public CaptureImage(int width, int height, byte[] pixels, DateTime time)
    {
        if (pixels.Length != width * height * 4)
            throw new ArgumentException("The pixel data doesn't match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        Time = time;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>When the picture was taken.</summary>
    public DateTime Time { get; }

    public PixelRect Bounds => new(0, 0, Width, Height);

    /// <summary>A copy of part of the picture. <paramref name="area"/> is clipped to the picture.</summary>
    public CaptureImage Crop(PixelRect area)
    {
        area = area.Intersect(Bounds);
        if (area.Width <= 0 || area.Height <= 0)
            throw new ArgumentException("The area is outside the picture.", nameof(area));

        var pixels = new byte[area.Width * area.Height * 4];
        var rowBytes = area.Width * 4;
        for (var y = 0; y < area.Height; y++)
            Buffer.BlockCopy(Pixels, ((area.Y + y) * Width + area.X) * 4, pixels, y * rowBytes, rowBytes);
        return new CaptureImage(area.Width, area.Height, pixels, Time);
    }

    /// <summary>A new Avalonia bitmap of the picture. Safe to call from any thread.</summary>
    public Bitmap ToBitmap()
    {
        var handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        try
        {
            // The bitmap copies the pixels, so the array can be unpinned straight away.
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(Width, Height), new Vector(96, 96), Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A small copy for lists, no bigger than <paramref name="maxWidth"/> x <paramref name="maxHeight"/>.</summary>
    public Bitmap ToThumbnail(int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / Width, (double)maxHeight / Height));
        var size = new PixelSize(Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)));
        using var full = ToBitmap();
        return full.CreateScaledBitmap(size, BitmapInterpolationMode.HighQuality);
    }
}
