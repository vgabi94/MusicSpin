using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// Children in a row, <see cref="Spacing"/> apart, each right after the one before (like inline text). The first child
/// gets the width the others leave, so a long text trims while a badge after it stays in view (mockup: .card .a).
/// </summary>
public sealed class TrimFirstPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<TrimFirstPanel, double>(nameof(Spacing));

    static TrimFirstPanel() => AffectsMeasure<TrimFirstPanel>(SpacingProperty);

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

        double rest = 0, height = 0;
        foreach (var child in visible.Skip(1))
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            rest += child.DesiredSize.Width + Spacing;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var first = visible[0];
        first.Measure(new Size(Math.Max(0, availableSize.Width - rest), availableSize.Height));
        return new Size(first.DesiredSize.Width + rest, Math.Max(height, first.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }
}
