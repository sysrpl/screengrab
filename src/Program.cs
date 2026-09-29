using Avalonia;
using screengrab.Services;

namespace screengrab;

internal static class Program
{
    private const string Usage =
        """
        Screen Grab: takes screenshots with Print Screen, even while a menu is open.

        Usage:
          screengrab                  Start in the system tray, or show the copy that's already running
          screengrab --capture [FILE] [--delay SECONDS] [--pointer]
                                      Save one screenshot (.png, .jpg or .gif) and exit
                                      (FILE defaults to a new file in the save folder)
        """;

    /// <summary>The single-instance lock, or null when running without one (--capture).</summary>
    public static SingleInstance? Instance { get; private set; }

    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Contains("--capture"))
            return CommandLine.Capture(args);

        Instance = SingleInstance.Claim();
        if (Instance is null)
            return 0;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance.Release();
        }
        return 0;
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
