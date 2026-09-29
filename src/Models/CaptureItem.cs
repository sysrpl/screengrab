using Avalonia.Media.Imaging;

namespace screengrab.Models;

/// <summary>One screenshot in the history list: the picture, a thumbnail, and everywhere it was saved and uploaded.</summary>
public sealed class CaptureItem
{
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 100;

    /// <param name="id">The name it's kept under between sessions; a new one when null.</param>
    public CaptureItem(CaptureImage image, string? id = null)
    {
        Image = image;
        Id = id ?? Guid.NewGuid().ToString("N");
        Thumbnail = image.ToThumbnail(ThumbnailWidth, ThumbnailHeight);
    }

    public string Id { get; }
    public CaptureImage Image { get; }
    public Bitmap Thumbnail { get; }

    /// <summary>Every file it was saved to, oldest first.</summary>
    public List<string> SavedPaths { get; } = [];

    /// <summary>Every upload to S3, oldest first.</summary>
    public List<S3Upload> Uploads { get; } = [];

    /// <summary>The latest upload, for Copy link.</summary>
    public S3Upload? LatestUpload => Uploads.Count == 0 ? null : Uploads[^1];

    public string Caption => $"{Image.Time:HH:mm:ss} · {Image.Width} × {Image.Height}";

    /// <summary>Records a save; saving over the same file again moves it to the end.</summary>
    public void AddSavedPath(string path)
    {
        SavedPaths.Remove(path);
        SavedPaths.Add(path);
    }

    /// <summary>Records an upload; uploading over the same file again replaces the old record.</summary>
    public void AddUpload(S3Upload upload)
    {
        Uploads.RemoveAll(u => u.Bucket == upload.Bucket && u.Key == upload.Key);
        Uploads.Add(upload);
    }
}
