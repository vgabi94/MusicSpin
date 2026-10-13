using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// An icon from Icons/IconsResource.axaml: a geometry in any coordinate space, fitted to the control (uniformly,
/// centred, like <c>PathIcon</c>) and drawn in <see cref="Foreground"/>: filled, or as lines <see cref="StrokeThickness"/>
/// pixels wide when <see cref="IsStroked"/>.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<bool> IsStrokedProperty = AvaloniaProperty.Register<Icon, bool>(nameof(IsStroked));

    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.5);

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

    /// <summary>Line width in pixels (not scaled with the icon).</summary>
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

        // Lines are centred on the path, so leave half a line around it.
        var inset = IsStroked ? StrokeThickness : 0;
        var bounds = data.Bounds;
        var room = new Size(Math.Max(0, Bounds.Width - inset), Math.Max(0, Bounds.Height - inset));
        var scale = Scale(bounds.Width, room.Width, bounds.Height, room.Height);
        if (scale <= 0)
        {
            return;
        }

        var offset = new Vector(
            ((Bounds.Width - (bounds.Width * scale)) / 2) - (bounds.X * scale),
            ((Bounds.Height - (bounds.Height * scale)) / 2) - (bounds.Y * scale));
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            if (IsStroked)
            {
                context.DrawGeometry(null, new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
            }
            else
            {
                context.DrawGeometry(brush, null, data);
            }
        }
    }

    /// <summary>The uniform scale that fits the path; a flat path (a single line) fits by its other side.</summary>
    private static double Scale(double width, double roomWidth, double height, double roomHeight)
        => (width > 0, height > 0) switch
        {
            (true, true) => Math.Min(roomWidth / width, roomHeight / height),
            (true, false) => roomWidth / width,
            (false, true) => roomHeight / height,
            _ => 0,
        };
}
