using Amazon.Runtime;
using Avalonia.Controls;
using Avalonia.Interactivity;
using screengrab.Helpers;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>
/// The AWS keys, region, bucket and folder for uploads. OK saves them (encrypted) and returns
/// them; Cancel returns null.
/// </summary>
public partial class S3SettingsWindow : DialogWindow
{
    private readonly S3SettingsStore _store;
    private readonly S3Settings _current;

    /// <summary>For the XAML designer.</summary>
    public S3SettingsWindow() : this(new S3SettingsStore(), new S3Settings())
    {
    }

    public S3SettingsWindow(S3SettingsStore store, S3Settings current)
    {
        _store = store;
        _current = current;
        InitializeComponent();

        RegionBox.ItemsSource = AwsRegions.Names;
        AccessKeyBox.Text = current.AccessKeyId;
        SecretKeyBox.Text = current.SecretAccessKey;
        RegionBox.SelectedItem = AwsRegions.Names.Contains(current.Region) ? current.Region : AwsRegions.Default;
        BucketBox.Text = current.Bucket;
        FolderBox.Text = current.Folder;
        ShowSecretCheck.IsCheckedChanged += (_, _) => SecretKeyBox.RevealPassword = ShowSecretCheck.IsChecked == true;
        Opened += (_, _) => (AccessKeyBox.Text?.Length > 0 ? BucketBox : AccessKeyBox).Focus();
    }

    /// <summary>The form as settings (the upload format is kept from before).</summary>
    private S3Settings FromForm() => new()
    {
        AccessKeyId = AccessKeyBox.Text?.Trim() ?? "",
        SecretAccessKey = SecretKeyBox.Text?.Trim() ?? "",
        Region = RegionBox.SelectedItem as string ?? AwsRegions.Default,
        Bucket = BucketBox.Text?.Trim() ?? "",
        Folder = (FolderBox.Text ?? "").Trim().Trim('/'),
        Format = _current.Format,
    };

    private void ShowError(string? message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = message is not null;
    }

    private async void ListBuckets_Click(object? sender, RoutedEventArgs e)
    {
        var settings = FromForm();
        if (settings.AccessKeyId.Length == 0 || settings.SecretAccessKey.Length == 0)
        {
            ShowError("Enter the access key ID and secret access key first.");
            return;
        }

        ShowError(null);
        ListButton.IsEnabled = false;
        IReadOnlyList<string> buckets;
        try
        {
            buckets = await S3Uploader.ListBucketsAsync(settings);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or System.Net.Http.HttpRequestException)
        {
            ShowError($"Couldn't list the buckets: {ex.Message}");
            return;
        }
        finally
        {
            ListButton.IsEnabled = true;
        }

        if (buckets.Count == 0)
        {
            ShowError("These keys can't see any buckets. Type the bucket's name instead.");
            return;
        }

        var menu = new MenuFlyout();
        foreach (var bucket in buckets)
        {
            var item = new MenuItem { Header = bucket };
            item.Click += (_, _) => BucketBox.Text = bucket;
            menu.Items.Add(item);
        }
        menu.ShowAt(ListButton);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var settings = FromForm();
        var error =
            settings.AccessKeyId.Length == 0 ? "Enter the access key ID."
            : settings.SecretAccessKey.Length == 0 ? "Enter the secret access key."
            : settings.Bucket.Length == 0 ? "Enter or choose a bucket."
            : null;
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"Couldn't save the settings: {ex.Message}");
            return;
        }
        CloseWith(settings);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
