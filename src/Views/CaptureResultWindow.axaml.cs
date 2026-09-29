using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using screengrab.Interop;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>
/// What was just captured, with the choice to copy it to the clipboard or save it as a PNG,
/// JPG or GIF file.
/// </summary>
public partial class CaptureResultWindow : Window
{
    private readonly CaptureImage _image;
    private readonly Func<string> _saveFolder;
    private readonly Bitmap _bitmap;

    /// <summary>For the XAML designer.</summary>
    public CaptureResultWindow() : this(new CaptureImage(1, 1, new byte[4], DateTime.Now), () => ImageFiles.DefaultFolder)
    {
    }

    /// <param name="saveFolder">Where Save starts; asked each time, as Preferences may change it.</param>
    public CaptureResultWindow(CaptureImage image, Func<string> saveFolder)
    {
        _image = image;
        _saveFolder = saveFolder;
        InitializeComponent();

        _bitmap = image.ToBitmap();
        Preview.Source = _bitmap;

        KeyDown += Window_KeyDown;
        // Come to the front after a capture, even over an always on top window (such as the
        // piano roll's player while its F1 backdrop is up), so it starts always on top too.
        // Cinnamon has to be managing the window before it will give it the focus, hence the wait.
        Topmost = true;
        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.2) };
        _focusTimer.Tick += FocusTimer_Tick;
        Opened += (_, _) =>
        {
            CopyButton.Focus();
            DispatcherTimer.RunOnce(() =>
            {
                OverlayWindow.BringToFront(this);
                _focusTimer.Start();
            }, TimeSpan.FromSeconds(0.5));
        };
        Closed += (_, _) =>
        {
            _focusTimer.Stop();
            _bitmap.Dispose();
        };
    }

    /// <summary>Checks whether the window still has the focus, from when it's given it until it's lost.</summary>
    private readonly DispatcherTimer _focusTimer;

    /// <summary>
    /// Once another window has the focus, this becomes an ordinary window again: always on top
    /// comes off, and on the next check it moves one place down, below the window under it
    /// (Cinnamon puts a window leaving always on top at the top of the ordinary ones, over the
    /// window just clicked). The next check gives Cinnamon time to move it first. Its own dialogs
    /// (Save, Cloud, messages) having the focus counts as it having the focus.
    /// </summary>
    private void FocusTimer_Tick(object? sender, EventArgs e)
    {
        if (OverlayWindow.HasFocusWithin(this))
            return;
        if (Topmost)
        {
            Topmost = false;
            return;
        }
        _focusTimer.Stop();
        OverlayWindow.LowerOneStep(this);
    }

    /// <summary>Raised with the file's path after each save.</summary>
    public event Action<string>? Saved;

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.Control)
            return;
        if (e.Key == Key.C)
            Copy_Click(sender, e);
        else if (e.Key == Key.S)
            Save_Click(sender, e);
        else
            return;
        e.Handled = true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        // Only a failure is worth a message.
        if (!await ClipboardImages.CopyAsync(this, _image))
            await MessageDialog.ShowAsync(this, "Couldn't copy to the clipboard", "The clipboard isn't available right now. Try again, or save the image instead.", isError: true);
    }

    /// <summary>Raised after each upload to S3.</summary>
    public event Action<S3Upload>? Uploaded;

    private async void Cloud_Click(object? sender, RoutedEventArgs e)
    {
        if (await new S3UploadWindow(_image).ShowModalAsync<S3Upload?>(this) is { } upload)
            Uploaded?.Invoke(upload);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (await SaveImage.SaveAsAsync(this, _image, _saveFolder()) is { } path)
            Saved?.Invoke(path);
    }
}
