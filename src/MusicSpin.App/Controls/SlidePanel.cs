using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A panel that slides into place from its <see cref="Edge"/> when it opens and back out when it closes (mockup:
/// .nowplaying, translate 0 104%, from below; .queue, translate 105% 0, from the right), or, while <see cref="IsFading"/>,
/// fades in and out in place (mockup: .nowplaying.scene). <see cref="Shown"/> is set here from <see cref="IsOpen"/> as a
/// local value and animated by the styles' transitions; it's hidden once it's out.
/// </summary>
public sealed class SlidePanel : Panel
{
    /// <summary>How far below its place a closed panel sits, as a share of its height (mockup: 104%).</summary>
    private const double Below = 1.04;

    /// <summary>How far right of its place a closed panel sits, as a share of its width (mockup: 105%, past its shadow).</summary>
    private const double Aside = 1.05;

    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<SlidePanel, bool>(nameof(IsOpen));

    public static readonly StyledProperty<double> ShownProperty = AvaloniaProperty.Register<SlidePanel, double>(nameof(Shown));

    public static readonly StyledProperty<bool> IsFadingProperty = AvaloniaProperty.Register<SlidePanel, bool>(nameof(IsFading));

    public static readonly StyledProperty<Dock> EdgeProperty = AvaloniaProperty.Register<SlidePanel, Dock>(nameof(Edge), Dock.Bottom);

    public static readonly DirectProperty<SlidePanel, bool> CoversProperty =
        AvaloniaProperty.RegisterDirect<SlidePanel, bool>(nameof(Covers), p => p.Covers);

    public SlidePanel()
    {
        Show();
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>0: out (hidden); 1: in place.</summary>
    public double Shown
    {
        get => GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    /// <summary>Fades in place instead of sliding.</summary>
    public bool IsFading
    {
        get => GetValue(IsFadingProperty);
        set => SetValue(IsFadingProperty, value);
    }

    /// <summary>
    /// Open, all the way in and not fading: an opaque panel hides what is under it, which then needn't be drawn (Now Playing
    /// over the page and the sidebar: their spinning records would be drawn every frame for nothing).
    /// </summary>
    public bool Covers
    {
        get;
        private set => SetAndRaise(CoversProperty, ref field, value);
    }

    /// <summary>Where it slides in from: <see cref="Dock.Bottom"/> (the default) or <see cref="Dock.Right"/>.</summary>
    public Dock Edge
    {
        get => GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            IsVisible = true;
            Shown = IsOpen ? 1 : 0;
            Show();
        }
        else if (change.Property == ShownProperty || change.Property == IsFadingProperty || change.Property == BoundsProperty
                 || change.Property == EdgeProperty)
        {
            Show();
        }
    }

    private void Show()
    {
        // Fading, it stays in place even closed (invisible), so what is on it can be measured.
        IsHitTestVisible = IsOpen;
        IsVisible = IsOpen || Shown > 0 || IsFading;
        Covers = IsOpen && !IsFading && Shown >= 1;
        Opacity = IsFading ? Shown : 1;
        var away = 1 - Shown;
        RenderTransform = IsFading ? null
            : Edge == Dock.Right ? new TranslateTransform(away * Aside * Bounds.Width, 0)
            : new TranslateTransform(0, away * Below * Bounds.Height);
    }
}
