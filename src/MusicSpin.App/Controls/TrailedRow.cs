using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// A row centred in its width with something trailing it, <see cref="Spacing"/> after (#191: the transport's buttons,
/// then the Exclusive pill). The first child is centred, so the play button stays over the middle of the position bar;
/// only when the trailer would not fit after it does the row move left, just as far as needed (and no further than the
/// start). The trailer gets a slot <see cref="TrailerWidth"/> wide whatever it shows, so a longer trailer moves nothing.
/// </summary>
public sealed class TrailedRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<TrailedRow, double>(nameof(Spacing));

    public static readonly StyledProperty<double> TrailerWidthProperty = AvaloniaProperty.Register<TrailedRow, double>(nameof(TrailerWidth));

    static TrailedRow() => AffectsMeasure<TrailedRow>(SpacingProperty, TrailerWidthProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double TrailerWidth
    {
        get => GetValue(TrailerWidthProperty);
        set => SetValue(TrailerWidthProperty, value);
    }

    /// <summary>The row's x and the trailer's x, for a row <paramref name="row"/> wide in <paramref name="width"/>.</summary>
    internal (double Row, double Trailer) Positions(double width, double row)
    {
        var centred = (width - row) / 2;
        var fitting = width - row - Spacing - TrailerWidth; // the furthest right the row may start with the trailer after it
        var x = Math.Max(0, Math.Floor(Math.Min(centred, fitting))); // whole pixels, so the buttons stay sharp
        return (x, x + row + Spacing);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double rowWidth = 0, height = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(i == 0 ? availableSize.WithWidth(double.PositiveInfinity) : new Size(TrailerWidth, availableSize.Height));
            rowWidth = i == 0 ? child.DesiredSize.Width : rowWidth;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = double.IsInfinity(availableSize.Width) ? rowWidth + Spacing + TrailerWidth : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        var row = Children[0].DesiredSize.Width;
        var (rowX, trailerX) = Positions(finalSize.Width, row);
        Children[0].Arrange(new Rect(rowX, 0, row, finalSize.Height));
        for (var i = 1; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(trailerX, 0, TrailerWidth, finalSize.Height));
        }

        return finalSize;
    }
}
