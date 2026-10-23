using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A round thing's soft shadow, as a <see cref="DropShadowEffect"/> or a <see cref="BoxShadow"/> of <see cref="Blur"/>
/// draws it, without blurring anything: blurred by a Gaussian of σ, a disc of radius R is, to well under 1% while R is
/// several σ, a radial gradient whose alpha falls as ½·erfc((r − R) / (σ√2)) across R ± 3σ. So a frame fills one circle,
/// where the effect or the shadow blurred it again each frame: the turntable scene's record (#129), a platter's rim under
/// the spinning record (#122). Place it where the disc is. Standing in for a drop shadow, move it with a transform (the
/// effect's offset is in the disc's units); for a box shadow, <see cref="PixelOffsetY"/> moves it and
/// <see cref="OutsideOnly"/> leaves the disc's own place clear, as Avalonia draws one.
/// </summary>
public sealed class DiscShadow : Control
{
    /// <summary>Gradient stops across R ± 3σ: enough that the falloff looks smooth.</summary>
    private const int Stops = 16;

    /// <summary>Skia's blur radius to σ, as Avalonia converts an effect's radius (Avalonia.Skia: SkBlurRadiusToSigma).</summary>
    private const double SigmaPerRadius = 0.288675;
    private const double SigmaBase = 0.5;

    public static readonly StyledProperty<double> BlurProperty = AvaloniaProperty.Register<DiscShadow, double>(nameof(Blur));

    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<DiscShadow, Color>(nameof(Color), Colors.Black);

    public static readonly StyledProperty<double> StrengthProperty = AvaloniaProperty.Register<DiscShadow, double>(nameof(Strength), 1);

    public static readonly StyledProperty<double> PixelOffsetYProperty = AvaloniaProperty.Register<DiscShadow, double>(nameof(PixelOffsetY));

    public static readonly StyledProperty<bool> OutsideOnlyProperty = AvaloniaProperty.Register<DiscShadow, bool>(nameof(OutsideOnly));

    private IBrush? _brush;

    /// <summary>The window's pixels a unit here, when it was last drawn.</summary>
    private double _pixels = 1;

    static DiscShadow()
    {
        AffectsRender<DiscShadow>(BlurProperty, ColorProperty, StrengthProperty, PixelOffsetYProperty, OutsideOnlyProperty);
        foreach (var property in new AvaloniaProperty[] { BlurProperty, ColorProperty, StrengthProperty, BoundsProperty })
        {
            property.Changed.AddClassHandler<DiscShadow>((shadow, _) => shadow._brush = null);
        }
    }

    public DiscShadow()
    {
        // Scaled from above (a deck in a Viewbox, resized), it draws its offset again for the new scale.
        LayoutUpdated += (_, _) =>
        {
            if (PixelOffsetY != 0 && Math.Abs(Pixels() - _pixels) > 1e-6)
            {
                InvalidateVisual();
            }
        };
    }

    /// <summary>The blur, as a <see cref="DropShadowEffect.BlurRadius"/>.</summary>
    public double Blur
    {
        get => GetValue(BlurProperty);
        set => SetValue(BlurProperty, value);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>The shadow's opacity, as a <see cref="DropShadowEffect.Opacity"/>.</summary>
    public double Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    /// <summary>
    /// How far below the disc the shadow falls, in the window's pixels whatever the scale: as Avalonia offsets a box shadow
    /// (after its transform), so one standing in for a box shadow falls where it did.
    /// </summary>
    public double PixelOffsetY
    {
        get => GetValue(PixelOffsetYProperty);
        set => SetValue(PixelOffsetYProperty, value);
    }

    /// <summary>Nothing is drawn where the disc itself is (a box shadow's outer shadow).</summary>
    public bool OutsideOnly
    {
        get => GetValue(OutsideOnlyProperty);
        set => SetValue(OutsideOnlyProperty, value);
    }

    /// <summary>How often its gradient was made (tests check it isn't blurred, nor made again, while nothing changes).</summary>
    internal int Made { get; private set; }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (radius <= 0 || Strength <= 0)
        {
            return;
        }

        var sigma = Sigma(Blur);
        var reach = radius + (3 * sigma);
        _pixels = Pixels();
        var offset = PixelOffsetY / _pixels;
        var disc = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var centre = disc + new Vector(0, offset);
        using (OutsideOnly ? context.PushGeometryClip(Outside(disc, radius, reach + Math.Abs(offset))) : default(DrawingContext.PushedState?))
        {
            context.DrawEllipse(_brush ??= Make(radius, sigma, reach), null, centre, reach, reach);
        }
    }

    /// <summary>All round the disc, out to <paramref name="reach"/>, but not the disc.</summary>
    private static CombinedGeometry Outside(Point disc, double radius, double reach)
        => new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(disc.X - reach, disc.Y - reach, 2 * reach, 2 * reach)),
            new EllipseGeometry(new Rect(disc.X - radius, disc.Y - radius, 2 * radius, 2 * radius)));

    /// <summary>The window's pixels a unit here: its scaling and every transform above.</summary>
    private double Pixels()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || this.TransformToVisual(top) is not { } m)
        {
            return 1;
        }

        var scale = Math.Sqrt(Math.Abs((m.M11 * m.M22) - (m.M12 * m.M21))) * top.RenderScaling;
        return scale > 0 ? scale : 1;
    }

    /// <summary>The Gaussian's σ for a blur radius, as Skia blurs it (0: no blur, a sharp disc).</summary>
    internal static double Sigma(double blur) => blur > 0 ? (SigmaPerRadius * blur) + SigmaBase : 0;

    /// <summary>The shadow's alpha at <paramref name="r"/> from the centre, as a share of its full strength.</summary>
    internal static double Falloff(double r, double radius, double sigma)
        => sigma <= 0 ? (r <= radius ? 1 : 0) : 0.5 * Erfc((r - radius) / (sigma * Math.Sqrt(2)));

    private RadialGradientBrush Make(double radius, double sigma, double reach)
    {
        Made++;
        var stops = new GradientStops();
        var from = Math.Max(0, radius - (3 * sigma));
        stops.Add(new GradientStop(Shade(1), 0));
        for (var i = 0; i <= Stops; i++)
        {
            var r = from + ((reach - from) * i / Stops);
            stops.Add(new GradientStop(Shade(Falloff(r, radius, sigma)), r / reach));
        }

        return new RadialGradientBrush { GradientStops = stops };
    }

    private Color Shade(double share) => Color.FromArgb((byte)Math.Round(Math.Clamp(Color.A * Strength * share, 0, 255)), Color.R, Color.G, Color.B);

    /// <summary>The complementary error function (Abramowitz and Stegun 7.1.26, within 1.5e-7).</summary>
    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1 / (1 + (0.3275911 * z));
        var poly = t * (0.254829592 + (t * (-0.284496736 + (t * (1.421413741 + (t * (-1.453152027 + (t * 1.061405429))))))));
        var erfc = poly * Math.Exp(-z * z);
        return x >= 0 ? erfc : 2 - erfc;
    }
}
