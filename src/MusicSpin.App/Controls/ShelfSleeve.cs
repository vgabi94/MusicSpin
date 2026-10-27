using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MusicSpin.App.Theming;

namespace MusicSpin.App.Controls;

/// <summary>
/// A sleeve on the shelf (mockup: .slot, .sleeve3d): a flat slot that takes the pointer, and in it the sleeve turned away
/// by <see cref="Tilt"/> about its left edge, seen from its own bottom edge (so it stands flat on the board) with
/// Settings › Shelf depth as the perspective. <see cref="Turn"/> (hovered or playing) turns it to face you and brings it
/// forward, <see cref="Pull"/> widens the slot for the playing record, and <see cref="RecordOut"/> slides the record
/// half out. The styles animate those three; the slot never moves with the 3D turn, so hovering doesn't flicker.
/// </summary>
/// <remarks>
/// <para>
/// A hovered slot widens, so the sleeves after it move along the row while it turns. Drawn through its 3D projection, a
/// sleeve costs a lot (its cover resampled through a perspective matrix); one at rest is drawn through
/// <see cref="RestCache"/>, an image of it made once, so moving it along the row only copies that image (#131). A
/// sleeve that turns or whose record comes out draws itself live until it is at rest again. The record isn't drawn at
/// all while it is in its sleeve, out of sight.
/// </para>
/// <para>
/// Template parts: PART_Vinyl (the record, behind the sleeve), PART_Sleeve (the cover), PART_Edge (the spine, a thin
/// face at the left edge) and PART_Shadow (the contact shadow on the board). The parts take no input.
/// </para>
/// <para>
/// Hover intent (#147, in place of the mockup's .slot:hover transition-delay 60ms): a closed sleeve turns only once the
/// pointer has rested on it for <see cref="HoverIntent"/>, timed on <see cref="Clock"/>, so a pointer swept along the row
/// turns none of the sleeves it crosses, and one left sooner never moves. A sleeve the pointer reaches from one it opened,
/// while that one is still open or closing, turns at once: the one you leave and the one you enter move in step, and the
/// slots between them keep their places. The wait is not the transition's delay: a transition left during its delay
/// starts back from the value it was going to, so the sleeve would open all the way at once and then close.
/// </para>
/// <para>
/// The control sets Turn, Pull and RecordOut itself, from <see cref="InputElement.IsPointerOver"/> and
/// <see cref="IsPlaying"/>, rather than leaving them to :pointerover and .playing styles: when a style stops applying while
/// its property is still in a transition, Avalonia carries the transition on to the old value and only then drops it, so a
/// sleeve the pointer left mid-turn would turn all the way open and then snap shut. A local value turns it back from where
/// it is.
/// </para>
/// </remarks>
[TemplatePart("PART_Sleeve", typeof(Control))]
[TemplatePart("PART_Vinyl", typeof(Control))]
[TemplatePart("PART_Edge", typeof(Control))]
[TemplatePart("PART_Shadow", typeof(Control))]
public sealed class ShelfSleeve : Button
{
    /// <summary>The sleeve the pointer last turned: while it is open, or still closing, the next one turns without waiting.</summary>
    private static readonly WeakReference<ShelfSleeve> s_hovered = new(null!);

    /// <summary>The hover intent's wait, while the pointer rests on a closed sleeve.</summary>
    private ITimer? _waiting;

    /// <summary>Counts cancelled waits.</summary>
    private int _waits;

    /// <summary>A facing sleeve keeps this share of the tilt (mockup: rotateY(tilt × .12)).</summary>
    private const double FacingTilt = 0.12;

    /// <summary>A facing sleeve comes this far forward (mockup: translateZ(36px)).</summary>
    private const double FacingLift = 36;

    /// <summary>How far the record comes out, as a share of its width (mockup: translateX(52%)).</summary>
    private const double RecordPull = 0.52;

    /// <summary>The record sits just behind the sleeve (mockup: translateZ(-2px)).</summary>
    private const double VinylBehind = -2;

