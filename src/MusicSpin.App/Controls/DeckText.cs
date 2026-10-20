using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MusicSpin.App.Controls;

/// <summary>Where a <see cref="DeckText"/> sits on its <see cref="DeckText.X"/>, as SVG's <c>text-anchor</c>.</summary>
public enum TextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>
/// A line of text placed as the design studies' SVG places it: its baseline at (<see cref="X"/>, <see cref="Y"/>) on a
/// <see cref="Canvas"/>, anchored at its start, middle or end. A deck's engraved name and readouts.
/// </summary>
public sealed class DeckText : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<DeckText, string?>(nameof(Text));

    public static readonly StyledProperty<double> XProperty = AvaloniaProperty.Register<DeckText, double>(nameof(X));

    public static readonly StyledProperty<double> YProperty = AvaloniaProperty.Register<DeckText, double>(nameof(Y));

    public static readonly StyledProperty<TextAnchor> AnchorProperty = AvaloniaProperty.Register<DeckText, TextAnchor>(nameof(Anchor));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<DeckText>();

    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<DeckText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty = TextElement.FontWeightProperty.AddOwner<DeckText>();

    public static readonly StyledProperty<double> LetterSpacingProperty = TextElement.LetterSpacingProperty.AddOwner<DeckText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<DeckText>();

    private TextLayout? _layout;

    static DeckText()
    {
        AffectsMeasure<DeckText>(TextProperty, AnchorProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, LetterSpacingProperty);
        AffectsRender<DeckText>(ForegroundProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Where the anchor is, across.</summary>
    public double X
    {
        get => GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    /// <summary>The baseline, down.</summary>
    public double Y
    {
        get => GetValue(YProperty);
        set => SetValue(YProperty, value);
    }

    public TextAnchor Anchor
    {
        get => GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    /// <summary>Space after each letter (SVG's <c>letter-spacing</c>).</summary>
    public double LetterSpacing
    {
        get => GetValue(LetterSpacingProperty);
        set => SetValue(LetterSpacingProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The face the text asks for (tests check it's the bundled font that draws it).</summary>
    public Typeface Typeface => new(FontFamily, FontStyle.Normal, FontWeight);

    /// <summary>Where the text's box goes on its canvas, so its baseline and anchor fall on (X, Y).</summary>
    public Point Origin
    {
        get
        {
            var layout = Layout();
            var shift = Anchor switch
            {
                TextAnchor.Middle => layout.WidthIncludingTrailingWhitespace / 2,
                TextAnchor.End => layout.WidthIncludingTrailingWhitespace,
                _ => 0,
            };
            return new Point(X - shift, Y - layout.TextLines[0].Baseline);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        var shaped = change.Property == TextProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty
                     || change.Property == FontWeightProperty || change.Property == LetterSpacingProperty;
        if (shaped || change.Property == ForegroundProperty)
        {
            _layout?.Dispose();
            _layout = null;
        }

        // Only what moves the text places it again (its size, its anchor point, which end is anchored).
        if (shaped || change.Property == XProperty || change.Property == YProperty || change.Property == AnchorProperty)
        {
            var origin = Origin;
            Canvas.SetLeft(this, origin.X);
            Canvas.SetTop(this, origin.Y);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout();
        return new Size(layout.WidthIncludingTrailingWhitespace, layout.Height);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Layout().Draw(context, default);
    }

    /// <summary>The text shaped once, with its brush, until the text, the font or the brush changes.</summary>
    private TextLayout Layout() => _layout ??= new(Text ?? string.Empty, Typeface, FontSize, Foreground, letterSpacing: LetterSpacing);
}
