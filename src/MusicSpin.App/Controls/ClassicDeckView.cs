using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// The mockup's own deck (mockup: .deck): a plinth, a striped platter, the record, the tonearm on the right with its
/// shadow, an LED bottom left and "Music Spin" engraved bottom right. Styles/NowPlaying.axaml draws it.
/// </summary>
/// <remarks>
/// <see cref="Swing"/> (0: parked, 1: over the record) and <see cref="Drop"/> (0: lifted, 1: lowered) are set here from
/// <see cref="DeckView.Arm"/> as local values and animated by the theme's transitions, so an arm stopped half way turns
/// back from where it is. Lifted, the arm is a little larger (nearer the eye) and its shadow falls further off.
/// </remarks>
[TemplatePart("PART_Tonearm", typeof(Control))]
[TemplatePart("PART_ArmShadow", typeof(Control))]
[TemplatePart("PART_Led", typeof(Shape))]
public sealed class ClassicDeckView : DeckView
{
    /// <summary>The arm's angle on its rest and over the lead-in (mockup: rotate -4deg, 22deg).</summary>
    private const double ParkedAngle = -4;
    private const double LeadInAngle = 22;

    /// <summary>A lifted arm's scale (mockup: scale 1.035).</summary>
    private const double LiftedScale = 1.035;

    /// <summary>The arm shadow's offset and opacity, lifted and lowered (mockup: .arm-shadow translate, opacity).</summary>
    private static readonly Vector LiftedShadow = new(14, 18);
    private static readonly Vector LoweredShadow = new(4, 5);
    private const double LiftedShadowOpacity = 0.5;
    private const double LoweredShadowOpacity = 0.8;

    public static readonly StyledProperty<double> SwingProperty = AvaloniaProperty.Register<ClassicDeckView, double>(nameof(Swing));

    public static readonly StyledProperty<double> DropProperty = AvaloniaProperty.Register<ClassicDeckView, double>(nameof(Drop));

    public static readonly StyledProperty<IBrush?> LedOffBrushProperty = AvaloniaProperty.Register<ClassicDeckView, IBrush?>(nameof(LedOffBrush));

    /// <summary>The lit LED's glow (mockup: box-shadow 0 0 10px var(--accent)).</summary>
    public static readonly StyledProperty<double> LedGlowProperty = AvaloniaProperty.Register<ClassicDeckView, double>(nameof(LedGlow));

    private Control? _tonearm;
    private Control? _shadow;
    private Shape? _led;

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

    /// <summary>The LED while the platter is still.</summary>
    public IBrush? LedOffBrush
    {
        get => GetValue(LedOffBrushProperty);
        set => SetValue(LedOffBrushProperty, value);
    }

    public double LedGlow
    {
        get => GetValue(LedGlowProperty);
        set => SetValue(LedGlowProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        _tonearm = e.NameScope.Find<Control>("PART_Tonearm");
        _shadow = e.NameScope.Find<Control>("PART_ArmShadow");
        _led = e.NameScope.Find<Shape>("PART_Led");
        Place();
        Light();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ArmProperty)
        {
            Swing = Arm == ArmState.Parked ? 0 : 1;
            Drop = Arm == ArmState.Lowered ? 1 : 0;
        }
        else if (change.Property == SwingProperty || change.Property == DropProperty)
        {
            Place();
        }
        else if (change.Property == IsSpinningProperty || change.Property == AccentProperty
                 || change.Property == LedOffBrushProperty || change.Property == LedGlowProperty)
        {
            Light();
        }
    }

    /// <summary>The LED: lit in the accent, glowing, while the platter spins.</summary>
    private void Light()
    {
        if (_led is null)
        {
            return;
        }

        var lit = IsSpinning && Accent is not null;
        _led.Fill = lit ? Accent : LedOffBrush;
        _led.Effect = lit && Accent is ISolidColorBrush accent
            ? new DropShadowEffect { Color = accent.Color, BlurRadius = LedGlow, OffsetX = 0, OffsetY = 0, Opacity = 1 }
            : null;
    }

    private void Place()
    {
        var angle = ParkedAngle + (Swing * (LeadInAngle - ParkedAngle));
        var scale = LiftedScale + (Drop * (1 - LiftedScale));
        _tonearm?.RenderTransform = new MatrixTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateRotation(angle * Math.PI / 180));
        if (_shadow is not null)
        {
            var offset = LiftedShadow + ((LoweredShadow - LiftedShadow) * Drop);
            _shadow.RenderTransform = new TranslateTransform(offset.X, offset.Y);
            _shadow.Opacity = LiftedShadowOpacity + (Drop * (LoweredShadowOpacity - LiftedShadowOpacity));
        }
    }
}
