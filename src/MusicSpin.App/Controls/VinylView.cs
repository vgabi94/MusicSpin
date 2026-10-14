using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A record (mockup: "Vinyl"): a black disc with grooves, the album cover as the centre label, the spindle hole, and a
/// light reflection that stays still while the disc turns. The disc is a child rotated by a render transform, so turning
/// it doesn't redraw it. Colours and proportions are styled properties set in Styles/Artwork.axaml.
/// </summary>
public sealed class VinylView : Control
{
    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<VinylView, double>(nameof(Angle));

    public static readonly StyledProperty<IImage?> LabelProperty = AvaloniaProperty.Register<VinylView, IImage?>(nameof(Label));

    public static readonly StyledProperty<IBrush?> DiscBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(DiscBrush));

    public static readonly StyledProperty<IBrush?> GrooveBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(GrooveBrush));

    public static readonly StyledProperty<IBrush?> EdgeBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(EdgeBrush));

    public static readonly StyledProperty<IBrush?> LabelFallbackBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(LabelFallbackBrush));

    public static readonly StyledProperty<IBrush?> LabelRimBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(LabelRimBrush));

    public static readonly StyledProperty<IBrush?> HoleBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(HoleBrush));

    public static readonly StyledProperty<IBrush?> HoleRimBrushProperty = AvaloniaProperty.Register<VinylView, IBrush?>(nameof(HoleRimBrush));

    public static readonly StyledProperty<Color> SheenColorProperty = AvaloniaProperty.Register<VinylView, Color>(nameof(SheenColor));

    /// <summary>The label's diameter as a share of the disc's (0.35).</summary>
    public static readonly StyledProperty<double> LabelScaleProperty = AvaloniaProperty.Register<VinylView, double>(nameof(LabelScale));

    /// <summary>The hole's diameter as a share of the disc's (0.032).</summary>
    public static readonly StyledProperty<double> HoleScaleProperty = AvaloniaProperty.Register<VinylView, double>(nameof(HoleScale));

    /// <summary>Distance between grooves in pixels.</summary>
    public static readonly StyledProperty<double> GroovePitchProperty = AvaloniaProperty.Register<VinylView, double>(nameof(GroovePitch));

    private readonly Disc _disc;
    private readonly Sheen _sheen;

    static VinylView()
    {
        AffectsRender<VinylView>(SheenColorProperty);
        AngleProperty.Changed.AddClassHandler<VinylView>((v, _) => v._disc.RenderTransform = new RotateTransform(v.Angle));
        foreach (var property in new AvaloniaProperty[]
                 {
                     LabelProperty, DiscBrushProperty, GrooveBrushProperty, EdgeBrushProperty, LabelFallbackBrushProperty, LabelRimBrushProperty,
                     HoleBrushProperty, HoleRimBrushProperty, LabelScaleProperty, HoleScaleProperty, GroovePitchProperty,
                 })
        {
            property.Changed.AddClassHandler<VinylView>((v, _) => v._disc.InvalidateVisual());
        }

        SheenColorProperty.Changed.AddClassHandler<VinylView>((v, _) => v._sheen.InvalidateVisual());
    }

    public VinylView()
    {
        _disc = new Disc(this) { RenderTransformOrigin = RelativePoint.Center };
        _sheen = new Sheen(this);
        VisualChildren.Add(_disc);
        VisualChildren.Add(_sheen);
    }

    /// <summary>The disc's rotation in degrees (from the shared spin clock while its album plays).</summary>
    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public IImage? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IBrush? DiscBrush
    {
        get => GetValue(DiscBrushProperty);
        set => SetValue(DiscBrushProperty, value);
    }

    public IBrush? GrooveBrush
    {
        get => GetValue(GrooveBrushProperty);
        set => SetValue(GrooveBrushProperty, value);
    }

    public IBrush? EdgeBrush
    {
        get => GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    public IBrush? LabelFallbackBrush
    {
        get => GetValue(LabelFallbackBrushProperty);
        set => SetValue(LabelFallbackBrushProperty, value);
    }

    public IBrush? LabelRimBrush
    {
        get => GetValue(LabelRimBrushProperty);
        set => SetValue(LabelRimBrushProperty, value);
    }

    public IBrush? HoleBrush
    {
        get => GetValue(HoleBrushProperty);
        set => SetValue(HoleBrushProperty, value);
    }

    public IBrush? HoleRimBrush
    {
        get => GetValue(HoleRimBrushProperty);
        set => SetValue(HoleRimBrushProperty, value);
    }

    public Color SheenColor
    {
        get => GetValue(SheenColorProperty);
        set => SetValue(SheenColorProperty, value);
    }

    public double LabelScale
    {
        get => GetValue(LabelScaleProperty);
        set => SetValue(LabelScaleProperty, value);
    }

    public double HoleScale
    {
        get => GetValue(HoleScaleProperty);
        set => SetValue(HoleScaleProperty, value);
    }

    public double GroovePitch
    {
        get => GetValue(GroovePitchProperty);
        set => SetValue(GroovePitchProperty, value);
    }

    /// <summary>The disc's rotation as rendered (tests check that discs of the playing album agree).</summary>
    public double RenderedAngle => (_disc.RenderTransform as RotateTransform)?.Angle ?? 0;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = Math.Min(finalSize.Width, finalSize.Height);
        var rect = new Rect((finalSize.Width - side) / 2, (finalSize.Height - side) / 2, side, side);
        _disc.Arrange(rect);
        _sheen.Arrange(rect);
        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _disc.Measure(availableSize);
        _sheen.Measure(availableSize);
        return default;
    }

    /// <summary>The turning part: grooves, label, hole.</summary>
    private sealed class Disc(VinylView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var r = Bounds.Width / 2;
            if (r <= 0)
            {
                return;
            }

            var center = new Point(r, r);
            context.DrawEllipse(owner.DiscBrush, owner.EdgeBrush is { } edge ? new Pen(edge, 1) : null, center, r, r);
            var labelR = r * owner.LabelScale;
            if (owner.GrooveBrush is { } groove && owner.GroovePitch > 0)
            {
                var pen = new Pen(groove, owner.GroovePitch / 2);
                for (var gr = labelR + owner.GroovePitch; gr < r - owner.GroovePitch; gr += owner.GroovePitch)
                {
                    context.DrawEllipse(null, pen, center, gr, gr);
                }
            }

            var label = new Rect(r - labelR, r - labelR, labelR * 2, labelR * 2);
            using (context.PushGeometryClip(new EllipseGeometry(label)))
            {
                if (owner.Label is { } image)
                {
                    context.DrawImage(image, Fill(image.Size), label);
                }
                else if (owner.LabelFallbackBrush is { } fallback)
                {
                    context.FillRectangle(fallback, label);
                }
            }

            if (owner.LabelRimBrush is { } rim)
            {
                context.DrawEllipse(null, new Pen(rim, 1), center, labelR, labelR);
            }

            var holeR = r * owner.HoleScale;
            context.DrawEllipse(owner.HoleBrush, owner.HoleRimBrush is { } holeRim ? new Pen(holeRim, 1) : null, center, holeR, holeR);
        }

        /// <summary>The centred square of the image (covers are square; others are cropped).</summary>
        private static Rect Fill(Size size)
        {
            var side = Math.Min(size.Width, size.Height);
            return new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
        }
    }

    /// <summary>The still reflection on the grooves (mockup: .vinyl .sheen, a conic gradient outside the label).</summary>
    private sealed class Sheen(VinylView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var r = Bounds.Width / 2;
            if (r <= 0 || owner.SheenColor.A == 0)
            {
                return;
            }

            var bright = owner.SheenColor;
            var soft = Color.FromArgb((byte)(bright.A * 0.75), bright.R, bright.G, bright.B);
            var clear = Color.FromArgb(0, bright.R, bright.G, bright.B);
            var brush = new ConicGradientBrush
            {
                Angle = 20,
                GradientStops =
                {
                    new GradientStop(clear, 0), new GradientStop(clear, 0.06), new GradientStop(bright, 0.11), new GradientStop(clear, 0.17),
                    new GradientStop(clear, 0.5), new GradientStop(soft, 0.61), new GradientStop(clear, 0.67), new GradientStop(clear, 1),
                },
            };
            var inner = r * owner.LabelScale * 0.98;
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Rect(0, 0, r * 2, r * 2)), new EllipseGeometry(new Rect(r - inner, r - inner, inner * 2, inner * 2)));
            context.DrawGeometry(brush, null, ring);
        }
    }
}
