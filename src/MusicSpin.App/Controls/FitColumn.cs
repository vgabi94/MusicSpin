using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// A vertical <see cref="StackPanel"/> as wide as its widest child would be with all the room it wants (a track title on
/// one line), at least its MinWidth and at most the room it is given (#193): Now Playing's album follows its names
/// instead of filling a fixed area. Children marked <see cref="FollowsProperty"/> (a line that wraps) don't widen it;
/// they take the width the others set.
/// </summary>
public class FitColumn : StackPanel
{
    /// <summary>The child takes the stack's width rather than setting it.</summary>
    public static readonly AttachedProperty<bool> FollowsProperty = AvaloniaProperty.RegisterAttached<FitColumn, Control, bool>("Follows");

    static FitColumn() => AffectsParentMeasure<FitColumn>(FollowsProperty);

    public static bool GetFollows(Control control) => control.GetValue(FollowsProperty);

    public static void SetFollows(Control control, bool value) => control.SetValue(FollowsProperty, value);

    protected override Type StyleKeyOverride => typeof(StackPanel);

    protected override Size MeasureOverride(Size availableSize)
    {
        // The width the children want, each as if alone on a line; the layout system has already held the room to
        // MinWidth..MaxWidth.
        double widest = 0;
        foreach (var child in Children)
        {
            if (child.IsVisible && !GetFollows(child))
            {
                child.Measure(Size.Infinity);
                widest = Math.Max(widest, child.DesiredSize.Width);
            }
        }

        var width = Math.Min(Math.Max(widest, MinWidth), availableSize.Width);
        var size = base.MeasureOverride(new Size(width, availableSize.Height));
        return new Size(width, size.Height);
    }
}
