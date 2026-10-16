using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// Marks something the queue takes: an album (a card, a shelf sleeve, a crate record, a Tracks page album header), a
/// track (a row, a Now Playing track) or a queue row: <c>controls:QueueSource.Item="{Binding}"</c>. Right-clicking it
/// opens its menu (Play, Play next, Add to queue) and dragging it drops it into the queue drawer.
/// </summary>
public static class QueueSource
{
    public static readonly AttachedProperty<object?> ItemProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("Item", typeof(QueueSource));

    public static object? GetItem(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(ItemProperty);
    }

    public static void SetItem(Control control, object? value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(ItemProperty, value);
    }
}
