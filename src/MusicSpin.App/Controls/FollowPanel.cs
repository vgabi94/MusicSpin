using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// A row whose first child takes its natural width, or what the others leave of the row when that is less, and whose
/// other children follow it, <see cref="Spacing"/> apart (mockup: <c>.meta</c>, a flex row whose text shrinks first).
/// The player bar's song name ends in an ellipsis when long; the analyzer and the volume follow its length.
/// </summary>
public sealed class FollowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FollowPanel, double>(nameof(Spacing));

    static FollowPanel() => AffectsMeasure<FollowPanel>(SpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        if (visible.Count == 0)
        {
            return default;
        }

        double others = 0, height = 0;
        foreach (var child in visible.Skip(1))
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            others += child.DesiredSize.Width + Spacing;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var first = visible[0];
        first.Measure(new Size(Math.Max(0, availableSize.Width - others), availableSize.Height));
        height = Math.Max(height, first.DesiredSize.Height);
        return new Size(first.DesiredSize.Width + others, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var others = visible.Skip(1).Sum(c => c.DesiredSize.Width + Spacing);
        var x = 0.0;
        for (var i = 0; i < visible.Count; i++)
        {
            var child = visible[i];
            var width = i == 0 ? Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - others)) : child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }
}
