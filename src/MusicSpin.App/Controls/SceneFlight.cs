using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using MusicSpin.App.Services;

namespace MusicSpin.App.Controls;

/// <summary>
/// The turntable scene's travelling sleeve and record (mockup: .fly .pack), drawn in the window's overlay layer at
/// <see cref="Time"/> into the scene: the sleeve flies from <see cref="Source"/> to the deck, the record slides out,
/// then the sleeve drifts away and the record crosses back onto <see cref="Landing"/>. Styles/NowPlaying.axaml draws it.
/// </summary>
/// <remarks>
/// The pack is the sleeve's size, S = the record's width / 0.96 (a 2% margin round the record), placed so the record
/// inside it sits on the deck's record; every movement is a transform of it, with the mockup's keyframes and easings.
/// The shelf's sleeves start square here (the mockup turns a shelf sleeve's copy 7° in 3D as it lifts).
/// </remarks>
[TemplatePart("PART_Pack", typeof(Control))]
[TemplatePart("PART_Record", typeof(Control))]
[TemplatePart("PART_Sleeve", typeof(Control))]
public sealed class SceneFlight : TemplatedControl
{
    /// <summary>The record's share of the sleeve (mockup: S = width / 0.96) and its margin in it.</summary>
    private const double RecordShare = 0.96;
    private const double RecordMargin = 0.02;

    /// <summary>The flight: lifted 12 px and 2% larger at 85% of the way (mockup: the pack's keyframes).</summary>
    private const double LiftAt = 0.85;
    private const double Lift = -12;
    private const double LiftScale = 1.02;

    /// <summary>How far the record slides out, as a share of its width.</summary>
    private const double SlideOut = 0.62;

    /// <summary>The sleeve drifting off: by (-0.3 S, 0.1 S), turned -8°, at 88%, fading out.</summary>
    private static readonly Vector Drift = new(-0.3, 0.1);
    private const double DriftAngle = -8;
    private const double DriftScale = 0.88;

    private static readonly SplineEasing FlightEasing = new(0.35, 0.6, 0.25, 1);
    private static readonly SplineEasing SlideEasing = new(0.5, 0, 0.2, 1);
    private static readonly SplineEasing DriftEasing = new(0.4, 0, 0.6, 1);
    private static readonly SplineEasing CrossEasing = new(0.45, 0, 0.3, 1);

    /// <summary>
    /// The record crossing back: at each offset, its position (x as a share of the slide), the lift, the scale and its
    /// drop shadow (y offset, blur, opacity), lifted highest a third of the way (mockup: the record's keyframes).
    /// </summary>
    private static readonly (double At, double X, double Y, double Scale, double ShadowY, double Blur, double Alpha)[] Cross =
    [
        (0, 1, 0, 1, 4, 6, 0x66 / 255.0),
        (0.35, 0.6, -14, 1.06, 34, 30, 0xAA / 255.0),
        (0.78, 0, -6, 1.045, 26, 24, 0x99 / 255.0),
        (1, 0, 0, 1, 2, 3, 0x88 / 255.0),
    ];

    public static readonly StyledProperty<double> TimeProperty = AvaloniaProperty.Register<SceneFlight, double>(nameof(Time));

    public static readonly StyledProperty<Rect> SourceProperty = AvaloniaProperty.Register<SceneFlight, Rect>(nameof(Source));

    public static readonly StyledProperty<Rect> LandingProperty = AvaloniaProperty.Register<SceneFlight, Rect>(nameof(Landing));

    public static readonly StyledProperty<IImage?> CoverProperty = AvaloniaProperty.Register<SceneFlight, IImage?>(nameof(Cover));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SceneFlight, string?>(nameof(Title));

    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<SceneFlight, double>(nameof(Angle));

    private Control? _pack;
    private Control? _record;
    private Control? _sleeve;

    /// <summary>Milliseconds into the scene at Motion Smooth (<see cref="SceneTimeline"/>).</summary>
    public double Time
    {
        get => GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    /// <summary>The sleeve where it sat (window coordinates).</summary>
    public Rect Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The deck's record (window coordinates).</summary>
    public Rect Landing
    {
        get => GetValue(LandingProperty);
        set => SetValue(LandingProperty, value);
    }

    public IImage? Cover
    {
        get => GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    /// <summary>The album's title (the sleeve's placeholder without a cover).</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The record's rotation, as the deck's (so it lands as the deck shows it).</summary>
    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    /// <summary>The travelling sleeve (PART_Sleeve).</summary>
    public Control? Sleeve => _sleeve;

    /// <summary>The travelling record (PART_Record).</summary>
    public Control? Record => _record;

    /// <summary>The sleeve and record together (PART_Pack): the sleeve's size, where the sleeve lands.</summary>
    public Control? Pack => _pack;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        _pack = e.NameScope.Find<Control>("PART_Pack");
        _record = e.NameScope.Find<Control>("PART_Record");
        _sleeve = e.NameScope.Find<Control>("PART_Sleeve");
        Place();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == TimeProperty || change.Property == SourceProperty || change.Property == LandingProperty
            || change.Property == BoundsProperty)
        {
            Place();
        }
    }