    /// <summary>The contact shadow reaches this far below the sleeve, laid back this far (mockup: bottom -10px, rotateX(80deg)).</summary>
    private const double ShadowBelow = 10;
    private const double ShadowLie = 80;

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(Size), 170);

    public static readonly StyledProperty<double> TiltProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(Tilt), 58);

    public static readonly StyledProperty<double> DepthProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(Depth), 40);

    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<ShelfSleeve, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<double> TurnProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(Turn));

    public static readonly StyledProperty<double> PullProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(Pull));

    public static readonly StyledProperty<double> RecordOutProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(RecordOut));

    public static readonly StyledProperty<double> EdgeWidthProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(EdgeWidth), 6);

    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<ShelfSleeve, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ShelfSleeve, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ArtistProperty = AvaloniaProperty.Register<ShelfSleeve, string?>(nameof(Artist));

    public static readonly StyledProperty<double> DiscAngleProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(DiscAngle));

    /// <summary>The album's colour, for the spine; null: <see cref="SpineFallback"/>.</summary>
    public static readonly StyledProperty<Color?> SpineColorProperty = AvaloniaProperty.Register<ShelfSleeve, Color?>(nameof(SpineColor));

    public static readonly StyledProperty<IBrush?> SpineFallbackProperty = AvaloniaProperty.Register<ShelfSleeve, IBrush?>(nameof(SpineFallback));

    /// <summary>The spine is in shadow: its colour times this (mockup: .edge filter: brightness(.55)).</summary>
    public static readonly StyledProperty<double> SpineShadeProperty = AvaloniaProperty.Register<ShelfSleeve, double>(nameof(SpineShade), 1);

    /// <summary>
    /// How long the pointer rests on a closed sleeve before it turns (Styles/Motion.axaml: ShelfHoverIntent, following the
    /// Motion setting); zero turns it at once.
    /// </summary>
    public static readonly StyledProperty<TimeSpan> HoverIntentProperty = AvaloniaProperty.Register<ShelfSleeve, TimeSpan>(nameof(HoverIntent));

    /// <summary>The app's clock, which <see cref="HoverIntent"/> is timed on (null: the system's).</summary>
    public static readonly StyledProperty<TimeProvider?> ClockProperty = AvaloniaProperty.Register<ShelfSleeve, TimeProvider?>(nameof(Clock));

    /// <summary>The cache a sleeve at rest draws through (Styles/Shelf.axaml); null: always drawn live.</summary>
    public static readonly StyledProperty<CacheMode?> RestCacheProperty = AvaloniaProperty.Register<ShelfSleeve, CacheMode?>(nameof(RestCache));

    public static readonly DirectProperty<ShelfSleeve, IBrush?> SpineProperty =
        AvaloniaProperty.RegisterDirect<ShelfSleeve, IBrush?>(nameof(Spine), s => s.Spine);

    private Control? _sleeve;
    private Control? _vinyl;
    private Control? _edge;
    private Control? _shadow;

    static ShelfSleeve()
    {
        AffectsMeasure<ShelfSleeve>(SizeProperty, TiltProperty, TurnProperty, PullProperty);
        AffectsArrange<ShelfSleeve>(DepthProperty, RecordOutProperty, EdgeWidthProperty);
    }

    public ShelfSleeve()
    {
        // An image holds its text in grey antialiasing (subpixel text needs an opaque background): the text on a sleeve
        // (a cover without art) is drawn that way live too, so it doesn't change as the sleeve starts or stops turning.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Settings › Shelf tilt, in degrees.</summary>
    public double Tilt
    {
        get => GetValue(TiltProperty);
        set => SetValue(TiltProperty, value);
    }

    /// <summary>Settings › Shelf depth (0 is flat).</summary>
    public double Depth
    {
        get => GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    /// <summary>The playing record's sleeve: it faces you, its slot is wider and its record is half out.</summary>
    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>0: turned away; 1: facing you (hovered or playing).</summary>
    public double Turn
    {
        get => GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    /// <summary>0..1: the playing slot's extra width.</summary>
    public double Pull
    {
        get => GetValue(PullProperty);
        set => SetValue(PullProperty, value);
    }

    /// <summary>0..1: the record out of its sleeve (half out at 1).</summary>
    public double RecordOut
    {
        get => GetValue(RecordOutProperty);
        set => SetValue(RecordOutProperty, value);
    }

    /// <summary>The sleeve's thickness: the spine's width.</summary>
    public double EdgeWidth
    {
        get => GetValue(EdgeWidthProperty);
        set => SetValue(EdgeWidthProperty, value);
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

    public double DiscAngle
    {
        get => GetValue(DiscAngleProperty);
        set => SetValue(DiscAngleProperty, value);
    }

    public Color? SpineColor
    {
        get => GetValue(SpineColorProperty);
        set => SetValue(SpineColorProperty, value);
    }

    public IBrush? SpineFallback
    {
        get => GetValue(SpineFallbackProperty);
        set => SetValue(SpineFallbackProperty, value);
    }

    public double SpineShade
    {
        get => GetValue(SpineShadeProperty);
        set => SetValue(SpineShadeProperty, value);
    }

    public CacheMode? RestCache
    {
        get => GetValue(RestCacheProperty);
        set => SetValue(RestCacheProperty, value);
    }

    public TimeSpan HoverIntent
    {
        get => GetValue(HoverIntentProperty);
        set => SetValue(HoverIntentProperty, value);
    }

    public TimeProvider? Clock
    {
        get => GetValue(ClockProperty);
        set => SetValue(ClockProperty, value);
    }

    /// <summary>
    /// How near its end a turn or the record's slide counts as there: an animator that settles a hair short of its target
    /// (0.9999999999) doesn't leave the sleeve drawn live for good.
    /// </summary>
    public const double RestTolerance = 1e-6;

    /// <summary>Still (closed or facing you, its record in): drawn through <see cref="RestCache"/>.</summary>
    public bool IsAtRest => (Math.Abs(Turn) < RestTolerance || Math.Abs(1 - Turn) < RestTolerance) && !IsRecordOut;

    /// <summary>The record is (partly) out of its sleeve: it is drawn.</summary>
    private bool IsRecordOut => RecordOut >= RestTolerance;

    /// <summary>The spine's brush: the album's colour in shade, or the fallback.</summary>
    public IBrush? Spine
    {
        get;
        private set => SetAndRaise(SpineProperty, ref field, value);
    }

    /// <summary>The sleeve's projection now: where its corners land in the slot (tests read it).</summary>
    public Projection SleeveProjection { get; private set; } = Projection.Identity;

    /// <summary>The record's projection now (tests read it).</summary>
    public Projection VinylProjection { get; private set; } = Projection.Identity;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        _sleeve = e.NameScope.Find<Control>("PART_Sleeve");
        _vinyl = e.NameScope.Find<Control>("PART_Vinyl");
        _edge = e.NameScope.Find<Control>("PART_Edge");
        _shadow = e.NameScope.Find<Control>("PART_Shadow");
        foreach (var part in new[] { _sleeve, _vinyl, _edge, _shadow })
        {
            part?.RenderTransformOrigin = RelativePoint.TopLeft;
            part?.IsHitTestVisible = false;
        }

        UpdateRest();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty || change.Property == IsPlayingProperty)
        {
            StopWaiting();
            if (IsPointerOver && !IsPlaying && Turn < RestTolerance && !FromAnOpenSleeve() && HoverIntent > TimeSpan.Zero)
            {
                Wait(); // closed and the row still: it turns once the pointer has rested on it
            }
            else
            {
                TurnTo(IsPointerOver || IsPlaying);
            }

            Pull = RecordOut = IsPlaying ? 1 : 0;
        }
        else if (change.Property == TurnProperty || change.Property == RecordOutProperty || change.Property == RestCacheProperty)
        {
            UpdateRest();
        }
        else if (change.Property == SpineColorProperty || change.Property == SpineFallbackProperty || change.Property == SpineShadeProperty)
        {
            Spine = SpineColor is { } color
                ? new SolidColorBrush(Color.FromRgb(Shade(color.R), Shade(color.G), Shade(color.B)))
                : SpineFallback;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopWaiting();
    }

    /// <summary>Turns once the pointer has rested on the sleeve for <see cref="HoverIntent"/>.</summary>
    private void Wait()
    {
        var wait = _waits;
        _waiting = (Clock ?? TimeProvider.System).CreateTimer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (wait == _waits)
            {
                StopWaiting();
                TurnTo(IsPointerOver || IsPlaying);
            }
        }), null, HoverIntent, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Cancels a wait: a tick of its timer already queued does nothing.</summary>
    private void StopWaiting()
    {
        _waits++;
        _waiting?.Dispose();
        _waiting = null;
    }

    private void TurnTo(bool open)
    {
        if (open && !IsPlaying)
        {
            s_hovered.SetTarget(this);
        }

        Turn = open ? 1 : 0;
    }

    /// <summary>The pointer comes from a sleeve it turned that is still open, or closing, in this window.</summary>
    private bool FromAnOpenSleeve()
        => s_hovered.TryGetTarget(out var open) && open != this && !open.IsPlaying && open.Turn >= RestTolerance
           && open.VisualRoot is { } root && root == VisualRoot;

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(new Size(Size, Size));
        return new Size(ShelfGeometry.SlotWidth(Size, Tilt, Turn, Pull), Size);
    }

    /// <summary>The parts keep their own sizes at the slot's top left; their transforms put them in place.</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateProjection(finalSize.Height - Size);
        return arranged;
    }

    /// <summary>At rest, the sleeve draws through its cache; its record shows only when it is out of the sleeve.</summary>
    private void UpdateRest()
    {
        CacheMode = IsAtRest ? RestCache : null;
        _vinyl?.IsVisible = IsRecordOut;
    }

    private byte Shade(byte channel) => (byte)Math.Clamp(Math.Round(channel * SpineShade), 0, 255);

    /// <summary>
    /// The mockup's chain: perspective seen from the sleeve's bottom centre, then rotateY(tilt) translateZ(lift) about its
    /// left edge's middle. In the sleeve's plane: the record just behind it, centred, sliding out to the right; the spine,
    /// a face turned 90° at the left edge; and the contact shadow, laid nearly flat (rotateX(80°)) across the bottom edge.
    /// </summary>
    private void UpdateProjection(double top)
    {
        var size = Size;
        var angle = Tilt + (Turn * ((Tilt * FacingTilt) - Tilt));
        var turn = Projection.RotateY(angle) * Projection.Translate(0, 0, Turn * FacingLift);
        var sleeve = Projection.Perspective(ShelfGeometry.Perspective(Depth), new Point(size / 2, size))
                     * Projection.About(new Point(0, size / 2), turn);
        SleeveProjection = sleeve;
        var shift = Matrix.CreateTranslation(0, top);
        _sleeve?.RenderTransform = new MatrixTransform(sleeve.ToMatrix() * shift);
        _edge?.RenderTransform = new MatrixTransform((sleeve * Projection.RotateY(90)).ToMatrix() * shift);
        if (_vinyl is not null)
        {
            var record = _vinyl.Bounds.Width;
            var inset = (size - record) / 2;
            VinylProjection = sleeve * Projection.Translate(inset + (RecordOut * RecordPull * record), inset, VinylBehind);
            _vinyl.RenderTransform = new MatrixTransform(VinylProjection.ToMatrix() * shift);
        }

        if (_shadow is not null)
        {
            var height = _shadow.Bounds.Height;
            var shadow = sleeve * Projection.Translate(0, size + ShadowBelow - height, 0)
                                * Projection.About(new Point(size / 2, height / 2), Projection.RotateX(ShadowLie));
            _shadow.RenderTransform = new MatrixTransform(shadow.ToMatrix() * shift);
        }
    }
}
