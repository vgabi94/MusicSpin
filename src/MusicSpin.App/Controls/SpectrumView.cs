using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Controls;

/// <summary>
/// Draws a <see cref="SpectrumFrame"/>: one bar per band, built from <see cref="Rows"/> blocks in
/// <see cref="Foreground"/> that grow more opaque toward the top (from <see cref="BottomOpacity"/>), and a peak block in
/// <see cref="PeakBrush"/>. It draws only when the frame changes, so an idle analyzer costs nothing.
/// </summary>
public sealed class SpectrumView : Control
{
    public static readonly StyledProperty<SpectrumFrame?> FrameProperty = AvaloniaProperty.Register<SpectrumView, SpectrumFrame?>(nameof(Frame));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<SpectrumView>();

    public static readonly StyledProperty<IBrush?> PeakBrushProperty = AvaloniaProperty.Register<SpectrumView, IBrush?>(nameof(PeakBrush));

    public static readonly StyledProperty<int> RowsProperty = AvaloniaProperty.Register<SpectrumView, int>(nameof(Rows), 16);

    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<SpectrumView, double>(nameof(BarWidth), 4);

    public static readonly StyledProperty<double> BarGapProperty = AvaloniaProperty.Register<SpectrumView, double>(nameof(BarGap), 1);

    public static readonly StyledProperty<double> BottomOpacityProperty = AvaloniaProperty.Register<SpectrumView, double>(nameof(BottomOpacity), 0.4);

    static SpectrumView()
    {
        AffectsRender<SpectrumView>(FrameProperty, ForegroundProperty, PeakBrushProperty, RowsProperty, BarWidthProperty, BarGapProperty, BottomOpacityProperty);
        FrameProperty.Changed.AddClassHandler<SpectrumView>((v, _) => v.Redraws++);
    }

    public SpectrumFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>The bars' colour (the album's accent).</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? PeakBrush
    {
        get => GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double BarWidth
    {
        get => GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public double BarGap
    {
        get => GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    public double BottomOpacity
    {
        get => GetValue(BottomOpacityProperty);
        set => SetValue(BottomOpacityProperty, value);
    }

    /// <summary>How many times a new frame asked for a redraw.</summary>
    public int Redraws { get; private set; }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Frame is not { } frame || Rows <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var row = Bounds.Height / Rows;
        var color = (Foreground as ISolidColorBrush)?.Color;
        var rowBrushes = new IBrush[Rows];
        for (var r = 0; r < Rows; r++)
        {
            var opacity = Rows == 1 ? 1 : BottomOpacity + ((1 - BottomOpacity) * r / (Rows - 1));
            rowBrushes[r] = color is { } c ? new ImmutableSolidColorBrush(c, opacity) : Foreground ?? Brushes.Transparent;
        }

        for (var i = 0; i < frame.Bars.Count; i++)
        {
            var x = i * (BarWidth + BarGap);
            var height = (int)Math.Round(frame.Bars[i] * Rows);
            for (var r = 0; r < height; r++)
            {
                context.FillRectangle(rowBrushes[r], new Rect(x, Bounds.Height - ((r + 1) * row), BarWidth, row));
            }

            var peak = i < frame.Peaks.Count ? (int)Math.Round(frame.Peaks[i] * Rows) : 0;
            if (peak > 0 && PeakBrush is { } peakBrush)
            {
                context.FillRectangle(peakBrush, new Rect(x, Bounds.Height - (peak * row), BarWidth, row));
            }
        }
    }
}
