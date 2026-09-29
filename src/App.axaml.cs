using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using screengrab.Services;
using screengrab.Views;

namespace screengrab;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window only hides it: the program stays in the tray, listening for the hotkey.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow(new SettingsService());
            TrayIcon.SetIcons(this, [window.TrayIcon]);
            if (Program.Instance is { } instance)
                instance.ShowRequested += () => Dispatcher.UIThread.Post(window.ShowWindow);

            // It starts in the tray; the tray icon, or starting it again, shows the window.
            window.StartListening();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
