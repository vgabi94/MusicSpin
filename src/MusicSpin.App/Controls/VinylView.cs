using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;

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

    /// <summary>Set: the record's images are drawn at this scale of its size (times the window's), not as it shows now.</summary>
    public static readonly StyledProperty<double> ImageScaleProperty = AvaloniaProperty.Register<VinylView, double>(nameof(ImageScale));

    private readonly Grooves _grooves;
    private readonly Disc _disc;
    private readonly Sheen _sheen;

    static VinylView()
    {
        AffectsRender<VinylView>(SheenColorProperty);
        AngleProperty.Changed.AddClassHandler<VinylView>((v, _) => v._disc.Turn(v.Angle));
        // Each part's image is drawn again only when something it draws changes.
        ImageScaleProperty.Changed.AddClassHandler<VinylView>((v, _) =>
        {
            v._grooves.Redraw();
            v._disc.Redraw();
        });
        foreach (var property in new AvaloniaProperty[] { DiscBrushProperty, GrooveBrushProperty, EdgeBrushProperty, LabelScaleProperty, GroovePitchProperty })
        {
            property.Changed.AddClassHandler<VinylView>((v, _) => v._grooves.Redraw());
        }

        foreach (var property in new AvaloniaProperty[]
                 {
                     LabelProperty, LabelFallbackBrushProperty, LabelRimBrushProperty, LabelScaleProperty, HoleBrushProperty, HoleRimBrushProperty, HoleScaleProperty,
                 })
        {
            property.Changed.AddClassHandler<VinylView>((v, _) => v._disc.Redraw());
        }

        SheenColorProperty.Changed.AddClassHandler<VinylView>((v, _) => v._sheen.InvalidateVisual());
    }

    public VinylView()
    {
        _grooves = new Grooves(this);
        _disc = new Disc(this);
        _sheen = new Sheen(this);
        VisualChildren.Add(_grooves);
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

    /// <summary>
    /// The scale of its laid-out size the record's images are drawn for (0, the default: as it shows now). For a record
    /// that render transforms grow without a layout pass (the turntable scene's flying record): drawn for the largest size
    /// it reaches, it isn't stretched as it lands.
    /// </summary>
    public double ImageScale
    {
        get => GetValue(ImageScaleProperty);
        set => SetValue(ImageScaleProperty, value);
    }

    /// <summary>The still part's image, in pixels across (tests check it's sharp where it shows).</summary>
    internal int GrooveImagePixels => _grooves.Pixels;

    /// <summary>The disc's rotation as rendered (tests check that discs of the playing album agree).</summary>
    public double RenderedAngle => _disc.RenderedAngle;

    /// <summary>How often the record's drawing was recorded, its grooves and its label (turning mustn't: tests count).</summary>
    internal int DiscRenders => _grooves.Renders + _disc.Renders;

    /// <summary>How often the still part's image (disc, grooves) was drawn: only when it changes or shows at another size.</summary>
    internal int GrooveImages => _grooves.Images;

    /// <summary>How often the turning part's image (label, hole) was drawn: only when it changes or shows at another size.</summary>
    internal int LabelImages => _disc.Images;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = Math.Min(finalSize.Width, finalSize.Height);
        var rect = new Rect((finalSize.Width - side) / 2, (finalSize.Height - side) / 2, side, side);
        _grooves.Arrange(rect);
        _disc.Arrange(rect);
        _sheen.Arrange(rect);
        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _grooves.Measure(availableSize);
        _disc.Measure(availableSize);
        _sheen.Measure(availableSize);
        return default;
    }

    /// <summary>
    /// A part of the record drawn once into an image as large as it shows on screen, and that image drawn after: a frame
    /// of a spinning record is two images, not dozens of groove circles and a full-size cover. The image is drawn again
    /// when the record changes, or shows much larger or smaller (a resized window).
    /// </summary>
#pragma warning disable CA1001 // controls aren't disposed: the image is released when the part leaves the tree or is drawn again
    private abstract class CachedPart : Control
