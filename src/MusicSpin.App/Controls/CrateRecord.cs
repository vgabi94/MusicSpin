using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MusicSpin.App.Controls;

/// <summary>
/// A record in a crate (mockup: .crate .rec, layoutCrate), laid out at the crate's full size (k = 1; grouped crates are
/// scaled as a whole). <see cref="Position"/> is its place from the front record: 0 leans forward at the front, 1, 2, …
/// recede and darken, −1 has flipped forward out of view. Positions in between blend the poses, so a transition on
/// <see cref="Position"/> moves a record the way the mockup's transform transition does.
/// </summary>
/// <remarks>
/// <para>
/// Template parts: PART_Record (the sleeve; it gets the projection) and, inside it, PART_Shade (black, its opacity the
/// darkening: mockup filter: brightness). The control itself takes no space of its own and no input; PART_Record does.
/// </para>
/// <para>
/// The eye is the crate's (mockup: .crate, perspective 1000px, origin 50% (100% − 820px)): with the record standing
/// 70 above the crate's bottom, that is <see cref="EyeAbove"/> above the record's top, at its middle.
/// </para>
/// </remarks>
[TemplatePart("PART_Record", typeof(Control))]
[TemplatePart("PART_Shade", typeof(Control))]
public sealed class CrateRecord : TemplatedControl
{
    /// <summary>The crate's perspective distance (mockup: perspective: 1000px).</summary>
    public const double Perspective = 1000;

    /// <summary>The eye above the record's top: 820 − 70 − 300.</summary>
    public const double EyeAbove = 450;

    /// <summary>How far back each record behind the front one stands (mockup: translateZ(−26px × d)).</summary>
    private const double Spacing = 26;

    /// <summary>The records behind darken by this much each, down to <see cref="Darkest"/> (mockup: brightness(1 − .07d), .45).</summary>
    private const double DarkenStep = 0.07;
    private const double Darkest = 0.45;

    /// <summary>Records further back than this are hidden (mockup: d &gt; 9 → opacity 0).</summary>
    public const int Deepest = 9;

    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<CrateRecord, double>(nameof(Position));

    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<CrateRecord, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<CrateRecord, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ArtistProperty = AvaloniaProperty.Register<CrateRecord, string?>(nameof(Artist));

    private Control? _record;
    private Control? _shade;

    static CrateRecord()
    {
        AffectsArrange<CrateRecord>(PositionProperty);
    }

    /// <summary>The place from the front record (0); fractions while flipping.</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public Bitmap? Source
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

    /// <summary>The record's projection now (tests read it).</summary>
    public Projection RecordProjection { get; private set; } = Projection.Identity;

    /// <summary>How the record stands at <paramref name="position"/>: translateZ, translateY, rotateX, opacity, brightness.</summary>
    public static (double Z, double Y, double Tilt, double Opacity, double Brightness) Pose(double position)
    {
        (double Z, double Y, double Tilt) flipped = (140, 40, -70), front = (30, -40, -4);
        (double Z, double Y, double Tilt) Behind(double d) => (-Spacing * d, 0, Math.Min(18, 8 + (2 * d)));

        var (z, y, tilt) = position switch
        {
            <= -1 => flipped,
            < 0 => Blend(flipped, front, position + 1),
            < 1 => Blend(front, Behind(1), position),
            _ => Behind(position),
        };
        var opacity = Math.Clamp(Math.Min(position + 1, Deepest + 1 - position), 0, 1);
        var brightness = position > 0 ? Math.Max(Darkest, 1 - (DarkenStep * position)) : 1;
        return (z, y, tilt, opacity, brightness);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        _record = e.NameScope.Find<Control>("PART_Record");
        _shade = e.NameScope.Find<Control>("PART_Shade");
        _record?.RenderTransformOrigin = RelativePoint.TopLeft;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        var (z, y, tilt, opacity, brightness) = Pose(Position);
        var size = arranged.Width;
        RecordProjection = Projection.Perspective(Perspective, new Point(size / 2, -EyeAbove))
                           * Projection.About(new Point(size / 2, arranged.Height),
                               Projection.Translate(0, 0, z) * Projection.Translate(0, y, 0) * Projection.RotateX(tilt));
        if (_record is not null)
        {
            _record.RenderTransform = new MatrixTransform(RecordProjection.ToMatrix());
            _record.Opacity = opacity;
        }

        _shade?.Opacity = 1 - brightness;
        return arranged;
    }

    private static (double, double, double) Blend((double, double, double) a, (double, double, double) b, double t)
        => (a.Item1 + ((b.Item1 - a.Item1) * t), a.Item2 + ((b.Item2 - a.Item2) * t), a.Item3 + ((b.Item3 - a.Item3) * t));
}
