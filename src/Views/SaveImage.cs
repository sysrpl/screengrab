using Avalonia.Controls;
using Avalonia.Platform.Storage;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>Save as: choose a file, as PNG, JPG or GIF, and write the screenshot to it.</summary>
public static class SaveImage
{
    private static readonly FilePickerFileType[] FileTypes =
    [
        new("PNG image") { Patterns = ["*.png", "*.PNG"] },
        new("JPG image") { Patterns = ["*.jpg", "*.JPG"] },
        new("GIF image") { Patterns = ["*.gif", "*.GIF"] },
    ];

    /// <summary>
    /// Asks where to save, then saves. Returns the file's path, or null if cancelled or it
    /// failed (the user has been told why).
    /// </summary>
    public static async Task<string?> SaveAsAsync(Window owner, CaptureImage image, string folder)
    {
        IStorageFolder? startFolder = null;
        try
        {
            Directory.CreateDirectory(folder);
            startFolder = await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Start wherever the file chooser likes.
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save screenshot",
            SuggestedFileName = ImageFiles.FileName(image.Time),
            SuggestedStartLocation = startFolder,
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = FileTypes,
        });
        if (file?.TryGetLocalPath() is not { } path)
            return null;

        // A name typed without an extension is saved as PNG.
        if (!Path.HasExtension(path))
            path += ".png";
        if (!ImageEncoder.IsSupported(path))
        {
            await MessageDialog.ShowAsync(owner, "Choose PNG, JPG or GIF",
                $"Screenshots can be saved as .png, .jpg or .gif files, not {Path.GetExtension(path)}.", isError: true);
            return null;
        }

        try
        {
            await Task.Run(() => ImageEncoder.Save(image, path));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageDialog.ShowAsync(owner, "Couldn't save the screenshot", $"{ImageFiles.Display(path)}: {ex.Message}", isError: true);
            return null;
        }
    }
}
