using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// A turntable drawn on the design studies' shared bench (docs/design/mockup/decks/kit.css): the deck is 1320 × 1080 deck
/// units, the platter 1000 wide at (40, 40), the record 980 wide at (50, 50), and the arm's drawing spans -300 -300 600 1200
/// arm units around its pivot, the stylus at (0, 740). Each design is a control theme (Styles/Decks.axaml) that draws its
/// plinth, platter and arm in those units, scaled to <see cref="DeckView.BaseSize"/>.
/// </summary>
/// <remarks>
/// <para>
/// The arm swings about its pivot from <see cref="RestAngle"/> (parked) to <see cref="PlayAngle"/> (over the lead-in), or,
/// with a <see cref="CarriageTravel"/>, slides that far in along its rails instead (Linear Tracking). Lifted, it is a
/// little larger (nearer the eye) and its shadow falls further off. <see cref="Swing"/> and <see cref="Drop"/> are set
/// from <see cref="DeckView.Arm"/> and animated by the theme's transitions, as the Classic deck's are.
/// </para>
/// <para>
/// The lights are the theme's: it styles <c>:spinning</c> (the platter turns), <c>:armOn</c> (the arm is over the record)
/// and <c>:armDown</c> (the stylus is in the groove), as the studies style <c>.spinning</c>, <c>.arm-on</c> and
/// <c>.arm-down</c>.
/// </para>
/// </remarks>
[TemplatePart("PART_Arm", typeof(Control))]
[TemplatePart("PART_ArmDrawing", typeof(Control))]
[TemplatePart("PART_Stylus", typeof(Control))]
[PseudoClasses(":spinning", ":armOn", ":armDown")]
public sealed class KitDeckView : DeckView
{
    /// <summary>A lifted arm's scale (kit: .arm scale 1.035).</summary>
    public const double LiftedScale = 1.035;

    /// <summary>The arm's shadow, lifted and lowered, in arm units (kit: .arm > svg drop-shadow).</summary>
    public static readonly Vector LiftedShadowOffset = new(24, 30);
    public static readonly Vector LoweredShadowOffset = new(6, 8);
    public const double LiftedShadowBlur = 7;
    public const double LoweredShadowBlur = 3;
    public const double LiftedShadowOpacity = 0.55;
    public const double LoweredShadowOpacity = 0.72;

    public static readonly StyledProperty<double> RestAngleProperty = AvaloniaProperty.Register<KitDeckView, double>(nameof(RestAngle));

    public static readonly StyledProperty<double> PlayAngleProperty = AvaloniaProperty.Register<KitDeckView, double>(nameof(PlayAngle));

    public static readonly StyledProperty<double> CarriageTravelProperty = AvaloniaProperty.Register<KitDeckView, double>(nameof(CarriageTravel));

    public static readonly StyledProperty<double> SwingProperty = AvaloniaProperty.Register<KitDeckView, double>(nameof(Swing));

    public static readonly StyledProperty<double> DropProperty = AvaloniaProperty.Register<KitDeckView, double>(nameof(Drop));

    public static readonly DirectProperty<KitDeckView, string> SpeedTextProperty =
        AvaloniaProperty.RegisterDirect<KitDeckView, string>(nameof(SpeedText), d => d.SpeedText);

    private Control? _arm;
    private Control? _drawing;

    /// <summary>The arm's angle on its rest, in degrees clockwise.</summary>
    public double RestAngle
    {
        get => GetValue(RestAngleProperty);
        set => SetValue(RestAngleProperty, value);
    }

    /// <summary>The arm's angle over the lead-in.</summary>
    public double PlayAngle
    {
        get => GetValue(PlayAngleProperty);
        set => SetValue(PlayAngleProperty, value);
    }

    /// <summary>How far a carriage slides in along its rails, in deck units (0: the arm swings instead).</summary>
    public double CarriageTravel
    {
        get => GetValue(CarriageTravelProperty);
        set => SetValue(CarriageTravelProperty, value);
    }

    /// <summary>0: the arm on its rest; 1: over the lead-in.</summary>
    public double Swing
    {
        get => GetValue(SwingProperty);
        set => SetValue(SwingProperty, value);
    }

    /// <summary>0: lifted; 1: lowered onto the record.</summary>
    public double Drop
    {
        get => GetValue(DropProperty);
        set => SetValue(DropProperty, value);
    }

    /// <summary>The speed as a readout shows it: 33.3, 45 or 16.7.</summary>
    public string SpeedText
    {
        get;
        private set => SetAndRaise(SpeedTextProperty, ref field, value);
    } = Format(33.333);

    /// <summary>The arm (or the carriage), turned or moved about its pivot.</summary>
    public Control? ArmPart => _arm;

    /// <summary>A point at the stylus tip, carried with the arm (tests follow it onto the record).</summary>
    public Control? Stylus { get; private set; }

    /// <summary>The arm's transform for <paramref name="swing"/> and <paramref name="drop"/>, about its pivot.</summary>
    public Matrix ArmTransform(double swing, double drop)
    {
        var scale = LiftedScale + (drop * (1 - LiftedScale));
        var move = CarriageTravel != 0
            ? Matrix.CreateTranslation(-CarriageTravel * swing, 0)
            : Matrix.CreateRotation((RestAngle + (swing * (PlayAngle - RestAngle))) * Math.PI / 180);
        return Matrix.CreateScale(scale, scale) * move;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        _arm = e.NameScope.Find<Control>("PART_Arm");
        _drawing = e.NameScope.Find<Control>("PART_ArmDrawing");
        Stylus = e.NameScope.Find<Control>("PART_Stylus");
        Place();
        Mark();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ArmProperty)
        {
            Swing = Arm == ArmState.Parked ? 0 : 1;
            Drop = Arm == ArmState.Lowered ? 1 : 0;
            Mark();
        }
        else if (change.Property == SwingProperty || change.Property == DropProperty || change.Property == RestAngleProperty
                 || change.Property == PlayAngleProperty || change.Property == CarriageTravelProperty)
        {
            Place();
        }
        else if (change.Property == IsSpinningProperty)
        {
            Mark();
        }
        else if (change.Property == SpeedProperty)
        {
            SpeedText = Format(Speed);
        }
    }

    private static string Format(double rpm) => rpm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private void Mark()
    {
        PseudoClasses.Set(":spinning", IsSpinning);
        PseudoClasses.Set(":armOn", Arm != ArmState.Parked);
        PseudoClasses.Set(":armDown", Arm == ArmState.Lowered);
    }

    private void Place()
    {
        _arm?.RenderTransform = new MatrixTransform(ArmTransform(Swing, Drop));
        if (_drawing is not null)
        {
            var offset = LiftedShadowOffset + ((LoweredShadowOffset - LiftedShadowOffset) * Drop);
            _drawing.Effect = new DropShadowEffect
            {
                OffsetX = offset.X,
                OffsetY = offset.Y,
                BlurRadius = LiftedShadowBlur + (Drop * (LoweredShadowBlur - LiftedShadowBlur)),
                Color = Colors.Black,
                Opacity = LiftedShadowOpacity + (Drop * (LoweredShadowOpacity - LiftedShadowOpacity)),
            };
        }
    }
}
