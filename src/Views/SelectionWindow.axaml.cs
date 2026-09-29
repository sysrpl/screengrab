using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using screengrab.Interop;
using screengrab.Models;

namespace screengrab.Views;

/// <summary>
/// The screenshot shown full screen, exactly over the desktop it was taken from, dimmed with
/// 50% black, to select the part to keep: drag a rectangle, drag within 10 pixels of its edges
/// or corners to resize it, press anywhere else to start again. Enter keeps the selection (or
/// the whole screen when nothing is selected); Escape cancels.
///
/// It covers every monitor and sits above the panel, which a window-manager-managed window
/// can't do, so it's an override-redirect window that grabs the mouse and keyboard (see
/// <see cref="OverlayWindow"/>). If a menu was open when the screenshot was taken, that program
/// still holds them: the first click closes its menu, and from then on this window has them.
/// </summary>
public partial class SelectionWindow : Window
{
    private readonly CaptureImage _image;
    private readonly Bitmap _bitmap;
    private readonly TaskCompletionSource<CaptureImage?> _result = new();
    private readonly OverlayWindow.InputGrab _grab;
    private readonly DispatcherTimer _grabTimer;

    /// <summary>For the XAML designer.</summary>
    public SelectionWindow() : this(new CaptureImage(1, 1, new byte[4], DateTime.Now))
    {
    }

    public SelectionWindow(CaptureImage image)
    {
        _image = image;
        InitializeComponent();

        _bitmap = image.ToBitmap();
        Picture.Source = _bitmap;
        Picture.SelectionChanged += (_, _) => HintBar.IsVisible = Picture.Selection is null;

        // Cover the whole X screen: every monitor, as the screenshot does.
        var scaling = Screens.Primary?.Scaling ?? 1;
        Position = new PixelPoint(0, 0);
        Width = image.Width / scaling;
        Height = image.Height / scaling;
        OverlayWindow.BypassWindowManager(this);

        // Tunnel, so Enter and Escape reach the window before the picture (which clears its selection on Escape).
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        // Take the mouse and keyboard, so presses over Cinnamon's panel come here too. While
        // another program's menu is still open it holds them, so keep trying until it lets go.
        _grab = new OverlayWindow.InputGrab(this);
        _grabTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) =>
        {
            if (_grab.TryGrab())
                _grabTimer!.Stop();
        });
        Opened += (_, _) =>
        {
            Picture.Focus();
            if (!_grab.TryGrab())
                _grabTimer.Start();
        };
        Closed += (_, _) =>
        {
            _grabTimer.Stop();
            _grab.Dispose();
            _result.TrySetResult(null);
            _bitmap.Dispose();
        };
    }

    /// <summary>Shows the window and waits: the selected part of the screenshot, or null if cancelled.</summary>
    public Task<CaptureImage?> SelectAsync()
    {
        Show();
        return _result.Task;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Finish(Picture.Selection is { } selection ? _image.Crop(selection) : _image);
                break;
            case Key.Escape:
                Finish(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Finish(CaptureImage? result)
    {
        _result.TrySetResult(result);
        Close();
    }
}
