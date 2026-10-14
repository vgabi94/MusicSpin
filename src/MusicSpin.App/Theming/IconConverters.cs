using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MusicSpin.App.Theming;

/// <summary>Icons named by a resource key in a view model ("Albums"), looked up in Icons/IconsResource.axaml.</summary>
public static class IconConverters
{
    /// <summary>The icon's geometry.</summary>
    public static FuncValueConverter<string?, Geometry?> Geometry { get; } = new(key => Find(key) as Geometry);

    /// <summary>True when the icon is drawn as strokes (it has a "[key].Stroked" flag).</summary>
    public static FuncValueConverter<string?, bool> IsStroked { get; } = new(key => key is not null && Find(key + ".Stroked") is true);

    private static object? Find(string? key)
        => key is not null && Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value : null;
}
