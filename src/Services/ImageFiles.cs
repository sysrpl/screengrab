namespace screengrab.Services;

/// <summary>Where screenshots are saved, and what they're called.</summary>
public static class ImageFiles
{
    /// <summary>~/Pictures/Screenshots (the Pictures folder follows the user's XDG settings).</summary>
    public static string DefaultFolder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "Screenshots");
        }
    }

    /// <summary>The folder from the settings, or the default when that's empty.</summary>
    public static string Folder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SaveFolder) ? DefaultFolder : settings.SaveFolder;

    /// <summary>The file name for a screenshot taken at <paramref name="time"/>.</summary>
    public static string FileName(DateTime time) => $"Screenshot {time:yyyy-MM-dd HH-mm-ss}.png";

    /// <summary>A path in <paramref name="folder"/> that isn't taken yet: "Screenshot … (2).png" and so on.</summary>
    public static string NewPath(string folder, DateTime time)
    {
        var name = Path.GetFileNameWithoutExtension(FileName(time));
        var path = Path.Combine(folder, name + ".png");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}).png");
        return path;
    }

    /// <summary>A path shortened with ~ for the user's home folder, for messages.</summary>
    public static string Display(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }
}
