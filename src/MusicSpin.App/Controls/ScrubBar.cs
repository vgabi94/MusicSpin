using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.Controls;

/// <summary>
/// A thin bar in a taller hit area that sets a value by pointer, wheel and keys (the position bar and the volume bar).
/// It doesn't change <see cref="RangeBase.Value"/> itself: it runs <see cref="CommitCommand"/> with the new value, and
/// shows <see cref="Scrub"/> while dragged.
/// </summary>
/// <remarks>
/// Dragging moves relative to the pointer, <see cref="FineFactor"/> as fast with Shift; pressing with Shift starts from
/// the current value instead of jumping to the pointer. The first <see cref="SnapWidth"/> pixels are exactly the minimum.
/// Keys work on the focused bar and are its own key bindings, so they win over window shortcuts on the same keys:
/// ←/↓ and →/↑ by <see cref="RangeBase.SmallChange"/>, PgDn/PgUp by <see cref="RangeBase.LargeChange"/>, Home to the
/// minimum, End to the maximum when <see cref="EndKey"/>.
/// </remarks>
[PseudoClasses(":dragging")]
public sealed class ScrubBar : RangeBase
{
    /// <summary>The value shown while dragging; null otherwise.</summary>
    public static readonly StyledProperty<double?> ScrubProperty =
        AvaloniaProperty.Register<ScrubBar, double?>(nameof(Scrub), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Runs with the new value (a double).</summary>
    public static readonly StyledProperty<ICommand?> CommitCommandProperty = AvaloniaProperty.Register<ScrubBar, ICommand?>(nameof(CommitCommand));

    /// <summary>Commit on every move of a drag (the volume) rather than on release (the position).</summary>
    public static readonly StyledProperty<bool> CommitsWhileDraggingProperty = AvaloniaProperty.Register<ScrubBar, bool>(nameof(CommitsWhileDragging));

    public static readonly StyledProperty<double> SnapWidthProperty = AvaloniaProperty.Register<ScrubBar, double>(nameof(SnapWidth));

    /// <summary>Values from the pointer are rounded to this (0: not rounded).</summary>
    public static readonly StyledProperty<double> PointerStepProperty = AvaloniaProperty.Register<ScrubBar, double>(nameof(PointerStep));

    /// <summary>Keys and the wheel land on whole multiples of their step (37.5 steps to 38), instead of adding it.</summary>
    public static readonly StyledProperty<bool> StepsToMultiplesProperty = AvaloniaProperty.Register<ScrubBar, bool>(nameof(StepsToMultiples));

    public static readonly StyledProperty<double> FineFactorProperty = AvaloniaProperty.Register<ScrubBar, double>(nameof(FineFactor), 0.1);

    /// <summary>The wheel's step a notch (0: the wheel does nothing).</summary>
    public static readonly StyledProperty<double> WheelStepProperty = AvaloniaProperty.Register<ScrubBar, double>(nameof(WheelStep));

    public static readonly StyledProperty<double> LargeWheelStepProperty = AvaloniaProperty.Register<ScrubBar, double>(nameof(LargeWheelStep));

    public static readonly StyledProperty<bool> EndKeyProperty = AvaloniaProperty.Register<ScrubBar, bool>(nameof(EndKey));

    /// <summary>The value as screen readers read it ("1:23 of 6:46").</summary>
    public static readonly StyledProperty<string?> ValueTextProperty = AvaloniaProperty.Register<ScrubBar, string?>(nameof(ValueText));

    public static readonly DirectProperty<ScrubBar, double> FillWidthProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, double>(nameof(FillWidth), b => b.FillWidth);

    public static readonly DirectProperty<ScrubBar, double?> HoverValueProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, double?>(nameof(HoverValue), b => b.HoverValue);

    public static readonly DirectProperty<ScrubBar, double> TipLeftProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, double>(nameof(TipLeft), b => b.TipLeft);

    private Control? _tip;
    private double _hoverX;
    private double _dragValue;
    private double _dragX;

    static ScrubBar()
    {
        FocusableProperty.OverrideDefaultValue<ScrubBar>(true);
    }

    public ScrubBar()
    {
        Bind(Key.Left, () => StepBy(-SmallChange));
        Bind(Key.Down, () => StepBy(-SmallChange));
        Bind(Key.Right, () => StepBy(SmallChange));
        Bind(Key.Up, () => StepBy(SmallChange));
        Bind(Key.PageDown, () => StepBy(-LargeChange));
        Bind(Key.PageUp, () => StepBy(LargeChange));
        Bind(Key.Home, () => Commit(Minimum));
        Bind(Key.End, () => Commit(Maximum), () => EndKey);
    }

    public double? Scrub
    {
        get => GetValue(ScrubProperty);
        set => SetValue(ScrubProperty, value);
    }

    public ICommand? CommitCommand
    {
        get => GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public bool CommitsWhileDragging
    {
        get => GetValue(CommitsWhileDraggingProperty);
        set => SetValue(CommitsWhileDraggingProperty, value);
    }

    public double SnapWidth
    {
        get => GetValue(SnapWidthProperty);
        set => SetValue(SnapWidthProperty, value);
    }

    public double PointerStep
    {
        get => GetValue(PointerStepProperty);
        set => SetValue(PointerStepProperty, value);
    }

    public bool StepsToMultiples
    {
        get => GetValue(StepsToMultiplesProperty);
        set => SetValue(StepsToMultiplesProperty, value);
    }

    public double FineFactor
    {
        get => GetValue(FineFactorProperty);
        set => SetValue(FineFactorProperty, value);
    }

    public double WheelStep
    {
        get => GetValue(WheelStepProperty);
        set => SetValue(WheelStepProperty, value);
    }

    public double LargeWheelStep
    {
        get => GetValue(LargeWheelStepProperty);
        set => SetValue(LargeWheelStepProperty, value);
    }

    public bool EndKey
    {
        get => GetValue(EndKeyProperty);
        set => SetValue(EndKeyProperty, value);
    }

    public string? ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    /// <summary>The filled part's width: the shown value's share of the bar.</summary>
    public double FillWidth
    {
        get;
        private set => SetAndRaise(FillWidthProperty, ref field, value);
    }

    /// <summary>The value under the pointer while it hovers (not while dragging); the tip shows it.</summary>
    public double? HoverValue
    {
        get;
        private set => SetAndRaise(HoverValueProperty, ref field, value);
    }

    /// <summary>Where the tip goes so it is centred over the pointer.</summary>
    public double TipLeft
    {
        get;
        private set => SetAndRaise(TipLeftProperty, ref field, value);
    }

    public bool IsDragging => PseudoClasses.Contains(":dragging");

    /// <summary>The value the bar shows: <see cref="Scrub"/> while dragging, else <see cref="RangeBase.Value"/>.</summary>
    public double Shown => Scrub ?? Value;

    /// <summary>The value at <paramref name="x"/> pixels from the bar's left edge.</summary>
    public double ValueAt(double x)
    {
        if (x < SnapWidth || Bounds.Width <= 0)
        {
            return Minimum;
        }

        return Round(Minimum + (Math.Clamp(x / Bounds.Width, 0, 1) * (Maximum - Minimum)));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ScrubBarAutomationPeer(this);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        if (_tip is not null)
        {
            _tip.SizeChanged -= OnTipSizeChanged;
        }

        _tip = e.NameScope.Find<Control>("PART_Tip");
        if (_tip is not null)
        {
            _tip.SizeChanged += OnTipSizeChanged;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == ScrubProperty || change.Property == MinimumProperty
            || change.Property == MaximumProperty || change.Property == BoundsProperty)
        {
            var range = Maximum - Minimum;
            FillWidth = range > 0 ? Math.Clamp((Shown - Minimum) / range, 0, 1) * Bounds.Width : 0;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(this);
        Focus(NavigationMethod.Pointer);
        PseudoClasses.Set(":dragging", true);
        HoverValue = null;
        _dragX = e.GetPosition(this).X;
        _dragValue = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Shown : ValueAt(_dragX);
        Drag(_dragValue);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (!IsDragging)
        {
            Hover(x);
            return;
        }

        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _dragValue = !fine && x < SnapWidth
            ? Minimum
            : Math.Clamp(_dragValue + ((x - _dragX) / Math.Max(1, Bounds.Width) * (Maximum - Minimum) * (fine ? FineFactor : 1)), Minimum, Maximum);
        _dragX = x;
        Drag(Round(_dragValue));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (!IsDragging)
        {
            return;
        }

        var value = Scrub ?? Shown;
        EndDrag();
        if (!CommitsWhileDragging)
        {
            Commit(value);
        }

        e.Pointer.Capture(null);
        Hover(e.GetPosition(this).X);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoverValue = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (WheelStep <= 0)
        {
            return;
        }

        // Shift+wheel arrives as a horizontal scroll on Windows.
        var notches = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (notches == 0)
        {
            return;
        }

        StepBy(Math.Sign(notches) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? LargeWheelStep : WheelStep));
        e.Handled = true;
    }

    private void Bind(Key key, Action action, Func<bool>? enabled = null)
        => KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new KeyCommand(action, enabled ?? (() => true)) });

