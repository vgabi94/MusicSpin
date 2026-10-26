using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Controls;

/// <summary>
/// Draws a <see cref="SpectrumFrame"/>: one bar per band, built from <see cref="SpectrumViewModel.Rows"/> blocks in
/// <see cref="Foreground"/> that grow more opaque toward the top (from <see cref="BottomOpacity"/>), and a peak block in
/// <see cref="PeakBrush"/>. It draws only when the frame changes, so an idle analyzer costs nothing. The rows' brushes
/// are made once, and again only when the colour or the opacity changes (#126).
/// </summary>
public sealed class SpectrumView : Control
{
    public static readonly StyledProperty<SpectrumFrame?> FrameProperty = AvaloniaProperty.Register<SpectrumView, SpectrumFrame?>(nameof(Frame));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<SpectrumView>();

    public static readonly StyledProperty<IBrush?> PeakBrushProperty = AvaloniaProperty.Register<SpectrumView, IBrush?>(nameof(PeakBrush));

    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<SpectrumView, double>(nameof(BarWidth), 4);

    public static readonly StyledProperty<double> BarGapProperty = AvaloniaProperty.Register<SpectrumView, double>(nameof(BarGap), 1);

    public static readonly StyledProperty<double> BottomOpacityProperty = AvaloniaProperty.Register<SpectrumView, double>(nameof(BottomOpacity), 0.4);

    static SpectrumView()
    {
        AffectsRender<SpectrumView>(FrameProperty, ForegroundProperty, PeakBrushProperty, BarWidthProperty, BarGapProperty, BottomOpacityProperty);
        FrameProperty.Changed.AddClassHandler<SpectrumView>((v, _) => v.Redraws++);
        ForegroundProperty.Changed.AddClassHandler<SpectrumView>((v, _) => v._rowBrushes = null);
        BottomOpacityProperty.Changed.AddClassHandler<SpectrumView>((v, _) => v._rowBrushes = null);
    }

    private IBrush[]? _rowBrushes;

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

    /// <summary>How many times the rows' brushes were made.</summary>
    internal int BrushBuilds { get; private set; }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Frame is not { } frame || Bounds.Height <= 0)
        {
            return;
        }

        const int rows = SpectrumViewModel.Rows;
        var row = Bounds.Height / rows;
        var rowBrushes = _rowBrushes ??= RowBrushes();
        for (var i = 0; i < frame.Bars.Count; i++)
        {
            var x = i * (BarWidth + BarGap);
            var height = Math.Clamp(SpectrumViewModel.Blocks(frame.Bars[i]), 0, rows);
            for (var r = 0; r < height; r++)
            {
                context.FillRectangle(rowBrushes[r], new Rect(x, Bounds.Height - ((r + 1) * row), BarWidth, row));
            }

            var peak = i < frame.Peaks.Count ? SpectrumViewModel.Blocks(frame.Peaks[i]) : 0;
            if (peak > 0 && PeakBrush is { } peakBrush)
            {
                context.FillRectangle(peakBrush, new Rect(x, Bounds.Height - (peak * row), BarWidth, row));
            }
        }
    }

    /// <summary>Each row's brush: the colour, more opaque toward the top.</summary>
    private IBrush[] RowBrushes()
    {
        BrushBuilds++;
        const int rows = SpectrumViewModel.Rows;
        var color = (Foreground as ISolidColorBrush)?.Color;
        var brushes = new IBrush[rows];
        for (var r = 0; r < rows; r++)
        {
            var opacity = BottomOpacity + ((1 - BottomOpacity) * r / (rows - 1));
            brushes[r] = color is { } c ? new ImmutableSolidColorBrush(c, opacity) : Foreground ?? Brushes.Transparent;
        }

        return brushes;
    }
}
