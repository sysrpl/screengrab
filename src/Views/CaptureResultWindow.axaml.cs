using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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
        Opened += (_, _) =>
        {
            Activate();
            CopyButton.Focus();
        };
        Closed += (_, _) => _bitmap.Dispose();
    }

    /// <summary>A path the capture was saved to, if any.</summary>
    public string? SavedPath { get; private set; }

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

    private async void Cloud_Click(object? sender, RoutedEventArgs e) =>
        await new S3UploadWindow(_image).ShowModalAsync(this);

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (await SaveImage.SaveAsAsync(this, _image, _saveFolder()) is { } path)
            SavedPath = path;
    }
}
