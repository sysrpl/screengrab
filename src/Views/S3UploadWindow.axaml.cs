using Amazon.Runtime;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>
/// Uploads a capture to Amazon S3, as PNG, GIF or JPG, under a file name of the user's choice,
/// into the bucket and folder set with the gear button. When the upload is done, the file's link
/// (on the bucket's CloudFront domain, if it has one) goes on the clipboard, opens in the default
/// browser, and the window closes with an <see cref="S3Upload"/>.
/// </summary>
public partial class S3UploadWindow : DialogWindow
{
    private readonly CaptureImage _image;
    private readonly S3SettingsStore _store = new();
    private readonly CancellationTokenSource _cancel = new();
    private S3Settings _settings = new();
    private bool _uploading;

    /// <summary>For the XAML designer.</summary>
    public S3UploadWindow() : this(new CaptureImage(1, 1, new byte[4], DateTime.Now))
    {
    }

    public S3UploadWindow(CaptureImage image)
    {
        _image = image;
        InitializeComponent();

        NameBox.Text = Path.GetFileNameWithoutExtension(ImageFiles.FileName(image.Time));
        try
        {
            _settings = _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError(ex.Message);
        }
        FormatRadio(_settings.Format).IsChecked = true;

        NameBox.TextChanged += (_, _) => ShowDestination();
        foreach (var radio in new[] { PngRadio, GifRadio, JpgRadio })
            radio.IsCheckedChanged += (_, _) => ShowDestination();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_uploading)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Closed += (_, _) => _cancel.Cancel();
        ShowDestination();
    }

    private RadioButton FormatRadio(string extension) => extension switch
    {
        ".gif" => GifRadio,
        ".jpg" => JpgRadio,
        _ => PngRadio,
    };

    private string Extension => GifRadio.IsChecked == true ? ".gif" : JpgRadio.IsChecked == true ? ".jpg" : ".png";

    /// <summary>The file name as typed, without an extension of ours if one was typed.</summary>
    private string BaseName
    {
        get
        {
            var name = NameBox.Text?.Trim() ?? "";
            return ImageEncoder.IsSupported(name) ? Path.GetFileNameWithoutExtension(name) : name;
        }
    }

    private void ShowDestination()
    {
        InfoText.Classes.Set("error", false);
        InfoText.Text = !_settings.IsComplete
            ? "Set your AWS keys and bucket with the gear button."
            : $"s3://{_settings.Bucket}/{S3Uploader.Key(_settings, BaseName + Extension)}";
    }

    private void ShowError(string message)
    {
        InfoText.Classes.Set("error", true);
        InfoText.Text = message;
    }

    /// <summary>Like the title bar's close button: closing during an upload cancels it.</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (await new S3SettingsWindow(_store, _settings).ShowModalAsync<S3Settings?>(this) is { } saved)
            _settings = saved;
        ShowDestination();
    }

    private async void Upload_Click(object? sender, RoutedEventArgs e)
    {
        if (_uploading)
            return;
        if (!_settings.IsComplete)
        {
            Settings_Click(sender, e);
            return;
        }

        var name = BaseName;
        if (name.Length == 0)
        {
            ShowError("Enter a file name.");
            return;
        }
        if (name.Contains('/') || name.Contains('\\'))
        {
            ShowError("The file name can't contain / or \\. Set the folder with the gear button.");
            return;
        }

        var extension = Extension;
        var key = S3Uploader.Key(_settings, name + extension);
        SetUploading(true);
        try
        {
            if (await S3Uploader.ExistsAsync(_settings, key, _cancel.Token) &&
                !await ConfirmDialog.AskAsync(this, "Replace file", $"{name}{extension} is already in s3://{_settings.Bucket}. Replace it?"))
                return;

            var data = await Task.Run(() => ImageEncoder.Encode(_image, extension));
            var url = await S3Uploader.UploadAsync(_settings, key, data, ImageEncoder.ContentType(extension), _cancel.Token);
            if (Clipboard is { } clipboard)
            {
                try
                {
                    await clipboard.SetTextAsync(url);
                }
                catch (Exception)
                {
                    // Uploaded all the same; the clipboard just refused this time.
                }
            }

            // Show it: open the link in the default browser.
            try
            {
                await Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception)
            {
                // No browser to open it with; the link is on the clipboard all the same.
            }

            // Remember the format for next time.
            _settings.Format = extension;
            try { _store.Save(_settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            CloseWith(new S3Upload(_settings.Bucket, key, url));
        }
        catch (OperationCanceledException)
        {
            // The window was closed.
        }
        catch (AmazonServiceException ex)
        {
            ShowError(S3Uploader.DescribeUploadError(ex));
        }
        catch (AmazonClientException ex)
        {
            ShowError($"Couldn't reach S3: {ex.Message}");
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            ShowError($"Couldn't reach S3: {ex.Message}");
        }
        finally
        {
            SetUploading(false);
        }
    }

    private void SetUploading(bool uploading)
    {
        _uploading = uploading;
        UploadButton.IsEnabled = !uploading;
        UploadButton.Content = uploading ? "Uploading" : "Upload";
    }
}
