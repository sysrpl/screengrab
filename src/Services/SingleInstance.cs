using System.Net.Sockets;
using System.Text;

namespace screengrab.Services;

/// <summary>
/// Keeps Screen Grab to one copy per user, as two copies would both take a screenshot on every
/// key press. The first copy listens on a Unix socket; starting another copy tells the first to
/// show its window, and then exits.
/// </summary>
public sealed class SingleInstance
{
    private const string ShowMessage = "show\n";

    /// <summary>Null when the socket couldn't be made; that copy runs without the check.</summary>
    private readonly Socket? _listener;

    private SingleInstance(Socket? listener)
    {
        _listener = listener;
        _ = AcceptAsync();
    }

    /// <summary>Another copy was started. Raised on a thread-pool thread.</summary>
    public event Action? ShowRequested;

    private static string SocketPath
    {
        get
        {
            var folder = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(folder)
                ? Path.Combine(Path.GetTempPath(), $"screengrab-{Environment.UserName}.sock")
                : Path.Combine(folder, "screengrab.sock");
        }
    }

    /// <summary>
    /// Becomes the running copy, or returns null after asking the copy that's already running
    /// to show itself.
    /// </summary>
    public static SingleInstance? Claim()
    {
        var path = SocketPath;
        UnixDomainSocketEndPoint endPoint;
        try
        {
            endPoint = new UnixDomainSocketEndPoint(path);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Socket paths are limited to 108 characters; with a very long runtime folder, run unchecked.
            return new SingleInstance(null);
        }

        if (File.Exists(path))
        {
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(endPoint);
                client.Send(Encoding.ASCII.GetBytes(ShowMessage));
                return null;
            }
            catch (SocketException)
            {
                // Left behind by a copy that didn't exit cleanly: nobody is listening.
                File.Delete(path);
            }
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(endPoint);
            listener.Listen(4);
        }
        catch (SocketException)
        {
            // Can't listen (another copy won a race, say); run anyway rather than not at all.
            listener.Dispose();
            return new SingleInstance(null);
        }
        return new SingleInstance(listener);
    }

    private async Task AcceptAsync()
    {
        if (_listener is null)
            return;

        var buffer = new byte[64];
        while (true)
        {
            try
            {
                using var client = await _listener.AcceptAsync();
                var count = await client.ReceiveAsync(buffer, SocketFlags.None);
                if (Encoding.ASCII.GetString(buffer, 0, count) == ShowMessage)
                    ShowRequested?.Invoke();
            }
            catch (SocketException)
            {
                // A client that hung up early; keep listening.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>Stops listening and removes the socket file.</summary>
    public void Release()
    {
        if (_listener is null)
            return;
        _listener.Dispose();
        try { File.Delete(SocketPath); } catch (IOException) { }
    }
}
