using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A turntable's platter (mockup: .deck .platter): a disc in fine radial stripes, <see cref="StripeAngle"/> degrees of
/// <see cref="StripeBrush"/> then as many of <see cref="Background"/>, with a dark ring around it.
/// </summary>
public sealed class PlatterView : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<PlatterView, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> StripeBrushProperty = AvaloniaProperty.Register<PlatterView, IBrush?>(nameof(StripeBrush));

    public static readonly StyledProperty<double> StripeAngleProperty = AvaloniaProperty.Register<PlatterView, double>(nameof(StripeAngle), 2);

    public static readonly StyledProperty<IBrush?> RingBrushProperty = AvaloniaProperty.Register<PlatterView, IBrush?>(nameof(RingBrush));

    /// <summary>The ring's width, outside the disc (mockup: box-shadow 0 0 0 4px).</summary>
    public static readonly StyledProperty<double> RingWidthProperty = AvaloniaProperty.Register<PlatterView, double>(nameof(RingWidth));

    private StreamGeometry? _stripes;
    private double _stripesFor;

    static PlatterView()
    {
        AffectsRender<PlatterView>(BackgroundProperty, StripeBrushProperty, StripeAngleProperty, RingBrushProperty, RingWidthProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? StripeBrush
    {
        get => GetValue(StripeBrushProperty);
        set => SetValue(StripeBrushProperty, value);
    }

    public double StripeAngle
    {
        get => GetValue(StripeAngleProperty);
        set => SetValue(StripeAngleProperty, value);
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
        if (StripeBrush is { } stripe && StripeAngle > 0)
        {
            context.DrawGeometry(stripe, null, Stripes(center, r));
        }
    }

    /// <summary>Every other wedge of <see cref="StripeAngle"/> degrees.</summary>
    private StreamGeometry Stripes(Point center, double r)
    {
        var key = r + (StripeAngle * 1e6);
        if (_stripes is not null && _stripesFor.Equals(key))
        {
            return _stripes;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var step = StripeAngle * Math.PI / 180;
            for (var a = 0.0; a < 2 * Math.PI - 1e-9; a += 2 * step)
            {
                g.BeginFigure(center, true);
                g.LineTo(center + new Vector(r * Math.Cos(a), r * Math.Sin(a)));
                g.ArcTo(center + new Vector(r * Math.Cos(a + step), r * Math.Sin(a + step)), new Size(r, r), 0, false, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
        }

        (_stripes, _stripesFor) = (geometry, key);
        return geometry;
    }
}
