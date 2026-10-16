using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// Marks where an album's sleeve sits on the albums page (a card's art, a shelf sleeve, a crate record):
/// <c>controls:SceneSource.AlbumId="{Binding Album.Id}"</c>. The turntable scene starts from it.
/// </summary>
public static class SceneSource
{
    public static readonly AttachedProperty<string?> AlbumIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("AlbumId", typeof(SceneSource));

    public static string? GetAlbumId(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(AlbumIdProperty);
    }

    public static void SetAlbumId(Control control, string? value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(AlbumIdProperty, value);
    }
}
