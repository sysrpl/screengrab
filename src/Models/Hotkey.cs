namespace screengrab.Models;

/// <summary>The modifier keys held with Print Screen.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Super = 8,
}

/// <summary>A key combination that takes a screenshot: Print Screen, alone or with one modifier.</summary>
public sealed record Hotkey(string Id, string Name, HotkeyModifiers Modifiers)
{
    public static readonly IReadOnlyList<Hotkey> All =
    [
        new("Print", "Print Screen", HotkeyModifiers.None),
        new("Shift+Print", "Shift+Print Screen", HotkeyModifiers.Shift),
        new("Control+Print", "Ctrl+Print Screen", HotkeyModifiers.Control),
        new("Alt+Print", "Alt+Print Screen", HotkeyModifiers.Alt),
        new("Super+Print", "Super+Print Screen", HotkeyModifiers.Super),
    ];

    /// <summary>The hotkey with this <see cref="Id"/>, or Print Screen if there's none.</summary>
    public static Hotkey FromId(string? id) => All.FirstOrDefault(h => h.Id == id) ?? All[0];

    public override string ToString() => Name;
}
