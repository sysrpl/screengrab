using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using screengrab.Models;
using SkiaSharp;

namespace screengrab.Services;

/// <summary>A screenshot read back from the history folder.</summary>
public sealed record StoredCapture(string Id, CaptureImage Image, List<string> SavedPaths, List<S3Upload> Uploads);

/// <summary>
/// Keeps the main window's list of screenshots between sessions, in the history folder
/// (~/.local/share/screengrab/history on Linux): each picture as a PNG named by its id, and
/// history.json listing them, newest first, with every file each was saved to and every upload.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly string _indexPath;

    public HistoryStore(string? folder = null)
    {
        _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "screengrab", "history");
        _indexPath = Path.Combine(_folder, "history.json");
    }

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public DateTime Time { get; set; }
        public List<string> SavedPaths { get; set; } = [];
        public List<S3Upload> Uploads { get; set; } = [];

        // The first version kept only the last save and upload; read them, never written.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SavedPath { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public S3Upload? Upload { get; set; }
    }

    /// <summary>
    /// The screenshots from history.json, newest first. Ones whose picture is missing or can't be
    /// read are left out. Slow (it decodes every PNG), so call it off the UI thread.
    /// </summary>
    public List<StoredCapture> Load()
    {
        List<Entry>? entries = null;
        try
        {
            if (File.Exists(_indexPath))
                entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_indexPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Start with an empty list; the next save writes a good file again.
        }

        var captures = new List<StoredCapture>();
        foreach (var entry in entries ?? [])
        {
            // The id becomes a file name, so it must be one of ours.
            if (!Guid.TryParseExact(entry.Id, "N", out _))
                continue;
            try
            {
                if (entry.SavedPath is { } savedPath && !entry.SavedPaths.Contains(savedPath))
                    entry.SavedPaths.Add(savedPath);
                if (entry.Upload is { } upload && !entry.Uploads.Contains(upload))
                    entry.Uploads.Add(upload);
                if (Decode(ImagePath(entry.Id), entry.Time) is { } image)
                    captures.Add(new StoredCapture(entry.Id, image, entry.SavedPaths, entry.Uploads));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave this one out.
            }
        }
        return captures;
    }

    /// <summary>Writes history.json for <paramref name="items"/>, through a temporary file.</summary>
    public void SaveIndex(IEnumerable<CaptureItem> items)
    {
        var entries = items.Select(i => new Entry { Id = i.Id, Time = i.Image.Time, SavedPaths = [.. i.SavedPaths], Uploads = [.. i.Uploads] }).ToList();
        var temporary = _indexPath + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporary, _indexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list still works for this session.
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    /// <summary>Writes an item's picture as a PNG, through a temporary file. Safe to call from any thread.</summary>
    public void SaveImage(CaptureItem item)
    {
        var temporary = Path.Combine(_folder, item.Id + ".tmp.png");
        try
        {
            ImageEncoder.Save(item.Image, temporary);
            File.Move(temporary, ImagePath(item.Id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    public void DeleteImage(string id)
    {
        try { File.Delete(ImagePath(id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes pictures in the folder that aren't in <paramref name="keep"/>: left over from a
    /// removal that happened while the picture was still being written, or a crash.
    /// </summary>
    public void DeleteOrphans(IEnumerable<string> keep)
    {
        var ids = keep.ToHashSet();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_folder, "*.png"))
                if (!ids.Contains(Path.GetFileName(path).Split('.')[0]))
                    File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again next time.
        }
    }

    private string ImagePath(string id) => Path.Combine(_folder, id + ".png");

    /// <summary>A PNG as a <see cref="CaptureImage"/>, or null if it's missing or not a picture.</summary>
    private static CaptureImage? Decode(string path, DateTime time)
    {
        if (!File.Exists(path))
            return null;
        using var decoded = SKBitmap.Decode(path);
        if (decoded is null)
            return null;
        using var converted = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
        if (decoded.ColorType != SKColorType.Bgra8888 && converted is null)
            return null;
        var bitmap = converted ?? decoded;

        var rowBytes = bitmap.Width * 4;
        var pixels = new byte[rowBytes * bitmap.Height];
        var source = bitmap.GetPixels();
        for (var y = 0; y < bitmap.Height; y++)
            Marshal.Copy(source + y * bitmap.RowBytes, pixels, y * rowBytes, rowBytes);
        return new CaptureImage(bitmap.Width, bitmap.Height, pixels, time);
    }
}
