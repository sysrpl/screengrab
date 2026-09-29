namespace screengrab.Services;

/// <summary>
/// Starts Screen Grab when the user logs in, with an XDG autostart entry
/// (~/.config/autostart/screengrab.desktop), which Cinnamon and other desktops read.
/// It starts in the system tray, as it always does.
/// </summary>
public static class Autostart
{
    private static string EntryPath
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "screengrab.desktop");
        }
    }

    public static bool IsEnabled => File.Exists(EntryPath);

    /// <summary>Turns starting at login on or off. Throws IOException or UnauthorizedAccessException.</summary>
    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(EntryPath);
            return;
        }

        var program = Environment.ProcessPath ?? throw new IOException("Couldn't find Screen Grab's program file.");
        // Run as "dotnet screengrab.dll" (in development), the process is dotnet itself.
        var command = Path.GetFileNameWithoutExtension(program) == "dotnet"
            ? $"\"{program}\" \"{Path.Combine(AppContext.BaseDirectory, "screengrab.dll")}\""
            : $"\"{program}\"";
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath,
            $"""
            [Desktop Entry]
            Type=Application
            Name=Screen Grab
            Comment=Take screenshots with Print Screen, even while a menu is open
            Exec={command}
            Icon=screen-grab
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }
}
