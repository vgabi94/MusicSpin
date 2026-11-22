using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MusicSpin.App.Theming;

namespace MusicSpin.App.Controls;

/// <summary>
/// A thin indeterminate loading bar for the startup splash screen (#226): a rounded track with an accent segment
/// sweeping across it at <see cref="CycleDuration"/>, scaled by the Motion setting. At Motion None the segment stays
/// still in the middle of the track.
/// </summary>
#pragma warning disable CA1001 // controls aren't disposed: the animation's source is cancelled and disposed when the bar leaves the tree
public sealed class LoadingBar : Control
#pragma warning restore CA1001
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<LoadingBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty = AvaloniaProperty.Register<LoadingBar, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<double> CornerRadiusProperty = AvaloniaProperty.Register<LoadingBar, double>(nameof(CornerRadius));

    /// <summary>The moving highlight's width as a share of the track (0..1).</summary>
    public static readonly StyledProperty<double> SegmentShareProperty = AvaloniaProperty.Register<LoadingBar, double>(nameof(SegmentShare), 0.35);

    public static readonly StyledProperty<bool> IsLoadingProperty = AvaloniaProperty.Register<LoadingBar, bool>(nameof(IsLoading));

    /// <summary>Position of the highlight along the track, 0 (entering left) to 1 (leaving right); 0.5 is centred.</summary>
    public static readonly StyledProperty<double> PhaseProperty = AvaloniaProperty.Register<LoadingBar, double>(nameof(Phase), 0.5);

    /// <summary>The sweep's resources in Styles/Motion.axaml.</summary>
    public const string CycleDuration = "SplashLoadDuration";

    public const string CycleEasing = "EaseInOut";

    private CancellationTokenSource? _animation;

    static LoadingBar()
    {
        AffectsRender<LoadingBar>(TrackBrushProperty, FillBrushProperty, CornerRadiusProperty, SegmentShareProperty, IsLoadingProperty, PhaseProperty);
        IsLoadingProperty.Changed.AddClassHandler<LoadingBar>((bar, _) => bar.UpdateAnimation());
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double SegmentShare
    {
        get => GetValue(SegmentShareProperty);
        set => SetValue(SegmentShareProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public double Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    /// <summary>The highlight is sweeping now (tests read it).</summary>
    public bool IsSweeping => _animation is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionScale.Current.PropertyChanged += OnMotionChanged;
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MotionScale.Current.PropertyChanged -= OnMotionChanged;
        StopAnimation();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = Math.Clamp(CornerRadius, 0, Math.Min(width, height) / 2);
        var track = new RoundedRect(new Rect(0, 0, width, height), radius);
        if (TrackBrush is { } trackBrush)
        {
            context.DrawRectangle(trackBrush, null, track);
        }

        if (!IsLoading || FillBrush is not { } fillBrush)
        {
            return;
        }

        var segmentWidth = Math.Clamp(SegmentShare, 0.05, 1) * width;
        var phase = Math.Clamp(Phase, 0, 1);
        var x = -segmentWidth + (phase * (width + segmentWidth));
        using (context.PushClip(track))
        {
            context.DrawRectangle(fillBrush, null, new RoundedRect(new Rect(x, 0, segmentWidth, height), radius));
        }
    }

    private void OnMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        StopAnimation();
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (!IsLoading || TopLevel.GetTopLevel(this) is null || MotionScale.Current.Multiplier <= 0)
        {
            StopAnimation();
            return;
        }

        if (_animation is not null)
        {
            return;
        }

        var cts = _animation = new CancellationTokenSource();
        _ = RunAsync(cts.Token);
    }

    private void StopAnimation()
    {
        _animation?.Cancel();
        _animation?.Dispose();
        _animation = null;
        ClearValue(PhaseProperty);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Resource(CycleDuration) is not TimeSpan duration || Resource(CycleEasing) is not Easing easing)
        {
            return;
        }

        var sweep = new Animation
        {
            Duration = MotionScale.Current.Scale(duration),
            IterationCount = IterationCount.Infinite,
            Easing = easing,
        };
        sweep.Children.Add(new KeyFrame { Cue = new Cue(0), Setters = { new Setter(PhaseProperty, 0.0) } });
        sweep.Children.Add(new KeyFrame { Cue = new Cue(1), Setters = { new Setter(PhaseProperty, 1.0) } });
        try
        {
            await sweep.RunAsync(this, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // stopped on close or motion change
        }
    }

    private static object? Resource(string key)
        => Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true ? value : null;
}