    private void StepBy(double step)
    {
        var from = Shown;
        Commit(StepsToMultiples ? Steps.Next(from, step) : from + step);
    }

    private void Drag(double value)
    {
        Scrub = value;
        if (CommitsWhileDragging)
        {
            Commit(value);
        }
    }

    private void EndDrag()
    {
        if (!IsDragging)
        {
            return;
        }

        PseudoClasses.Set(":dragging", false);
        Scrub = null;
    }

    private void Commit(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (CommitCommand is { } command && command.CanExecute(value))
        {
            command.Execute(value);
        }
    }

    private void Hover(double x)
    {
        _hoverX = Math.Clamp(x, 0, Bounds.Width);
        HoverValue = ValueAt(x);
        PlaceTip();
    }

    private void OnTipSizeChanged(object? sender, SizeChangedEventArgs e) => PlaceTip();

    private void PlaceTip() => TipLeft = _hoverX - ((_tip?.Bounds.Width ?? 0) / 2);

    private double Round(double value)
        => PointerStep > 0 ? Math.Clamp(Math.Round(value / PointerStep) * PointerStep, Minimum, Maximum) : value;

    private sealed class KeyCommand(Action action, Func<bool> enabled) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => enabled();

        public void Execute(object? parameter) => action();
    }
}

/// <summary>A slider for screen readers, with the bar's <see cref="ScrubBar.ValueText"/> as its value.</summary>
public sealed class ScrubBarAutomationPeer(ScrubBar owner) : RangeBaseAutomationPeer(owner), IValueProvider
{
    bool IValueProvider.IsReadOnly => true;

    string? IValueProvider.Value => ((ScrubBar)Owner).ValueText;

    void IValueProvider.SetValue(string? value) => throw new InvalidOperationException("The value is set with the bar, not as text.");

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
}
