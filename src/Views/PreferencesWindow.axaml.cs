using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>
/// Preferences. OK copies the choices into the settings (the caller saves them). Taking
/// shortcuts from Cinnamon, or giving them back, happens at once and is saved straight away, as
/// it changes the desktop's settings rather than Screen Grab's.
/// </summary>
public partial class PreferencesWindow : DialogWindow
{
    /// <summary>Cinnamon's names for its screenshot settings, as its Keyboard settings show them.</summary>
    private static readonly Dictionary<string, string> CinnamonNames = new()
    {
        ["screenshot"] = "Take a screenshot",
        ["screenshot-clip"] = "Copy a screenshot to clipboard",
        ["window-screenshot"] = "Take a screenshot of a window",
        ["window-screenshot-clip"] = "Copy a screenshot of a window to clipboard",
        ["area-screenshot"] = "Take a screenshot of an area",
        ["area-screenshot-clip"] = "Copy a screenshot of an area to clipboard",
    };

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private List<CinnamonBinding> _conflicts = [];

    /// <summary>For the XAML designer.</summary>
    public PreferencesWindow() : this(new SettingsService(), new AppSettings())
    {
    }

    public PreferencesWindow(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
        InitializeComponent();

        HotkeyCombo.ItemsSource = Hotkey.All;
        HotkeyCombo.SelectedItem = Hotkey.FromId(settings.Hotkey);
        FolderBox.Text = ImageFiles.Folder(settings);
        PointerCheck.IsChecked = settings.IncludePointer;
        DelaySpin.Value = Math.Clamp(settings.DelaySeconds, 1, 60);
        AutostartCheck.IsChecked = Autostart.IsEnabled;

        HotkeyCombo.SelectionChanged += async (_, _) => await RefreshConflictsAsync();
        Opened += async (_, _) => await RefreshConflictsAsync();
    }

    private Hotkey SelectedHotkey => HotkeyCombo.SelectedItem as Hotkey ?? Hotkey.All[0];

    private static string Describe(CinnamonBinding binding) =>
        $"“{CinnamonNames.GetValueOrDefault(binding.Key, binding.Key)}”";

    /// <summary>Shows whether Cinnamon also uses the chosen hotkey, or has shortcuts to get back.</summary>
    private async Task RefreshConflictsAsync()
    {
        var hotkey = SelectedHotkey;
        _conflicts = await CinnamonKeys.FindConflictsAsync(hotkey);
        if (hotkey != SelectedHotkey)
            return; // Changed again while gsettings ran; that refresh will finish the job.

        if (_conflicts.Count > 0)
        {
            ConflictText.Text = $"Cinnamon also uses {hotkey.Name}, for {string.Join(" and ", _conflicts.Select(Describe))}, " +
                "so pressing it with no menu open takes two screenshots.";
            ConflictButton.Content = "Stop Cinnamon using it";
            ConflictPanel.IsVisible = true;
        }
        else if (_settings.ReleasedCinnamonBindings.Count > 0)
        {
            var released = _settings.ReleasedCinnamonBindings;
            ConflictText.Text = $"Screen Grab has taken {string.Join(", ", released.Select(b => b.Accelerator).Distinct())} " +
                $"from Cinnamon's {string.Join(" and ", released.Select(Describe))}.";
            ConflictButton.Content = "Give it back";
            ConflictPanel.IsVisible = true;
        }
        else
        {
            ConflictPanel.IsVisible = false;
        }
    }

    private async void Conflict_Click(object? sender, RoutedEventArgs e)
    {
        ConflictButton.IsEnabled = false;
        try
        {
            if (_conflicts.Count > 0)
            {
                if (!await CinnamonKeys.RemoveAsync(_conflicts))
                {
                    await MessageDialog.ShowAsync(this, "Couldn't change Cinnamon's shortcuts", "gsettings failed. You can remove the shortcut in Keyboard settings > Shortcuts > System > Screenshots and Recording.", isError: true);
                    return;
                }
                _settings.ReleasedCinnamonBindings.AddRange(_conflicts.Where(c => !_settings.ReleasedCinnamonBindings.Contains(c)));
            }
            else
            {
                if (!await CinnamonKeys.RestoreAsync(_settings.ReleasedCinnamonBindings))
                {
                    await MessageDialog.ShowAsync(this, "Couldn't change Cinnamon's shortcuts", "gsettings failed. You can set the shortcut again in Keyboard settings > Shortcuts > System > Screenshots and Recording.", isError: true);
                    return;
                }
                _settings.ReleasedCinnamonBindings.Clear();
            }
            _settingsService.Save(_settings);
        }
        finally
        {
            ConflictButton.IsEnabled = true;
            await RefreshConflictsAsync();
        }
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        if (Directory.Exists(FolderBox.Text))
            start = await StorageProvider.TryGetFolderFromPathAsync(FolderBox.Text);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the save folder",
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            FolderBox.Text = path;
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var autostart = AutostartCheck.IsChecked == true;
        if (autostart != Autostart.IsEnabled)
        {
            try
            {
                Autostart.Set(autostart);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await MessageDialog.ShowAsync(this, "Couldn't change starting at login", ex.Message, isError: true);
                return;
            }
        }

        _settings.Hotkey = SelectedHotkey.Id;
        var folder = FolderBox.Text?.Trim() ?? "";
        // The default is stored as empty, so it follows the Pictures folder if that moves.
        _settings.SaveFolder = folder == ImageFiles.DefaultFolder ? "" : folder;
        _settings.IncludePointer = PointerCheck.IsChecked == true;
        _settings.DelaySeconds = (int)(DelaySpin.Value ?? 5);
        CloseWith(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
