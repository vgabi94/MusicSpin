using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// What shows through a see-through deck (Smoked Acrylic): the image behind it (<see cref="Source"/>, Now Playing's blurred
/// cover) drawn again where it is on screen, so the two line up across the deck's edge. Avalonia can't blur what is behind
/// an element (CSS <c>backdrop-filter</c>); this draws the same image, the same size and place, with the same blur and
/// opacity over the same <see cref="Background"/>, under the same <see cref="Veil"/>. The parent clips it to the plinth.
/// </summary>
public sealed class BackdropView : Control
{
    public static readonly StyledProperty<Image?> SourceProperty = AvaloniaProperty.Register<BackdropView, Image?>(nameof(Source));

    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<BackdropView, IBrush?>(nameof(Background));

    public static readonly StyledProperty<Border?> VeilProperty = AvaloniaProperty.Register<BackdropView, Border?>(nameof(Veil));

    private Matrix _placed = Matrix.Identity;
    private Matrix _veiled = Matrix.Identity;
    private bool _attached;

    static BackdropView()
    {
        AffectsRender<BackdropView>(SourceProperty, BackgroundProperty, VeilProperty);
    }

    public BackdropView()
    {
        LayoutUpdated += (_, _) => Follow();
    }

    /// <summary>What a redraw depends on: the source's bitmap, opacity, stretch and blur; the veil's brush.</summary>
    private static readonly AvaloniaProperty[] Watched =
        [Image.SourceProperty, OpacityProperty, Image.StretchProperty, EffectProperty, Border.BackgroundProperty];

    /// <summary>The image behind the deck.</summary>
    public Image? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>What is under the image (the page's background).</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>What lies over the image (a veil that darkens it), drawn over it here too.</summary>
    public Border? Veil
    {
        get => GetValue(VeilProperty);
        set => SetValue(VeilProperty, value);
    }

    /// <summary>The source's coordinates in this control's (the image lines up through it).</summary>
    public Matrix Placement => _placed;

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
        {
            context.FillRectangle(background, bounds);
        }

        if (Source is { Source: { } image } source && source.Bounds.Width > 0 && source.Bounds.Height > 0)
        {
            var size = source.Bounds.Size;
            var crop = Crop(image.Size, size, source.Stretch);
            using (context.PushOpacity(source.Opacity))
            using (context.PushTransform(_placed))
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = RenderOptions.GetBitmapInterpolationMode(source) }))
            {
                context.DrawImage(image, crop, new Rect(size)); // the same image, stretched the same way
            }
        }

        if (Veil is { Background: { } veil } over)
        {
            using (context.PushTransform(_veiled))
            {
                context.FillRectangle(veil, new Rect(over.Bounds.Size));
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if ((change.Property == SourceProperty || change.Property == VeilProperty) && _attached)
        {
            // The image and the veil stay the same controls while what they show changes (a new album's cover): watch them.
            (change.OldValue as AvaloniaObject)?.PropertyChanged -= OnWatchedChanged;
            (change.NewValue as AvaloniaObject)?.PropertyChanged += OnWatchedChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Source?.PropertyChanged += OnWatchedChanged;
        Veil?.PropertyChanged += OnWatchedChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Source?.PropertyChanged -= OnWatchedChanged;
        Veil?.PropertyChanged -= OnWatchedChanged;
    }

    private void OnWatchedChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (Array.IndexOf(Watched, e.Property) >= 0)
        {
            Follow();
            InvalidateVisual();
        }
    }

    /// <summary>The part of the image an <see cref="Image"/> of <paramref name="size"/> shows (UniformToFill crops it).</summary>
    private static Rect Crop(Size image, Size size, Stretch stretch)
    {
        if (stretch != Stretch.UniformToFill)
        {
            return new Rect(image);
        }

        var scale = Math.Max(size.Width / image.Width, size.Height / image.Height);
        var shown = new Size(size.Width / scale, size.Height / scale);
        return new Rect((image.Width - shown.Width) / 2, (image.Height - shown.Height) / 2, shown.Width, shown.Height);
    }

    /// <summary>Follows the source's place and blur after every layout pass (the window or the deck moved).</summary>
    private void Follow()
    {
        if (Veil?.TransformToVisual(this) is { } veiled && veiled != _veiled)
        {
            _veiled = veiled;
            InvalidateVisual();
        }

        if (Source is not { } source || source.TransformToVisual(this) is not { } placed)
        {
            return;
        }

        // The source's blur is in its own units: here it is as many of ours as one of its units is.
        var radius = source.Effect is BlurEffect blur ? blur.Radius * Math.Sqrt(Math.Abs((placed.M11 * placed.M22) - (placed.M12 * placed.M21))) : 0;
        if (placed != _placed)
        {
            _placed = placed;
            InvalidateVisual();
        }

        if (Effect is not BlurEffect mine ? radius > 0 : Math.Abs(mine.Radius - radius) > 0.01)
        {
            Effect = radius > 0 ? new BlurEffect { Radius = radius } : null;
        }
    }
}
