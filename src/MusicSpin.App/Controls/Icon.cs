using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// An icon from Styles/Icons.axaml (a geometry on a 24×24 grid), scaled into the control and drawn in
/// <see cref="Foreground"/>: filled, or stroked with <see cref="StrokeThickness"/> when <see cref="IsStroked"/>.
/// </summary>
public sealed class Icon : Control
{
    public const double GridSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<bool> IsStrokedProperty = AvaloniaProperty.Register<Icon, bool>(nameof(IsStroked));

    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.7);

    static Icon() => AffectsRender<Icon>(DataProperty, ForegroundProperty, IsStrokedProperty, StrokeThicknessProperty);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsStroked
    {
        get => GetValue(IsStrokedProperty);
        set => SetValue(IsStrokedProperty, value);
    }

    /// <summary>Stroke width in grid units (scaled with the icon).</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Data is not { } data || Foreground is not { } brush || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / GridSize;
        var offset = new Vector((Bounds.Width - (GridSize * scale)) / 2, (Bounds.Height - (GridSize * scale)) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            if (IsStroked)
            {
                context.DrawGeometry(null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
            }
            else
            {
                context.DrawGeometry(brush, null, data);
            }
        }
    }
}
