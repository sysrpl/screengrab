using System.Collections.ObjectModel;
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
/// or save it, and this session's screenshots down the side. The window lives as long as the
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

    /// <summary>Adds a new screenshot to the top of the list and shows it.</summary>
    private CaptureItem AddToHistory(CaptureImage image)
    {
        var item = new CaptureItem(image);
        _history.Insert(0, item);
        while (_history.Count > MaxHistory)
            RemoveFromHistory(_history[^1]);
        HistoryList.SelectedItem = item;
        return item;
    }

    private void RemoveFromHistory(CaptureItem item)
    {
        _history.Remove(item);
        item.Thumbnail.Dispose();
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentItem is not { } item)
            return;
        var index = _history.IndexOf(item);
        RemoveFromHistory(item);
        HistoryList.SelectedItem = _history.Count == 0 ? null : _history[Math.Min(index, _history.Count - 1)];
        SetStatus("Removed the screenshot from the list.");
    }

    /// <summary>Shows a screenshot from the list, or the "press Print Screen" message for none.</summary>
    private void ShowItem(CaptureItem? item)
    {
        var old = Picture.Source;
        Picture.Source = item?.Image.ToBitmap();
        old?.Dispose();

        EmptyMessage.IsVisible = item is null;
        PictureScroller.IsVisible = item is not null;
        foreach (var button in new Button[] { CopyButton, SaveButton, SaveAsButton, ScreenButton, ZoomButton, DeleteButton })
            button.IsEnabled = item is not null;
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