    private void Place()
    {
        if (_pack is null || _record is null || _sleeve is null || Landing.Width <= 0)
        {
            return;
        }

        // In this control's coordinates (the overlay layer sits on the window).
        var origin = TopLevel.GetTopLevel(this) is { } top && top.TranslatePoint(default, this) is { } at ? at : default;
        var record = Landing.Width;
        var size = record / RecordShare;
        var place = Landing.TopLeft + origin - new Point(RecordMargin * size, RecordMargin * size);
        Canvas.SetLeft(_pack, place.X);
        Canvas.SetTop(_pack, place.Y);
        (_pack.Width, _pack.Height) = (size, size);
        _record.Margin = new Thickness(RecordMargin * size, RecordMargin * size, 0, 0);
        (_record.Width, _record.Height) = (record, record);

        var t = Time;
        _pack.RenderTransform = new MatrixTransform(FlightAt(t, Source.TopLeft + origin - place, Source.Height / size));

        // The record: out of the sleeve, then lifted back across onto the platter, its shadow growing and shrinking.
        var slide = SlideOut * record;
        var crossing = t >= SceneTimeline.CrossAt;
        _record.ZIndex = crossing ? 3 : 1;
        _sleeve.ZIndex = 2;
        var (x, y, scale, shadowY, blur, alpha) = crossing
            ? Keyframes(CrossEasing.Ease(Progress(t, SceneTimeline.CrossAt + SceneTimeline.CrossDelayMs, SceneTimeline.CrossMs)))
            : (SlideEasing.Ease(Progress(t, SceneTimeline.SlideOutAt, SceneTimeline.SlideOutMs)), 0, 1, Cross[0].ShadowY, Cross[0].Blur, Cross[0].Alpha);
        _record.RenderTransform = new MatrixTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x * slide, y));
        _record.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = shadowY, BlurRadius = blur, Color = Colors.Black, Opacity = alpha };

        // The sleeve drifts away and fades as the record crosses.
        var drift = crossing ? DriftEasing.Ease(Progress(t, SceneTimeline.CrossAt, SceneTimeline.DriftMs)) : 0;
        var turn = Matrix.CreateScale(1 + ((DriftScale - 1) * drift), 1 + ((DriftScale - 1) * drift))
                   * Matrix.CreateRotation(DriftAngle * drift * Math.PI / 180)
                   * Matrix.CreateTranslation(Drift.X * size * drift, Drift.Y * size * drift);
        _sleeve.RenderTransform = new MatrixTransform(turn);
        _sleeve.Opacity = 1 - drift;
    }

    /// <summary>
    /// The pack in flight: from the sleeve where it sat (<paramref name="from"/>, at <paramref name="scale"/> of its
    /// size), lifted near the end, set down on the deck.
    /// </summary>
    private static Matrix FlightAt(double t, Vector from, double scale)
    {
        var p = FlightEasing.Ease(Progress(t, 0, SceneTimeline.FlightMs));
        var (x, y, s) = p <= LiftAt
            ? (Lerp(from.X, 0, p / LiftAt), Lerp(from.Y, Lift, p / LiftAt), Lerp(scale, LiftScale, p / LiftAt))
            : (0, Lerp(Lift, 0, (p - LiftAt) / (1 - LiftAt)), Lerp(LiftScale, 1, (p - LiftAt) / (1 - LiftAt)));
        return Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y);
    }

    private static (double X, double Y, double Scale, double ShadowY, double Blur, double Alpha) Keyframes(double p)
    {
        var i = 1;
        while (i < Cross.Length - 1 && p > Cross[i].At)
        {
            i++;
        }

        var (a, b) = (Cross[i - 1], Cross[i]);
        var u = (p - a.At) / (b.At - a.At);
        return (Lerp(a.X, b.X, u), Lerp(a.Y, b.Y, u), Lerp(a.Scale, b.Scale, u), Lerp(a.ShadowY, b.ShadowY, u), Lerp(a.Blur, b.Blur, u),
            Lerp(a.Alpha, b.Alpha, u));
    }

    private static double Progress(double t, double start, double length) => Math.Clamp((t - start) / length, 0, 1);

    private static double Lerp(double a, double b, double u) => a + ((b - a) * u);
}
