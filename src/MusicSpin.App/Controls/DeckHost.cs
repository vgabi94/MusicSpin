using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Reactive;

namespace MusicSpin.App.Controls;

/// <summary>
/// Holds the turntable design chosen in Settings (<see cref="Design"/>): it builds the design's <see cref="DeckView"/>
/// from the data templates in scope (a design is a <c>DeckDesign</c> record; its template is its deck), passes it the
/// record, the arm, the spin, the empty platter, the accent, the speed and what is behind it, and scales it to the window. Changing the design
/// swaps the deck at once.
/// </summary>
/// <remarks>
/// The mockup sizes the deck by the window: <c>--s: min(46vh, 34vw)</c> is the platter's diameter. Designs draw at
/// <see cref="DeckView.BaseSize"/>; the host scales them to <c>min(<see cref="HeightShare"/> × window height,
/// <see cref="WidthShare"/> × window width)</c>.
/// </remarks>
public sealed class DeckHost : Control
{
    public static readonly StyledProperty<object?> DesignProperty = AvaloniaProperty.Register<DeckHost, object?>(nameof(Design));

    public static readonly StyledProperty<IImage?> LabelProperty = DeckView.LabelProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<double> AngleProperty = DeckView.AngleProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<ArmState> ArmProperty = DeckView.ArmProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<bool> IsSpinningProperty = DeckView.IsSpinningProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<bool> IsEmptyProperty = DeckView.IsEmptyProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<IBrush?> AccentProperty = DeckView.AccentProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<double> SpeedProperty = DeckView.SpeedProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<Image?> BackdropProperty = DeckView.BackdropProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<Border?> BackdropVeilProperty = DeckView.BackdropVeilProperty.AddOwner<DeckHost>();

    public static readonly StyledProperty<double> HeightShareProperty = AvaloniaProperty.Register<DeckHost, double>(nameof(HeightShare), 0.46);

    public static readonly StyledProperty<double> WidthShareProperty = AvaloniaProperty.Register<DeckHost, double>(nameof(WidthShare), 0.34);

    public static readonly DirectProperty<DeckHost, DeckView?> DeckProperty =
        AvaloniaProperty.RegisterDirect<DeckHost, DeckView?>(nameof(Deck), h => h.Deck);

    private readonly LayoutTransformControl _scaler = new() { ClipToBounds = false };
    private TopLevel? _topLevel;
    private IDisposable? _windowSize;

    public DeckHost()
    {
        VisualChildren.Add(_scaler);
        LogicalChildren.Add(_scaler);
    }

    /// <summary>The design (a <c>DeckDesign</c>); the data template for its type builds its deck.</summary>
    public object? Design
    {
        get => GetValue(DesignProperty);
        set => SetValue(DesignProperty, value);
    }

    public IImage? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

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

    public bool IsSpinning
    {
        get => GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    public bool IsEmpty
    {
        get => GetValue(IsEmptyProperty);
        set => SetValue(IsEmptyProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public Image? Backdrop
    {
        get => GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    public Border? BackdropVeil
    {
        get => GetValue(BackdropVeilProperty);
        set => SetValue(BackdropVeilProperty, value);
    }

    /// <summary>The platter's diameter as a share of the window's height (mockup: 46vh).</summary>
    public double HeightShare
    {
        get => GetValue(HeightShareProperty);
        set => SetValue(HeightShareProperty, value);
    }

    /// <summary>The platter's diameter as a share of the window's width (mockup: 34vw).</summary>
    public double WidthShare
    {
        get => GetValue(WidthShareProperty);
        set => SetValue(WidthShareProperty, value);
    }

    /// <summary>The design's deck now.</summary>
    public DeckView? Deck
    {
        get;
        private set => SetAndRaise(DeckProperty, ref field, value);
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Build();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _windowSize = _topLevel?.GetObservable(TopLevel.ClientSizeProperty).Subscribe(new AnonymousObserver<Size>(_ => Scale()));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _windowSize?.Dispose();
        _windowSize = null;
        _topLevel = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == DesignProperty)
        {
            Build();
        }
        else if (change.Property == HeightShareProperty || change.Property == WidthShareProperty)
        {
            Scale();
        }
    }

    private void Build()
    {
        if (!((ILogical)this).IsAttachedToLogicalTree)
        {
            return;
        }

        var deck = Design is { } design ? this.FindDataTemplate(design)?.Build(design) as DeckView : null;
        if (deck is not null)
        {
            deck.DataContext = Design;
            deck.Bind(DeckView.LabelProperty, this.GetObservable(LabelProperty));
            deck.Bind(DeckView.AngleProperty, this.GetObservable(AngleProperty));
            deck.Bind(DeckView.ArmProperty, this.GetObservable(ArmProperty));
            deck.Bind(DeckView.IsSpinningProperty, this.GetObservable(IsSpinningProperty));
            deck.Bind(DeckView.IsEmptyProperty, this.GetObservable(IsEmptyProperty));
            deck.Bind(DeckView.AccentProperty, this.GetObservable(AccentProperty));
            deck.Bind(DeckView.SpeedProperty, this.GetObservable(SpeedProperty));
            deck.Bind(DeckView.BackdropProperty, this.GetObservable(BackdropProperty));
            deck.Bind(DeckView.BackdropVeilProperty, this.GetObservable(BackdropVeilProperty));
        }

        _scaler.Child = deck;
        Deck = deck;
    }

    private void Scale()
    {
        if (_topLevel is not { } top)
        {
            return;
        }

        var size = Math.Min(top.ClientSize.Height * HeightShare, top.ClientSize.Width * WidthShare);
        var scale = size / DeckView.BaseSize;
        _scaler.LayoutTransform = new ScaleTransform(scale, scale);
    }
}
