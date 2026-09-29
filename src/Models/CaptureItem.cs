using Avalonia.Media.Imaging;

namespace screengrab.Models;

/// <summary>One screenshot in the history list: the picture, a thumbnail, and where it was saved.</summary>
public sealed class CaptureItem
{
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 100;

    public CaptureItem(CaptureImage image)
    {
        Image = image;
        Thumbnail = image.ToThumbnail(ThumbnailWidth, ThumbnailHeight);
    }

    public CaptureImage Image { get; }
    public Bitmap Thumbnail { get; }

    /// <summary>The last file it was saved to, if any.</summary>
    public string? SavedPath { get; set; }

    public string Caption => $"{Image.Time:HH:mm:ss} · {Image.Width} × {Image.Height}";
}
