using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A panel that slides up into place when it opens and back down when it closes (mockup: .nowplaying, translate 0 104%),
/// or, while <see cref="IsFading"/>, fades in and out in place (mockup: .nowplaying.scene). <see cref="Shown"/> is set
/// here from <see cref="IsOpen"/> as a local value and animated by the styles' transitions; it's hidden once it's down.
/// </summary>
public sealed class SlidePanel : Panel
{
    /// <summary>How far below its place a closed panel sits, as a share of its height (mockup: 104%).</summary>
    private const double Below = 1.04;

    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<SlidePanel, bool>(nameof(IsOpen));

    public static readonly StyledProperty<double> ShownProperty = AvaloniaProperty.Register<SlidePanel, double>(nameof(Shown));

    public static readonly StyledProperty<bool> IsFadingProperty = AvaloniaProperty.Register<SlidePanel, bool>(nameof(IsFading));

    public SlidePanel()
    {
        Show();
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>0: down (hidden); 1: in place.</summary>
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
        else if (change.Property == ShownProperty || change.Property == IsFadingProperty || change.Property == BoundsProperty)
        {
            Show();
        }
    }

    private void Show()
    {
        // Fading, it stays in place even closed (invisible), so what is on it can be measured.
        IsHitTestVisible = IsOpen;
        IsVisible = IsOpen || Shown > 0 || IsFading;
        Opacity = IsFading ? Shown : 1;
        RenderTransform = IsFading ? null : new TranslateTransform(0, (1 - Shown) * Below * Bounds.Height);
    }
}
