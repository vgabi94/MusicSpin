using Avalonia;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// Evenly spaced ticks round the edge of a circle (CSS <c>repeating-conic-gradient</c>): a platter's strobe marks or its
/// striped rim. <see cref="Count"/> ticks, each <see cref="TickAngle"/> degrees wide, from <see cref="InnerRatio"/> of the
/// radius out to the edge, the first starting at the top; the ring turns with <see cref="Angle"/>.
/// </summary>
public sealed class TickRing : Avalonia.Controls.Control
{
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<TickRing, int>(nameof(Count), 72);

    public static readonly StyledProperty<double> TickAngleProperty = AvaloniaProperty.Register<TickRing, double>(nameof(TickAngle), 1.2);

    public static readonly StyledProperty<double> InnerRatioProperty = AvaloniaProperty.Register<TickRing, double>(nameof(InnerRatio), 0.9);

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<TickRing, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<TickRing, double>(nameof(Angle));

    private Geometry? _ticks;

    static TickRing()
    {
        AffectsRender<TickRing>(FillProperty, AngleProperty);
        foreach (var property in new AvaloniaProperty[] { CountProperty, TickAngleProperty, InnerRatioProperty, BoundsProperty })
        {
            property.Changed.AddClassHandler<TickRing>((ring, _) =>
            {
                ring._ticks = null;
                ring.InvalidateVisual();
            });
        }
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Each tick's width in degrees.</summary>
    public double TickAngle
    {
        get => GetValue(TickAngleProperty);
        set => SetValue(TickAngleProperty, value);
    }

    /// <summary>Where the ticks start, as a share of the radius.</summary>
    public double InnerRatio
    {
        get => GetValue(InnerRatioProperty);
        set => SetValue(InnerRatioProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The ring's rotation in degrees (the record's, so the marks turn with it).</summary>
    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Fill is null || Count <= 0)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        using (context.PushTransform(Matrix.CreateTranslation(-centre.X, -centre.Y) * Matrix.CreateRotation(Angle * Math.PI / 180)
                                     * Matrix.CreateTranslation(centre.X, centre.Y)))
        {
            context.DrawGeometry(Fill, null, _ticks ??= Ticks(centre));
        }
    }

    private StreamGeometry Ticks(Point centre)
    {
        var outer = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var inner = outer * InnerRatio;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        for (var i = 0; i < Count; i++)
        {
            var from = 360.0 * i / Count;
            var to = from + TickAngle;
            context.BeginFigure(At(centre, inner, from), true);
            context.LineTo(At(centre, outer, from));
            context.ArcTo(At(centre, outer, to), new Size(outer, outer), 0, false, SweepDirection.Clockwise);
            context.LineTo(At(centre, inner, to));
            context.ArcTo(At(centre, inner, from), new Size(inner, inner), 0, false, SweepDirection.CounterClockwise);
            context.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>The point <paramref name="degrees"/> clockwise from the top, <paramref name="radius"/> out.</summary>
    private static Point At(Point centre, double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(centre.X + (radius * Math.Sin(a)), centre.Y - (radius * Math.Cos(a)));
    }
}
