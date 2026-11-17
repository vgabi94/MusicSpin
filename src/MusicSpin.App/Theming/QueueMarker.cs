using System.Globalization;
using Avalonia.Data.Converters;

namespace MusicSpin.App.Theming;

/// <summary>
/// Whether a queue row is the one playing, or sits above it. Both bindings are the row's index and the drawer's current
/// index, so a track change updates the rows on screen and no other.
/// </summary>
public sealed class QueueMarker : IMultiValueConverter
{
    /// <summary>True when the row's index is above the current one.</summary>
    public static QueueMarker Before { get; } = new(before: true);

    /// <summary>True when the row is the one playing.</summary>
    public static QueueMarker Current { get; } = new(before: false);

    private readonly bool _before;

    private QueueMarker(bool before) => _before = before;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Count: >= 2 } || values[0] is not int index || values[1] is not int current || current < 0)
        {
            return false;
        }

        return _before ? index < current : index == current;
    }
}
