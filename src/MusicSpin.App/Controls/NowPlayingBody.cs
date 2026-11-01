using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// Now Playing's body (#193): the deck (the first child) centred in its column, <see cref="DeckShare"/> : 1 of the width
/// left after <see cref="Spacing"/>, as in the mockup's <c>.np-body</c> grid; the album (the second child) from
/// <see cref="Spacing"/> after the deck itself to the right edge, so a wide window gives long titles the room the grid
/// left empty beside the deck. The deck doesn't move for it. The album is as wide as it asks for (FitColumn: its names)
/// within that room, from its left.
/// </summary>
public sealed class NowPlayingBody : Panel
{
    public static readonly StyledProperty<double> DeckShareProperty = AvaloniaProperty.Register<NowPlayingBody, double>(nameof(DeckShare), 1);

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<NowPlayingBody, double>(nameof(Spacing));

    static NowPlayingBody() => AffectsMeasure<NowPlayingBody>(DeckShareProperty, SpacingProperty);

    /// <summary>The deck's column against the album's (the mockup's 1.15fr 1fr).</summary>
    public double DeckShare
    {
        get => GetValue(DeckShareProperty);
        set => SetValue(DeckShareProperty, value);
    }

    /// <summary>The gap between the deck and the album.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The deck's column's width in <paramref name="width"/>.</summary>
    internal double DeckColumn(double width) => Math.Max(0, width - Spacing) * DeckShare / (DeckShare + 1);

    /// <summary>
    /// Where the album starts and how wide it may be in <paramref name="width"/>, beside a deck <paramref name="deck"/>
    /// wide. With no deck yet (0 wide) it starts where the grid's second column did.
    /// </summary>
    internal (double X, double Width) Album(double width, double deck)
    {
        var column = DeckColumn(width);
        deck = Math.Clamp(deck, 0, column);
        var x = (deck > 0 ? (column + deck) / 2 : column) + Spacing;
        return (x, Math.Max(0, width - x));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var column = DeckColumn(width);
        double deck = 0, height = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i == 0)
            {
                child.Measure(new Size(column, availableSize.Height));
                deck = child.DesiredSize.Width;
            }
            else
            {
                child.Measure(new Size(Album(width, deck).Width, availableSize.Height));
            }

            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = DeckColumn(finalSize.Width);
        var deck = Children.Count > 0 ? Children[0].DesiredSize.Width : 0;
        var (x, width) = Album(finalSize.Width, deck);
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            // The album as wide as it asked for (its margin included), within the room.
            child.Arrange(i == 0
                ? new Rect(0, 0, column, finalSize.Height)
                : new Rect(x, 0, Math.Min(width, child.DesiredSize.Width), finalSize.Height));
        }

        return finalSize;
    }
}
