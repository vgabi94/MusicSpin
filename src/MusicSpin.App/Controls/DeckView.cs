using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>Where the tonearm is (mockup: .deck, .arm-on, .arm-down).</summary>
public enum ArmState
{
    /// <summary>On its rest, off the record.</summary>
    Parked,

    /// <summary>Swung over the lead-in groove, lifted (also where a pause leaves it).</summary>
    OverLeadIn,

    /// <summary>Lowered onto the record: playing.</summary>
    Lowered,
}

/// <summary>
/// A turntable design for Now Playing (Settings › Turntable › Design). <see cref="DeckHost"/> gives every design the same
/// things: the record (<see cref="Label"/>, turned by <see cref="Angle"/> from the spin clock), the arm's state, whether
/// the platter spins, the accent colour; the theme reaches it as every control's. A design draws them its own way, and
/// animates its own arm.
/// </summary>
/// <remarks>
/// A design draws at <see cref="BaseSize"/> (the mockup's <c>--s</c>, the platter's diameter, is 400 there); the host
/// scales it to the window. PART_Record is the record: where a record lands on the deck.
/// </remarks>
[TemplatePart("PART_Record", typeof(Control))]
public abstract class DeckView : TemplatedControl
{
    /// <summary>The platter's diameter a design is drawn for (the host scales it).</summary>
    public const double BaseSize = 400;

    public static readonly StyledProperty<IImage?> LabelProperty = AvaloniaProperty.Register<DeckView, IImage?>(nameof(Label));

    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<DeckView, double>(nameof(Angle));

    public static readonly StyledProperty<ArmState> ArmProperty = AvaloniaProperty.Register<DeckView, ArmState>(nameof(Arm));

    public static readonly StyledProperty<bool> IsSpinningProperty = AvaloniaProperty.Register<DeckView, bool>(nameof(IsSpinning));

    public static readonly StyledProperty<bool> IsEmptyProperty = AvaloniaProperty.Register<DeckView, bool>(nameof(IsEmpty));

    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<DeckView, IBrush?>(nameof(Accent));

    public static readonly StyledProperty<double> SpeedProperty = AvaloniaProperty.Register<DeckView, double>(nameof(Speed), 33.333);

    public static readonly StyledProperty<Image?> BackdropProperty = AvaloniaProperty.Register<DeckView, Image?>(nameof(Backdrop));

    public static readonly StyledProperty<Border?> BackdropVeilProperty = AvaloniaProperty.Register<DeckView, Border?>(nameof(BackdropVeil));

    public static readonly StyledProperty<double> UnitScaleProperty = AvaloniaProperty.Register<DeckView, double>(nameof(UnitScale), 1);

    /// <summary>The record's label: the album cover.</summary>
    public IImage? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The record's rotation in degrees.</summary>
    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public ArmState Arm
    {
        get => GetValue(ArmProperty);
        set => SetValue(ArmProperty, value);
    }

    /// <summary>The platter turns (a deck lights its LED).</summary>
    public bool IsSpinning
    {
        get => GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    /// <summary>No record on the platter (while one travels onto it).</summary>
    public bool IsEmpty
    {
        get => GetValue(IsEmptyProperty);
        set => SetValue(IsEmptyProperty, value);
    }

    /// <summary>The playing album's accent colour.</summary>
    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>The speed chosen in Settings, in rpm (a deck with a readout shows it).</summary>
    public double Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    /// <summary>What is behind the deck (Now Playing's blurred cover): a see-through deck shows it through its plinth.</summary>
    public Image? Backdrop
    {
        get => GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    /// <summary>What lies over <see cref="Backdrop"/> (Now Playing's veil), drawn through a see-through deck too.</summary>
    public Border? BackdropVeil
    {
        get => GetValue(BackdropVeilProperty);
        set => SetValue(BackdropVeilProperty, value);
    }

    /// <summary>
    /// How big one unit of the design's drawing is on the deck: 1 for a design drawn in the deck's own units (Classic),
    /// less for one drawn bigger and scaled down (the kit decks).
    /// </summary>
    public double UnitScale
    {
        get => GetValue(UnitScaleProperty);
        set => SetValue(UnitScaleProperty, value);
    }

    /// <summary>The record on the platter (PART_Record): where a record lands.</summary>
    public Control? Record { get; private set; }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        Record = e.NameScope.Find<Control>("PART_Record");
    }
}
