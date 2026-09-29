using System.Diagnostics;
using System.Text.RegularExpressions;
using screengrab.Models;

namespace screengrab.Services;

/// <summary>
/// Cinnamon's own screenshot shortcuts. Out of the box Cinnamon uses every Print Screen
/// combination (Print, Shift+Print, Ctrl+Print, Alt+Print and so on), so pressing Screen Grab's
/// hotkey while no menu is open would take two screenshots, one from each program. These find the
/// Cinnamon shortcuts that clash with a hotkey, take them away, and give them back, by running
/// gsettings. Changes take effect straight away, without logging out.
/// </summary>
public static partial class CinnamonKeys
{
    private const string MediaKeysSchema = "org.cinnamon.desktop.keybindings.media-keys";

    /// <summary>
    /// The Cinnamon shortcuts that <paramref name="hotkey"/> would also trigger. Empty when there
    /// are none, or this isn't Cinnamon (or gsettings isn't there).
    /// </summary>
    public static async Task<List<CinnamonBinding>> FindConflictsAsync(Hotkey hotkey)
    {
        var output = await RunAsync("list-recursively", MediaKeysSchema);
        var conflicts = new List<CinnamonBinding>();
        if (output is null)
            return conflicts;

        // Lines look like: org.cinnamon.desktop.keybindings.media-keys screenshot ['Print']
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length < 3 || parts[0] != MediaKeysSchema)
                continue;
            foreach (var accelerator in ParseList(parts[2]))
            {
                if (Matches(accelerator, hotkey))
                    conflicts.Add(new CinnamonBinding(parts[0], parts[1], accelerator));
            }
        }
        return conflicts;
    }

    /// <summary>Removes each shortcut from its Cinnamon setting. Returns false if gsettings failed.</summary>
    public static async Task<bool> RemoveAsync(IEnumerable<CinnamonBinding> bindings)
    {
        foreach (var group in bindings.GroupBy(b => (b.Schema, b.Key)))
        {
            var current = await GetListAsync(group.Key.Schema, group.Key.Key);
            if (current is null)
                return false;
            var removed = group.Select(b => b.Accelerator).ToHashSet();
            if (!await SetListAsync(group.Key.Schema, group.Key.Key, current.Where(a => !removed.Contains(a))))
                return false;
        }
        return true;
    }

    /// <summary>Puts shortcuts back into their Cinnamon settings. Returns false if gsettings failed.</summary>
    public static async Task<bool> RestoreAsync(IEnumerable<CinnamonBinding> bindings)
    {
        foreach (var group in bindings.GroupBy(b => (b.Schema, b.Key)))
        {
            var current = await GetListAsync(group.Key.Schema, group.Key.Key);
            if (current is null)
                return false;
            var restored = current.Concat(group.Select(b => b.Accelerator).Where(a => !current.Contains(a)));
            if (!await SetListAsync(group.Key.Schema, group.Key.Key, restored))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a GTK accelerator such as "&lt;Shift&gt;Print" or "&lt;Primary&gt;&lt;Alt&gt;Print"
    /// is exactly <paramref name="hotkey"/>.
    /// </summary>
    public static bool Matches(string accelerator, Hotkey hotkey)
    {
        var modifiers = HotkeyModifiers.None;
        foreach (Match match in ModifierPattern().Matches(accelerator))
        {
            modifiers |= match.Groups[1].Value.ToLowerInvariant() switch
            {
                "shift" => HotkeyModifiers.Shift,
                "control" or "ctrl" or "primary" => HotkeyModifiers.Control,
                "alt" or "mod1" or "meta" => HotkeyModifiers.Alt,
                "super" or "mod4" => HotkeyModifiers.Super,
                // Anything else (Hyper, say) can't be one of our hotkeys.
                _ => (HotkeyModifiers)(-1),
            };
        }
        var key = ModifierPattern().Replace(accelerator, "");
        return key.Equals("Print", StringComparison.OrdinalIgnoreCase) && modifiers == hotkey.Modifiers;
    }

    [GeneratedRegex("<([^>]+)>")]
    private static partial Regex ModifierPattern();

    /// <summary>The strings in a GVariant string array such as "['Print', 'XF86Screenshot']" or "@as []".</summary>
    private static List<string> ParseList(string value) =>
        QuotedPattern().Matches(value).Select(m => m.Groups[1].Value).ToList();

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedPattern();

    private static async Task<List<string>?> GetListAsync(string schema, string key)
    {
        var output = await RunAsync("get", schema, key);
        return output is null ? null : ParseList(output);
    }

    private static async Task<bool> SetListAsync(string schema, string key, IEnumerable<string> accelerators)
    {
        var value = "[" + string.Join(", ", accelerators.Select(a => $"'{a}'")) + "]";
        return await RunAsync("set", schema, key, value) is not null;
    }

    /// <summary>Runs gsettings; its output, or null if it failed or isn't installed.</summary>
    private static async Task<string?> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("gsettings")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // gsettings isn't installed: not a GNOME-family desktop.
            return null;
        }
    }
}
