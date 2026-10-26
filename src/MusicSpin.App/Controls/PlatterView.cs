using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A turntable's platter (mockup: .deck .platter): a plain disc with a dark ring around it. The mockup's fine radial
/// stripes are gone (#143).
/// </summary>
public sealed class PlatterView : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<PlatterView, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> RingBrushProperty = AvaloniaProperty.Register<PlatterView, IBrush?>(nameof(RingBrush));

    /// <summary>The ring's width, outside the disc (mockup: box-shadow 0 0 0 4px).</summary>
    public static readonly StyledProperty<double> RingWidthProperty = AvaloniaProperty.Register<PlatterView, double>(nameof(RingWidth));

    static PlatterView()
    {
        AffectsRender<PlatterView>(BackgroundProperty, RingBrushProperty, RingWidthProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public double RingWidth
    {
        get => GetValue(RingWidthProperty);
        set => SetValue(RingWidthProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (r <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (RingBrush is { } ring && RingWidth > 0)
        {
            context.DrawEllipse(null, new Pen(ring, RingWidth), center, r + (RingWidth / 2), r + (RingWidth / 2));
        }

        context.DrawEllipse(Background, null, center, r, r);
    }
}
