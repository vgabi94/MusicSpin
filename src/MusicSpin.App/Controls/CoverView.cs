using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// An album sleeve: the cover image with a light sheen and a soft shadow (Styles/Artwork.axaml), or, for an album
/// without art, a plain placeholder showing its title and artist (mockup: "Generated cover art", which were stand-ins).
/// </summary>
public sealed class CoverView : TemplatedControl
{
    /// <summary>The placeholder text's inset from the sleeve's left/right and bottom edges, as shares of its width (8%, 7%).</summary>
    public const double TextInsetX = 0.08;

    public const double TextInsetBottom = 0.07;

    /// <summary>The artist line's size relative to the title's (the mockup's .75em).</summary>
    public const double ArtistScale = 0.75;

    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<CoverView, IImage?>(nameof(Source));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<CoverView, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ArtistProperty = AvaloniaProperty.Register<CoverView, string?>(nameof(Artist));

    /// <summary>The placeholder title's size as a share of the sleeve's width (the mockup's 9cqw).</summary>
    public static readonly StyledProperty<double> TitleScaleProperty = AvaloniaProperty.Register<CoverView, double>(nameof(TitleScale));

    public static readonly StyledProperty<double> MinTitleFontSizeProperty = AvaloniaProperty.Register<CoverView, double>(nameof(MinTitleFontSize));

    public static readonly StyledProperty<double> MaxTitleFontSizeProperty = AvaloniaProperty.Register<CoverView, double>(nameof(MaxTitleFontSize));

    public static readonly DirectProperty<CoverView, double> TitleFontSizeProperty =
        AvaloniaProperty.RegisterDirect<CoverView, double>(nameof(TitleFontSize), c => c.TitleFontSize);

    public static readonly DirectProperty<CoverView, double> ArtistFontSizeProperty =
        AvaloniaProperty.RegisterDirect<CoverView, double>(nameof(ArtistFontSize), c => c.ArtistFontSize);

    public static readonly DirectProperty<CoverView, Thickness> TextMarginProperty =
        AvaloniaProperty.RegisterDirect<CoverView, Thickness>(nameof(TextMargin), c => c.TextMargin);

    public static readonly DirectProperty<CoverView, bool> HasArtProperty =
        AvaloniaProperty.RegisterDirect<CoverView, bool>(nameof(HasArt), c => c.HasArt);

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Artist
    {
        get => GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }

    public double TitleScale
    {
        get => GetValue(TitleScaleProperty);
        set => SetValue(TitleScaleProperty, value);
    }

    public double MinTitleFontSize
    {
        get => GetValue(MinTitleFontSizeProperty);
        set => SetValue(MinTitleFontSizeProperty, value);
    }

    public double MaxTitleFontSize
    {
        get => GetValue(MaxTitleFontSizeProperty);
        set => SetValue(MaxTitleFontSizeProperty, value);
    }

    /// <summary>The placeholder title's font size for the current width.</summary>
    public double TitleFontSize
    {
        get;
        private set => SetAndRaise(TitleFontSizeProperty, ref field, value);
    }

    /// <summary>The placeholder artist's font size (75% of the title's).</summary>
    public double ArtistFontSize
    {
        get;
        private set => SetAndRaise(ArtistFontSizeProperty, ref field, value);
    }

    /// <summary>The placeholder text's margin for the current width.</summary>
    public Thickness TextMargin
    {
        get;
        private set => SetAndRaise(TextMarginProperty, ref field, value);
    }

    /// <summary>True when there is an image; false shows the placeholder.</summary>
    public bool HasArt
    {
        get;
        private set => SetAndRaise(HasArtProperty, ref field, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            HasArt = Source is not null;
        }
        else if (change.Property == BoundsProperty || change.Property == TitleScaleProperty
                 || change.Property == MinTitleFontSizeProperty || change.Property == MaxTitleFontSizeProperty)
        {
            var width = Bounds.Width;
            TitleFontSize = Math.Clamp(width * TitleScale, MinTitleFontSize, Math.Max(MinTitleFontSize, MaxTitleFontSize));
            ArtistFontSize = TitleFontSize * ArtistScale;
            TextMargin = new Thickness(width * TextInsetX, 0, width * TextInsetX, width * TextInsetBottom);
        }
    }
}
