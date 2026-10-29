using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// The player bar's three columns (#133, #189), <see cref="Spacing"/> apart: the song, the middle (the volume, the
/// transport and the analyzer) and the end (the format and the buttons, <see cref="EndWidth"/> wide). The middle is as
/// wide as the room allows up to <see cref="MiddleMaxWidth"/>, centred between <see cref="StartWidth"/> and the end, so
/// it never moves for anything the song shows. The song is at least <see cref="StartWidth"/> wide and takes whatever
/// room the centring leaves before the middle, so a wide window gives the title and album more room.
/// </summary>
public sealed class PlayerColumns : Panel
{
    public static readonly StyledProperty<double> StartWidthProperty = AvaloniaProperty.Register<PlayerColumns, double>(nameof(StartWidth));

    public static readonly StyledProperty<double> MiddleMaxWidthProperty =
        AvaloniaProperty.Register<PlayerColumns, double>(nameof(MiddleMaxWidth), double.PositiveInfinity);

    public static readonly StyledProperty<double> EndWidthProperty = AvaloniaProperty.Register<PlayerColumns, double>(nameof(EndWidth));

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<PlayerColumns, double>(nameof(Spacing));

    static PlayerColumns() => AffectsMeasure<PlayerColumns>(StartWidthProperty, MiddleMaxWidthProperty, EndWidthProperty, SpacingProperty);

    public double StartWidth
    {
        get => GetValue(StartWidthProperty);
        set => SetValue(StartWidthProperty, value);
    }

    public double MiddleMaxWidth
    {
        get => GetValue(MiddleMaxWidthProperty);
        set => SetValue(MiddleMaxWidthProperty, value);
    }

    public double EndWidth
    {
        get => GetValue(EndWidthProperty);
        set => SetValue(EndWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The song's, the middle's and the end's x and width in <paramref name="width"/>.</summary>
    internal (double X, double Width)[] Columns(double width)
    {
        var room = Math.Max(0, width - StartWidth - EndWidth - (2 * Spacing));
        var middle = Math.Min(room, MiddleMaxWidth);
        var spare = (room - middle) / 2; // on either side of the middle
        return
        [
            (0, StartWidth + Math.Floor(spare)), // whole pixels: rounded up, its end would eat into the gap
            (StartWidth + Spacing + spare, middle),
            (StartWidth + (2 * Spacing) + (2 * spare) + middle, EndWidth), // the window's end, unless it is too narrow
        ];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? StartWidth + EndWidth + (2 * Spacing) + (double.IsInfinity(MiddleMaxWidth) ? 0 : MiddleMaxWidth)
            : availableSize.Width;
        var columns = Columns(width);
        double height = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(i < columns.Length ? columns[i].Width : 0, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        for (var i = 0; i < Children.Count; i++)
        {
            var (x, width) = i < columns.Length ? columns[i] : (0, 0);
            Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
