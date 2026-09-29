using System.Runtime.InteropServices;
using screengrab.Models;
using SkiaSharp;

namespace screengrab.Services;

/// <summary>Saves screenshots as PNG, JPG or GIF, chosen by the file's extension.</summary>
public static class ImageEncoder
{
    /// <summary>The extensions screenshots can be saved with.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".gif"];

    private const int JpegQuality = 92;

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Writes the picture to <paramref name="path"/>, making its folder if need be. Throws
    /// ArgumentException for an extension other than <see cref="Extensions"/>, and IOException or
    /// UnauthorizedAccessException if the file can't be written.
    /// </summary>
    public static void Save(CaptureImage image, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new ArgumentException($"Screenshots can be saved as {string.Join(", ", Extensions)} only.", nameof(path));

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        using var stream = File.Create(path);
        Write(image, stream, extension);
    }

    /// <summary>The picture as the bytes of a .png, .jpg or .gif file.</summary>
    public static byte[] Encode(CaptureImage image, string extension)
    {
        if (!Extensions.Contains(extension))
            throw new ArgumentException($"Screenshots can be saved as {string.Join(", ", Extensions)} only.", nameof(extension));
        using var stream = new MemoryStream();
        Write(image, stream, extension);
        return stream.ToArray();
    }

    /// <summary>The MIME type for a .png, .jpg or .gif file.</summary>
    public static string ContentType(string extension) => extension switch
    {
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => "image/png",
    };

    private static void Write(CaptureImage image, Stream stream, string extension)
    {
        switch (extension)
        {
            case ".gif":
                GifEncoder.Write(image, stream);
                break;
            case ".jpg":
                EncodeWithSkia(image, stream, SKEncodedImageFormat.Jpeg, JpegQuality);
                break;
            default:
                EncodeWithSkia(image, stream, SKEncodedImageFormat.Png, 100);
                break;
        }
    }

    private static void EncodeWithSkia(CaptureImage image, Stream stream, SKEncodedImageFormat format, int quality)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), image.Width * 4);
            using var data = pixmap.Encode(format, quality)
                ?? throw new IOException($"Skia couldn't encode the picture as {format}.");
            data.SaveTo(stream);
        }
        finally
        {
            handle.Free();
        }
    }
}
