using screengrab.Services;

namespace screengrab;

/// <summary>screengrab --capture: one screenshot saved straight to a file, with no window.</summary>
internal static class CommandLine
{
    public static int Capture(string[] args)
    {
        string? path = null;
        var delay = 0;
        var includePointer = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        path = args[++i];
                    break;
                case "--delay":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out delay) || delay < 0)
                        return Fail("--delay needs a number of seconds.");
                    break;
                case "--pointer":
                    includePointer = true;
                    break;
                default:
                    return Fail($"Unknown option {args[i]}. Try --help.");
            }
        }

        // Sets up Avalonia's drawing, which writes the PNG, without showing anything.
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        if (delay > 0)
            Thread.Sleep(TimeSpan.FromSeconds(delay));

        try
        {
            var image = ScreenCapture.Capture(includePointer);
            path ??= ImageFiles.NewPath(ImageFiles.Folder(new SettingsService().Settings), image.Time);
            ImageEncoder.Save(image, Path.GetFullPath(path));
            Console.WriteLine(Path.GetFullPath(path));
            return 0;
        }
        catch (CaptureException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Couldn't save the screenshot: {ex.Message}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"screengrab: {message}");
        return 1;
    }
}
