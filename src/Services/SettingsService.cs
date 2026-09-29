using System.Text.Json;
using screengrab.Models;

namespace screengrab.Services;

/// <summary>A Cinnamon shortcut Screen Grab took away, so it can be given back.</summary>
public sealed record CinnamonBinding(string Schema, string Key, string Accelerator);

/// <summary>Values Screen Grab remembers between sessions.</summary>
public sealed class AppSettings
{
    /// <summary>The <see cref="Models.Hotkey.Id"/> of the key that takes a screenshot.</summary>
    public string Hotkey { get; set; } = Models.Hotkey.All[0].Id;

    /// <summary>Where screenshots are saved; empty means <see cref="ImageFiles.DefaultFolder"/>.</summary>
    public string SaveFolder { get; set; } = "";

    public bool IncludePointer { get; set; }

    /// <summary>How long the Delayed button waits before taking the screenshot.</summary>
    public int DelaySeconds { get; set; } = 5;

    /// <summary>Whether the picture is shrunk to fit the window (rather than shown at 100%).</summary>
    public bool FitToWindow { get; set; } = true;

    /// <summary>Cinnamon shortcuts removed so they don't take a screenshot of their own too.</summary>
    public List<CinnamonBinding> ReleasedCinnamonBindings { get; set; } = [];
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as settings.json in the app data folder
/// (~/.config/screengrab on Linux).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsService(string? folder = null)
    {
        folder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "screengrab");
        _path = Path.Combine(folder, "settings.json");
        Settings = Load();
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Saves through a temporary file, so a crash can't leave a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        Settings = settings;
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving settings must never stop the window from closing.
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    /// <summary>The saved settings, or the defaults if there are none or the file can't be read.</summary>
    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to the defaults; the next save writes a good file again.
        }
        return new AppSettings();
    }
}
