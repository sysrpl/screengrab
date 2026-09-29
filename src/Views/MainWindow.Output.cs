using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using screengrab.Controls;
using screengrab.Models;
using screengrab.Services;

namespace screengrab.Views;

/// <summary>Getting screenshots out (copy, save, save as), selecting a monitor, and zooming.</summary>
public partial class MainWindow
{
    private void InitializeOutput()
    {
        var screenMenu = new MenuFlyout();
        screenMenu.Opening += (_, _) => FillScreenMenu(screenMenu);
        ScreenButton.Flyout = screenMenu;

        Picture.PropertyChanged += (_, e) =>
        {
            if (e.Property == CaptureView.ZoomProperty)
                ApplyZoom();
            else if (e.Property == BoundsProperty)
                UpdateZoomLabel();
        };
        Picture.Zoom = _settings.FitToWindow ? 0 : 1;
        ApplyZoom();
    }

    /// <summary>The selected area of the current screenshot, or all of it when nothing is selected.</summary>
    private CaptureImage? OutputImage()
    {
        if (CurrentItem is not { } item)
            return null;
        return Picture.Selection is { } selection ? item.Image.Crop(selection) : item.Image;
    }

    private string OutputDescription(CaptureImage image) =>
        Picture.Selection is null ? "the screenshot" : $"the {image.Width} × {image.Height} selection";

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (OutputImage() is not { } image)
            return;
        SetStatus(await ClipboardImages.CopyAsync(this, image)
            ? $"Copied {OutputDescription(image)} to the clipboard."
            : "Couldn't copy to the clipboard.");
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (OutputImage() is not { } image || CurrentItem is not { } item)
            return;

        var folder = ImageFiles.Folder(_settings);
        try
        {
            var path = ImageFiles.NewPath(folder, image.Time);
            await Task.Run(() => ImageEncoder.Save(image, path));
            item.SavedPath = path;
            SetStatus($"Saved {OutputDescription(image)} as {ImageFiles.Display(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("Couldn't save the screenshot", $"{ImageFiles.Display(folder)}: {ex.Message}");
        }
    }

    private async void SaveAs_Click(object? sender, RoutedEventArgs e)
    {
        if (OutputImage() is not { } image || CurrentItem is not { } item)
            return;
        if (await SaveImage.SaveAsAsync(this, image, ImageFiles.Folder(_settings)) is not { } path)
            return;
        item.SavedPath = path;
        SetStatus($"Saved {OutputDescription(image)} as {ImageFiles.Display(path)}.");
    }

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = ImageFiles.Folder(_settings);
        try
        {
            Directory.CreateDirectory(folder);
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await ShowErrorAsync("Couldn't open the save folder", $"{ImageFiles.Display(folder)}: {ex.Message}");
        }
    }

    /// <summary>One entry per monitor, left to right, then the whole picture.</summary>
    private void FillScreenMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var screens = Screens.All.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
        for (var i = 0; i < screens.Count; i++)
        {
            var bounds = screens[i].Bounds;
            var item = new MenuItem
            {
                Header = $"Screen {i + 1}: {bounds.Width} × {bounds.Height}{(screens[i].IsPrimary ? " (primary)" : "")}",
            };
            item.Click += (_, _) => Picture.Selection = bounds;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Everything" };
        all.Click += (_, _) =>
        {
            if (Picture.Source is { } source)
                Picture.Selection = new PixelRect(source.PixelSize);
        };
        menu.Items.Add(all);
    }

    /// <summary>Fit ↔ 100%. Ctrl+wheel over the picture zooms further.</summary>
    private void Zoom_Click(object? sender, RoutedEventArgs e)
    {
        if (Picture.Zoom > 0)
            Picture.Zoom = 0;
        else
            Picture.ZoomAround(1);
    }

    /// <summary>Scroll bars only when zoomed; to fit, the picture takes the space it's given.</summary>
    private void ApplyZoom()
    {
        var zoomed = Picture.Zoom > 0;
        PictureScroller.HorizontalScrollBarVisibility = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PictureScroller.VerticalScrollBarVisibility = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _settings.FitToWindow = !zoomed;
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        var percent = Picture.Source is null ? "" : $" {Math.Round(Picture.EffectiveZoom * 100)}%";
        ZoomLabel.Text = Picture.Zoom > 0 ? $"Zoom{percent}" : $"Fit{percent}";
    }
}