#pragma warning restore CA1001
    {
        /// <summary>How far the shown size may drift from the image's before it is drawn again.</summary>
        private const double Drift = 0.15;

        /// <summary>The largest image a record keeps, in pixels across.</summary>
        private const int MaxPixels = 2048;

        /// <summary>A clear border round the image, in pixels: strokes and smoothing reach past the part's edge.</summary>
        private const int Border = 2;

        private RenderTargetBitmap? _image;
        private double _imageScale;

        protected CachedPart(VinylView owner)
        {
            Owner = owner;
            LayoutUpdated += (_, _) =>
            {
                if (_image is not null && Math.Abs(Scale() - _imageScale) > _imageScale * Drift)
                {
                    Redraw();
                }
            };
        }

        protected VinylView Owner { get; }

        /// <summary>How often its drawing was recorded.</summary>
        public int Renders { get; private set; }

        /// <summary>How often its image was drawn.</summary>
        public int Images { get; private set; }

        /// <summary>Its image's size, in pixels across (without the border).</summary>
        public int Pixels { get; private set; }

        /// <summary>The record changed: the image is drawn again at the next frame.</summary>
        public void Redraw()
        {
            _image?.Dispose();
            _image = null;
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _image?.Dispose();
            _image = null;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            ArgumentNullException.ThrowIfNull(change);
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty && change.GetOldValue<Rect>().Size != change.GetNewValue<Rect>().Size)
            {
                Redraw();
            }
        }

        public override void Render(DrawingContext context)
        {
            Renders++;
            var side = Bounds.Width;
            if (side <= 0)
            {
                return;
            }

            if (_image is null)
            {
                Images++;
                var pixels = (int)Math.Clamp(Math.Ceiling(side * Scale()), 1, MaxPixels);
                Pixels = pixels;
                _imageScale = pixels / side;
                _image = new RenderTargetBitmap(new PixelSize(pixels + (2 * Border), pixels + (2 * Border)));
                using var drawing = _image.CreateDrawingContext();
                using (drawing.PushTransform(Matrix.CreateScale(_imageScale, _imageScale) * Matrix.CreateTranslation(Border, Border)))
                {
                    Draw(drawing, side / 2);
                }
            }

            var margin = Border / _imageScale;
            context.DrawImage(_image, new Rect(-margin, -margin, side + (2 * margin), side + (2 * margin)));
        }

        /// <summary>Draws the part on a record of radius <paramref name="r"/>, its centre at (r, r).</summary>
        protected abstract void Draw(DrawingContext context, double r);

        /// <summary>
        /// Screen pixels per unit here: the window's scaling and every transform above (rotation aside), or the record's
        /// <see cref="ImageScale"/> of the window's scaling.
        /// </summary>
        private double Scale()
        {
            var top = TopLevel.GetTopLevel(this);
            if (Owner.ImageScale > 0)
            {
                return Owner.ImageScale * (top?.RenderScaling ?? 1);
            }

            if (top is null || this.TransformToVisual(top) is not { } m)
            {
                return 1;
            }

            var scale = Math.Sqrt(Math.Abs((m.M11 * m.M22) - (m.M12 * m.M21))) * top.RenderScaling;
            return double.IsFinite(scale) && scale > 0 ? scale : 1;
        }
    }

    /// <summary>
    /// What doesn't change as the record turns: the disc, its edge and its grooves (circles about the centre). It stays
    /// still, so its image is drawn as it is, never resampled at an angle, and the grooves stay crisp.
    /// </summary>
    private sealed class Grooves(VinylView owner) : CachedPart(owner)
    {
        protected override void Draw(DrawingContext context, double r)
        {
            var center = new Point(r, r);
            context.DrawEllipse(Owner.DiscBrush, Owner.EdgeBrush is { } edge ? new Pen(edge, 1) : null, center, r, r);
            var labelR = r * Owner.LabelScale;
            if (Owner.GrooveBrush is { } groove && Owner.GroovePitch > 0)
            {
                var pen = new Pen(groove, Owner.GroovePitch / 2);
                for (var gr = labelR + Owner.GroovePitch; gr < r - Owner.GroovePitch; gr += Owner.GroovePitch)
                {
                    context.DrawEllipse(null, pen, center, gr, gr);
                }
            }
        }
    }

    /// <summary>
    /// What turns: the label (the cover) and the hole. It turns on its composition visual (rotation about its centre), so
    /// turning never records its drawing again: only the compositor moves it.
    /// </summary>
    private sealed class Disc(VinylView owner) : CachedPart(owner)
    {
        private double _angle;

        /// <summary>The angle the composition visual turns the label by, in degrees.</summary>
        public double RenderedAngle { get; private set; }

        /// <summary>Turns the label to <paramref name="degrees"/> on its composition visual (no new drawing).</summary>
        public void Turn(double degrees)
        {
            _angle = degrees;
            Apply();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Apply();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            ArgumentNullException.ThrowIfNull(change);
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty)
            {
                Apply(); // the centre it turns about
            }
        }

        public override void Render(DrawingContext context)
        {
            Apply(); // its composition visual exists by its first drawing (not yet when it was attached)
            base.Render(context);
        }

        protected override void Draw(DrawingContext context, double r)
        {
            var center = new Point(r, r);
            var labelR = r * Owner.LabelScale;
            var label = new Rect(r - labelR, r - labelR, labelR * 2, labelR * 2);
            using (context.PushGeometryClip(new EllipseGeometry(label)))
            {
                if (Owner.Label is { } image)
                {
                    context.DrawImage(image, Fill(image.Size), label);
                }
                else if (Owner.LabelFallbackBrush is { } fallback)
                {
                    context.FillRectangle(fallback, label);
                }
            }

            if (Owner.LabelRimBrush is { } rim)
            {
                context.DrawEllipse(null, new Pen(rim, 1), center, labelR, labelR);
            }

            var holeR = r * Owner.HoleScale;
            context.DrawEllipse(Owner.HoleBrush, Owner.HoleRimBrush is { } holeRim ? new Pen(holeRim, 1) : null, center, holeR, holeR);
        }

        private void Apply()
        {
            if (ElementComposition.GetElementVisual(this) is { } visual)
            {
                visual.CenterPoint = new Vector3D(Bounds.Width / 2, Bounds.Height / 2, 0);
                visual.RotationAngle = (float)(_angle * Math.PI / 180);
                RenderedAngle = _angle;
            }
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
        private (double R, Color Color, double Label) _made;
        private ConicGradientBrush? _brush;
        private Geometry? _ring;

        public override void Render(DrawingContext context)
        {
            var r = Bounds.Width / 2;
            if (r <= 0 || owner.SheenColor.A == 0)
            {
                return;
            }

            // The same reflection while the record and its size stay: made once, drawn as it is.
            if (_brush is null || _ring is null || _made != (r, owner.SheenColor, owner.LabelScale))
            {
                (_brush, _ring) = Make(r);
                _made = (r, owner.SheenColor, owner.LabelScale);
            }

            context.DrawGeometry(_brush, null, _ring);
        }

        private (ConicGradientBrush Brush, Geometry Ring) Make(double r)
        {
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
            return (brush, ring);
        }
    }
}
