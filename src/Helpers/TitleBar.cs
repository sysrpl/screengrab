using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace screengrab.Helpers;

/// <summary>
/// For windows drawn without Cinnamon's title bar, whose own top row takes its place. In XAML:
/// <code>h:TitleBar.MovesWindow="True"</code>
/// makes dragging the control (between its buttons, which keep their presses) move the window.
/// </summary>
public sealed class TitleBar : AvaloniaObject
{
    public static readonly AttachedProperty<bool> MovesWindowProperty =
        AvaloniaProperty.RegisterAttached<TitleBar, Control, bool>("MovesWindow");

    static TitleBar()
    {
        MovesWindowProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.PointerPressed -= Control_PointerPressed;
            if (e.NewValue is true)
                control.PointerPressed += Control_PointerPressed;
        });
    }

    public static bool GetMovesWindow(Control control) => control.GetValue(MovesWindowProperty);
    public static void SetMovesWindow(Control control, bool value) => control.SetValue(MovesWindowProperty, value);

    private static void Control_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || TopLevel.GetTopLevel(control) is not Window window ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;
        window.BeginMoveDrag(e);
        e.Handled = true;
    }
}
