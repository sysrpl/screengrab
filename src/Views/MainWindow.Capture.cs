using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>Taking screenshots: the global hotkey, the Capture buttons, and the tray icon.</summary>
public partial class MainWindow
{
    /// <summary>Time for the window to disappear from the screen before a screenshot of it is taken.</summary>
    private static readonly TimeSpan HideTime = TimeSpan.FromMilliseconds(400);

    private readonly HotkeyListener _listener = new();
    private NativeMenuItem _trayDelayItem = null!;
    private string? _listenerError;
    private bool _capturing;
    private SelectionWindow? _selectionWindow;

    /// <summary>The system tray icon; App puts it in the tray.</summary>
    public TrayIcon TrayIcon { get; private set; } = null!;

    private void InitializeCapture()
    {
        TrayIcon = CreateTrayIcon();
        _listener.Captured += image => Dispatcher.UIThread.Post(() => _ = SelectAndShowAsync(image, showWindowAfter: false));
        _listener.Failed += message => Dispatcher.UIThread.Post(() => _ = ShowErrorAsync("Couldn't take a screenshot", message));
        ApplySettings();
    }

    /// <summary>Starts watching for the hotkey. Called once, when the program starts.</summary>
    public void StartListening()
    {
        _listenerError = _listener.Start();
        _ = UpdateHotkeyTextAsync();
    }

    /// <summary>Puts the settings into effect after they're loaded or changed in Preferences.</summary>
    private void ApplySettings()
    {
        var hotkey = Hotkey.FromId(_settings.Hotkey);
        _listener.Hotkey = hotkey;
        _listener.IncludePointer = _settings.IncludePointer;
        EmptyHotkeyText.Text = $"Press {hotkey.Name} to take a screenshot";
        DelayLabel.Text = $"In {_settings.DelaySeconds} s";
        _trayDelayItem.Header = $"Take a screenshot in {_settings.DelaySeconds} seconds";
        _ = UpdateHotkeyTextAsync();
    }

    /// <summary>The status bar's note on the hotkey: working, clashing with Cinnamon, or not available.</summary>
    private async Task UpdateHotkeyTextAsync()
    {
        var hotkey = Hotkey.FromId(_settings.Hotkey);
        if (_listenerError is not null)
        {
            HotkeyText.Text = $"{hotkey.Name} is off: {_listenerError}";
            HotkeyText.Classes.Set("error", true);
            return;
        }

        HotkeyText.Classes.Set("error", false);
        HotkeyText.Text = $"{hotkey.Name} takes a screenshot";
        var conflicts = await CinnamonKeys.FindConflictsAsync(hotkey);
        if (conflicts.Count > 0)
            HotkeyText.Text = $"{hotkey.Name} takes a screenshot (Cinnamon's too; see Preferences)";
    }

    private async void CaptureNow_Click(object? sender, RoutedEventArgs e) => await CaptureFromAppAsync(0);

    private async void CaptureDelayed_Click(object? sender, RoutedEventArgs e) =>
        await CaptureFromAppAsync(_settings.DelaySeconds);

    /// <summary>
    /// A screenshot asked for from the window or the tray: hide the window so it isn't in the
    /// picture, wait if asked to, then take it.
    /// </summary>
    private async Task CaptureFromAppAsync(int delaySeconds)
    {
        if (_capturing)
            return;
        _capturing = true;
        var wasVisible = IsVisible;
        Hide();
        try
        {
            if (delaySeconds == 0)
                await Task.Delay(HideTime);
            for (var left = delaySeconds; left > 0; left--)
            {
                TrayIcon.ToolTipText = $"Screen Grab: screenshot in {left}…";
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            var includePointer = _settings.IncludePointer;
            var image = await Task.Run(() => ScreenCapture.Capture(includePointer));
            TrayIcon.ToolTipText = "Screen Grab";
            await SelectAndShowAsync(image, showWindowAfter: wasVisible);
        }
        catch (CaptureException ex)
        {
            await ShowErrorAsync("Couldn't take a screenshot", ex.Message);
        }
        finally
        {
            TrayIcon.ToolTipText = "Screen Grab";
            _capturing = false;
        }
    }

    /// <summary>
    /// The rest of every screenshot, however it was taken: select the area to keep on the full
    /// screen picture, then show what was captured, to copy or save. The capture also goes in
    /// this window's list.
    /// </summary>
    private async Task SelectAndShowAsync(CaptureImage screenshot, bool showWindowAfter)
    {
        // One at a time: the hotkey pressed while selecting is ignored.
        if (_selectionWindow is not null)
            return;

        _selectionWindow = new SelectionWindow(screenshot);
        CaptureImage? captured;
        try
        {
            captured = await _selectionWindow.SelectAsync();
        }
        finally
        {
            _selectionWindow = null;
        }

        if (showWindowAfter)
            ShowWindow();
        if (captured is null)
        {
            SetStatus("Screenshot cancelled.");
            return;
        }

        var item = AddToHistory(captured);
        SetStatus($"{captured.Time:HH:mm:ss}: captured {captured.Width} × {captured.Height}.");
        var result = new CaptureResultWindow(captured, () => ImageFiles.Folder(_settings));
        result.Saved += path =>
        {
            item.AddSavedPath(path);
            SaveHistory();
        };
        result.Uploaded += upload =>
        {
            item.AddUpload(upload);
            SaveHistory();
            if (CurrentItem == item)
                CopyLinkButton.IsEnabled = true;
        };
        result.Show();
    }

    private TrayIcon CreateTrayIcon()
    {
        var show = new NativeMenuItem("Show Screen Grab");
        show.Click += (_, _) => ShowWindow();
        var capture = new NativeMenuItem("Take a screenshot");
        capture.Click += async (_, _) => await CaptureFromAppAsync(0);
        _trayDelayItem = new NativeMenuItem("Take a screenshot later");
        _trayDelayItem.Click += async (_, _) => await CaptureFromAppAsync(_settings.DelaySeconds);
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        foreach (var menuItem in new NativeMenuItemBase[]
                 { show, new NativeMenuItemSeparator(), capture, _trayDelayItem, new NativeMenuItemSeparator(), quit })
            menu.Items.Add(menuItem);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://screengrab/resources/icon.png"))),
            ToolTipText = "Screen Grab",
            Menu = menu,
        };
        tray.Clicked += (_, _) => ShowWindow();
        return tray;
    }
}
