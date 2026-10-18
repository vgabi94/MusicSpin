using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MusicSpin.App.Theming;

namespace MusicSpin.App.Controls;

/// <summary>
/// The radio icon (a dot and two pairs of waves) drawn in <see cref="Foreground"/> as lines, fitted like
/// <see cref="Icon"/>. While <see cref="IsOnAir"/>, the waves pulse (mockup: @keyframes onair, the outer pair a little
/// after the inner), timed by <see cref="PulseDuration"/> and <see cref="FarDelay"/> scaled by the Motion setting. At
/// Motion None they stay still.
/// </summary>
#pragma warning disable CA1001 // controls aren't disposed: the pulse's source is cancelled and disposed when the icon leaves the tree
public sealed class RadioIcon : Control
#pragma warning restore CA1001
{
    public static readonly StyledProperty<Geometry?> DotProperty = AvaloniaProperty.Register<RadioIcon, Geometry?>(nameof(Dot));

    public static readonly StyledProperty<Geometry?> NearWavesProperty = AvaloniaProperty.Register<RadioIcon, Geometry?>(nameof(NearWaves));

    public static readonly StyledProperty<Geometry?> FarWavesProperty = AvaloniaProperty.Register<RadioIcon, Geometry?>(nameof(FarWaves));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<RadioIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<RadioIcon, double>(nameof(StrokeThickness), 1.5);

    /// <summary>The area of the paths' coordinate space to fit (null: the box of all the parts, as one stroked Icon fits).</summary>
    public static readonly StyledProperty<Rect?> ViewBoxProperty = AvaloniaProperty.Register<RadioIcon, Rect?>(nameof(ViewBox));

    public static readonly StyledProperty<bool> IsOnAirProperty = AvaloniaProperty.Register<RadioIcon, bool>(nameof(IsOnAir));

    /// <summary>The inner waves' opacity (the pulse animates it).</summary>
    public static readonly StyledProperty<double> NearOpacityProperty = AvaloniaProperty.Register<RadioIcon, double>(nameof(NearOpacity), 1);

    /// <summary>The outer waves' opacity.</summary>
    public static readonly StyledProperty<double> FarOpacityProperty = AvaloniaProperty.Register<RadioIcon, double>(nameof(FarOpacity), 1);

    /// <summary>How low the waves fade in a pulse (from a style).</summary>
    public static readonly StyledProperty<double> DimOpacityProperty = AvaloniaProperty.Register<RadioIcon, double>(nameof(DimOpacity), 0.25);

    /// <summary>The pulse's resources in Styles/Motion.axaml: one pulse, the outer waves' delay, the easing.</summary>
    public const string PulseDuration = "OnAirPulseDuration";

    public const string FarDelay = "OnAirFarDelay";

    public const string PulseEasing = "EaseInOut";

    private CancellationTokenSource? _pulse;

    static RadioIcon()
    {
        AffectsRender<RadioIcon>(DotProperty, NearWavesProperty, FarWavesProperty, ForegroundProperty, StrokeThicknessProperty, ViewBoxProperty,
            NearOpacityProperty, FarOpacityProperty);
        IsOnAirProperty.Changed.AddClassHandler<RadioIcon>((icon, _) => icon.UpdatePulse());
    }

    public Geometry? Dot
    {
        get => GetValue(DotProperty);
        set => SetValue(DotProperty, value);
    }

    public Geometry? NearWaves
    {
        get => GetValue(NearWavesProperty);
        set => SetValue(NearWavesProperty, value);
    }

    public Geometry? FarWaves
    {
        get => GetValue(FarWavesProperty);
        set => SetValue(FarWavesProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Line width in pixels (not scaled with the icon).</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Rect? ViewBox
    {
        get => GetValue(ViewBoxProperty);
        set => SetValue(ViewBoxProperty, value);
    }

    /// <summary>A station is on air: the waves pulse.</summary>
    public bool IsOnAir
    {
        get => GetValue(IsOnAirProperty);
        set => SetValue(IsOnAirProperty, value);
    }

    public double NearOpacity
    {
        get => GetValue(NearOpacityProperty);
        set => SetValue(NearOpacityProperty, value);
    }

    public double FarOpacity
    {
        get => GetValue(FarOpacityProperty);
        set => SetValue(FarOpacityProperty, value);
    }

    public double DimOpacity
    {
        get => GetValue(DimOpacityProperty);
        set => SetValue(DimOpacityProperty, value);
    }

    /// <summary>The waves are pulsing now (tests read it).</summary>
    public bool IsPulsing => _pulse is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionScale.Current.PropertyChanged += OnMotionChanged;
        UpdatePulse();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MotionScale.Current.PropertyChanged -= OnMotionChanged;
        StopPulse();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parts = new[] { (Dot, 1.0), (NearWaves, NearOpacity), (FarWaves, FarOpacity) }.Where(p => p.Item1 is not null).ToList();
        if (parts.Count == 0 || Foreground is not { } brush || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // As Icon draws one stroked path: the parts share the box of all of them, with half a line around it.
        var bounds = ViewBox ?? parts.Select(p => p.Item1!.Bounds).Aggregate((a, b) => a.Union(b));
        var room = new Size(Math.Max(0, Bounds.Width - StrokeThickness), Math.Max(0, Bounds.Height - StrokeThickness));
        var scale = bounds.Width > 0 && bounds.Height > 0 ? Math.Min(room.Width / bounds.Width, room.Height / bounds.Height) : 0;
        if (scale <= 0)
        {
            return;
        }

        var offset = new Vector(
            ((Bounds.Width - (bounds.Width * scale)) / 2) - (bounds.X * scale),
            ((Bounds.Height - (bounds.Height * scale)) / 2) - (bounds.Y * scale));
        var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            foreach (var (geometry, opacity) in parts)
            {
                using (context.PushOpacity(opacity))
                {
                    context.DrawGeometry(null, pen, geometry!);
                }
            }
        }
    }

    private void OnMotionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A new Motion setting: the pulse starts again at its new pace (or stops at None).
        StopPulse();
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        if (!IsOnAir || TopLevel.GetTopLevel(this) is null || MotionScale.Current.Multiplier <= 0)
        {
            StopPulse();
            return;
        }

        if (_pulse is not null)
        {
            return;
        }

        var pulse = _pulse = new CancellationTokenSource();
        _ = RunAsync(NearOpacityProperty, TimeSpan.Zero, pulse.Token);
        _ = RunAsync(FarOpacityProperty, Resource(FarDelay) as TimeSpan? ?? TimeSpan.Zero, pulse.Token);
    }

    private void StopPulse()
    {
        _pulse?.Cancel();
        _pulse?.Dispose();
        _pulse = null;
    }

    private async Task RunAsync(StyledProperty<double> wave, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (Resource(PulseDuration) is not TimeSpan duration || Resource(PulseEasing) is not Easing easing)
        {
            return;
        }

        // 1 → DimOpacity → 1, again and again (mockup: @keyframes onair { 0%, 100% { opacity: 1 } 50% { opacity: .25 } }).
        var pulse = new Animation
        {
            Duration = MotionScale.Current.Scale(duration),
            Delay = MotionScale.Current.Scale(delay),
            IterationCount = IterationCount.Infinite,
            Easing = easing,
        };
        foreach (var (cue, value) in new[] { (0.0, 1.0), (0.5, DimOpacity), (1.0, 1.0) })
        {
            pulse.Children.Add(new KeyFrame { Cue = new Cue(cue), Setters = { new Avalonia.Styling.Setter(wave, value) } });
        }

        await pulse.RunAsync(this, cancellationToken).ConfigureAwait(true);
    }

    private static object? Resource(string key)
        => Application.Current?.TryGetResource(key, Avalonia.Styling.ThemeVariant.Default, out var value) == true ? value : null;
}
