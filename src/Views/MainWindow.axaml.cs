using System.Collections.ObjectModel;
using Amazon.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>
/// Screen Grab's window: the screenshot being looked at, the tools to select part of it and copy
/// or save it, and the latest screenshots down the side (kept between sessions). The window lives as long as the
/// program; closing it only hides it, and the tray icon or the hotkey brings it back. The parts
/// live in partial files: Capture (the hotkey, taking screenshots, the tray icon) and Output
/// (copying, saving, selecting a monitor, zooming).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Screenshots are big (4 bytes a pixel), so only the latest few are kept.</summary>
    private const int MaxHistory = 12;

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<CaptureItem> _history = [];
    private readonly HistoryStore _historyStore = new();

    /// <summary>Whether the last session's list has been read; until then the list isn't saved, so it can't overwrite it.</summary>
    private bool _historyLoaded;
    private bool _quitting;

    /// <summary>For the XAML designer.</summary>
    public MainWindow() : this(new SettingsService())
    {
    }

    public MainWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        InitializeComponent();

        HistoryList.ItemsSource = _history;
        HistoryList.SelectionChanged += (_, _) => ShowItem(HistoryList.SelectedItem as CaptureItem);
        Picture.SelectionChanged += (_, _) => UpdateSelectionText();

        InitializeCapture();
        InitializeOutput();

        KeyDown += Window_KeyDown;
        Closing += Window_Closing;
        ShowItem(null);
        _ = LoadHistoryAsync();
    }

    /// <summary>The screenshot being shown, or null.</summary>
    private CaptureItem? CurrentItem => HistoryList.SelectedItem as CaptureItem;

    /// <summary>Brings the window back from the tray (or from behind other windows).</summary>
    public void ShowWindow()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers == KeyModifiers.Control;
        var controlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.N when control:
                CaptureNow_Click(sender, e);
                break;
            case Key.C when control:
                Copy_Click(sender, e);
                break;
            case Key.S when control:
                Save_Click(sender, e);
                break;
            case Key.S when controlShift:
                SaveAs_Click(sender, e);
                break;
            case Key.Q when control:
                Quit();
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                Delete_Click(sender, e);
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.Shift:
                DeleteEverywhere_Click(sender, e);
                break;
            case Key.Escape when Picture.Selection is not null:
                Picture.Selection = null;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Closing hides the window; the program keeps running in the tray. Quit (Ctrl+Q or the tray menu) ends it.</summary>
    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        SaveSettings();
        if (_quitting)
            return;
        e.Cancel = true;
        Hide();
    }

    public void Quit()
    {
        _quitting = true;
        SaveSettings();
        TrayIcon.IsVisible = false;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void SaveSettings() => _settingsService.Save(_settings);

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await new AboutWindow().ShowModalAsync(this);

    private async void Preferences_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new PreferencesWindow(_settingsService, _settings);
        if (await dialog.ShowModalAsync<bool>(this))
        {
            SaveSettings();
            ApplySettings();
        }
        else
        {
            // Cinnamon's shortcuts may have changed even if the rest was cancelled.
            await UpdateHotkeyTextAsync();
        }
    }

    /// <summary>
    /// Puts the last session's screenshots in the list, below any taken since the program started.
    /// </summary>
    private async Task LoadHistoryAsync()
    {
        var stored = await Task.Run(_historyStore.Load);
        foreach (var capture in stored)
        {
            if (_history.Count >= MaxHistory)
                break;
            var item = new CaptureItem(capture.Image, capture.Id);
            item.SavedPaths.AddRange(capture.SavedPaths);
            item.Uploads.AddRange(capture.Uploads);
            _history.Add(item);
        }
        _historyLoaded = true;
        _historyStore.DeleteOrphans(_history.Select(i => i.Id));
        SaveHistory();
        HistoryList.SelectedItem ??= _history.FirstOrDefault();
    }

    /// <summary>Writes the list (not the pictures, which are written once each) for next time.</summary>
    private void SaveHistory()
    {
        if (_historyLoaded)
            _historyStore.SaveIndex(_history);
    }

    /// <summary>Adds a new screenshot to the top of the list and shows it.</summary>
    private CaptureItem AddToHistory(CaptureImage image)
    {
        var item = new CaptureItem(image);
        _history.Insert(0, item);
        _ = Task.Run(() => _historyStore.SaveImage(item));
        while (_history.Count > MaxHistory)
            RemoveFromHistory(_history[^1]);
        HistoryList.SelectedItem = item;
        SaveHistory();
        return item;
    }

    private void RemoveFromHistory(CaptureItem item)
    {
        _history.Remove(item);
        _historyStore.DeleteImage(item.Id);
        item.Thumbnail.Dispose();
        SaveHistory();
    }

    /// <summary>Removes a screenshot from the list and shows the one that took its place.</summary>
    private void RemoveAndShowNext(CaptureItem item)
    {
        // Gone already if newer screenshots pushed it off the end while a dialog was open.
        var index = _history.IndexOf(item);
        if (index < 0)
            return;
        RemoveFromHistory(item);
        HistoryList.SelectedItem = _history.Count == 0 ? null : _history[Math.Min(index, _history.Count - 1)];
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentItem is not { } item)
            return;
        RemoveAndShowNext(item);
        SetStatus("Removed the screenshot from the list.");
    }

    /// <summary>
    /// Deletes every upload to S3 (clearing CloudFront's cache too) and every file the screenshot
    /// was saved to, then removes it from the list. If something can't be deleted, it stops there,
    /// and the screenshot stays in the list with what's left to delete.
    /// </summary>
    private async void DeleteEverywhere_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentItem is not { } item)
            return;

        var places = item.Uploads.Select(u => $"s3://{u.Bucket}/{u.Key}")
            .Concat(item.SavedPaths.Where(File.Exists).Select(ImageFiles.Display))
            .ToList();
        var message = places.Count == 0
            ? "It hasn't been saved or uploaded, so this only removes it from the list."
            : $"This deletes\n\n{string.Join("\n", places)}\n\nand removes it from the list. It can't be undone.";
        if (!await ConfirmDialog.AskAsync(this, "Delete everywhere", message))
            return;

        var cacheWarnings = new List<string>();
        if (item.Uploads.Count > 0)
        {
            if (LoadS3Keys(out var error) is not { } settings)
            {
                await ShowErrorAsync("Couldn't delete the uploaded files", error);
                return;
            }
            foreach (var upload in item.Uploads.ToList())
            {
                try
                {
                    if (await S3Uploader.DeleteAsync(settings, upload) is { } warning)
                        cacheWarnings.Add(warning);
                }
                catch (AmazonServiceException ex)
                {
                    await ShowErrorAsync("Couldn't delete the uploaded file",
                        $"S3 refused to delete s3://{upload.Bucket}/{upload.Key} (the keys need s3:DeleteObject): {ex.Message}");
                    return;
                }
                catch (Exception ex) when (ex is AmazonClientException or System.Net.Http.HttpRequestException)
                {
                    await ShowErrorAsync("Couldn't delete the uploaded file", $"Couldn't reach S3: {ex.Message}");
                    return;
                }
                item.Uploads.Remove(upload);
                SaveHistory();
                if (CurrentItem == item)
                    CopyLinkButton.IsEnabled = item.Uploads.Count > 0;
            }
        }

        foreach (var path in item.SavedPaths.ToList())
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowErrorAsync("Couldn't delete the saved file", $"{ImageFiles.Display(path)}: {ex.Message}");
                return;
            }
            item.SavedPaths.Remove(path);
            SaveHistory();
        }

        RemoveAndShowNext(item);
        SetStatus(places.Count == 0 ? "Removed the screenshot from the list."
            : places.Count == 1 ? $"Deleted {places[0]}."
            : $"Deleted {places.Count} files.");
        if (cacheWarnings.Count > 0)
            await MessageDialog.ShowAsync(this, "Deleted, but CloudFront may still show it", string.Join("\n\n", cacheWarnings), isError: false);
    }

    /// <summary>The keys from the Cloud window's settings, or null with the reason why not.</summary>
    private static S3Settings? LoadS3Keys(out string error)
    {
        error = "";
        try
        {
            var settings = new S3SettingsStore().Load();
            if (settings.AccessKeyId.Length > 0 && settings.SecretAccessKey.Length > 0)
                return settings;
            error = "There are no AWS keys. Set them with the gear button in the Cloud window.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"Couldn't read the S3 settings: {ex.Message}";
        }
        return null;
    }

    /// <summary>Shows a screenshot from the list, or the "press Print Screen" message for none.</summary>
    private void ShowItem(CaptureItem? item)
    {
        var old = Picture.Source;
        Picture.Source = item?.Image.ToBitmap();
        old?.Dispose();

        EmptyMessage.IsVisible = item is null;
        PictureScroller.IsVisible = item is not null;
        foreach (var button in new Button[] { CopyButton, SaveButton, SaveAsButton, ScreenButton, ZoomButton, DeleteButton, DeleteEverywhereButton })
            button.IsEnabled = item is not null;
        CopyLinkButton.IsEnabled = item?.LatestUpload is not null;
        UpdateSelectionText();
        UpdateZoomLabel();
    }

    private void UpdateSelectionText()
    {
        SelectionText.Text = Picture.Source is null ? ""
            : Picture.Selection is { } s ? $"Selected {s.Width} × {s.Height} at {s.X}, {s.Y} · Escape clears"
            : "Drag on the screenshot to select an area";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>For dialogs that can't wait for the window: show it first so they have an owner on screen.</summary>
    private async Task ShowErrorAsync(string heading, string message)
    {
        ShowWindow();
        await MessageDialog.ShowAsync(this, heading, message, isError: true);
    }
}
