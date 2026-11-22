using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A panel that slides into place from its <see cref="Edge"/> when it opens and back out when it closes (mockup:
/// .nowplaying, translate 0 104%, from below; .queue, translate 105% 0, from the right), or, while <see cref="IsFading"/>,
/// fades in and out in place (mockup: .nowplaying.scene). <see cref="Shown"/> is set here from <see cref="IsOpen"/> as a
/// local value and animated by the styles' transitions; it's hidden once it's out.
/// </summary>
/// <remarks>
/// While it moves (<see cref="IsMoving"/>), what it holds draws through <see cref="MotionCache"/>: an image of it made
/// once, so each step of the slide or fade copies that image instead of drawing it all again (#128). The cache is on what
/// it holds, not on the panel, so the panel's own transform and opacity (fading) composite the single cached image cleanly.
/// </remarks>
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

    public static readonly DirectProperty<SlidePanel, bool> IsMovingProperty =
        AvaloniaProperty.RegisterDirect<SlidePanel, bool>(nameof(IsMoving), p => p.IsMoving);

    public static readonly StyledProperty<CacheMode?> MotionCacheProperty = AvaloniaProperty.Register<SlidePanel, CacheMode?>(nameof(MotionCache));

    private readonly TranslateTransform _away = new();

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
    /// Open and all the way in (sliding or faded to full opacity): an opaque panel hides what is under it, which then
    /// needn't be drawn (Now Playing over the page and the sidebar).
    /// </summary>
    public bool Covers
    {
        get;
        private set => SetAndRaise(CoversProperty, ref field, value);
    }

    /// <summary>On its way in or out, sliding or fading, including the frame an animated move begins.</summary>
    public bool IsMoving
    {
        get;
        private set => SetAndRaise(IsMovingProperty, ref field, value);
    }

    /// <summary>What it holds draws through while it moves (Styles); null: always drawn live.</summary>
    public CacheMode? MotionCache
    {
        get => GetValue(MotionCacheProperty);
        set => SetValue(MotionCacheProperty, value);
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
                 || change.Property == EdgeProperty || change.Property == MotionCacheProperty)
        {
            Show();
        }
    }

    private void Show()
    {
        // Fading, it stays in place even closed (invisible), so what is on it can be measured.
        IsHitTestVisible = IsOpen;
        IsVisible = IsOpen || Shown > 0 || IsFading;
        Covers = IsOpen && Shown >= 1;
        IsMoving = IsVisible && (Shown is > 0 and < 1 || (HasShownTransition() && (IsOpen ? Shown < 1 : Shown > 0)));
        var cache = IsMoving ? MotionCache : null;
        foreach (var child in Children)
        {
            if (cache is null)
            {
                child.ClearValue(CacheModeProperty); // still: whatever its styles say
            }
            else
            {
                child.CacheMode = cache;
            }
        }

        Opacity = IsFading ? Shown : 1;
        var away = 1 - Shown;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        (_away.X, _away.Y) = Edge == Dock.Right
            ? (Snap(away * Aside * Bounds.Width, scale), 0d)
            : (0d, Snap(away * Below * Bounds.Height, scale));
        RenderTransform = IsFading ? null : _away; // one transform, moved: not a new one each step
    }

    private bool HasShownTransition()
    {
        if (Transitions is not { Count: > 0 } transitions)
        {
            return false;
        }

        foreach (var transition in transitions)
        {
            if (transition is TransitionBase t && t.Property == ShownProperty && t.Duration > TimeSpan.Zero)
            {
                return true;
            }
        }

        return false;
    }

    private static double Snap(double units, double scale) => scale > 0 ? Math.Round(units * scale) / scale : units;
}
