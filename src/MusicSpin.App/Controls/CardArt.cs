using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicSpin.App.Controls;

/// <summary>
/// An album card's art (mockup: .card .art): the sleeve with its record behind it, laid out at its own size and scaled
/// to the card. At rest it draws through <see cref="RestCache"/>, an image of it made once, so scrolling the page, or
/// anything near it being drawn again, only copies that image (#149). While the sleeve or the record moves (hovered, or
/// the album loading), it draws itself live until both are still again.
/// </summary>
/// <remarks>
/// <para>
/// The image is on this Viewbox, not on what it scales: Avalonia draws a cache at the screen's scaling, not at its
/// ancestors' scale, so one on the art itself would come out stretched on a card wider than the art.
/// </para>
/// <para>
/// It draws an invisible <see cref="Frame"/> around the art, past anything the art draws, starting on a whole pixel as
/// the card does. Its image then starts there too, so the art in it sits on the pixels it sits on drawn live.
/// </para>
/// <para>
/// Its parts are the children of its child panel (or the child itself). A part is still when its render transform is
/// the one its styles give it, not a value a transition is passing through; each step of a transition says so, and so
/// does its last, so the art is cached again as the move ends.
/// </para>
/// </remarks>
public sealed class CardArt : Viewbox
{
    public static readonly StyledProperty<CacheMode?> RestCacheProperty = AvaloniaProperty.Register<CardArt, CacheMode?>(nameof(RestCache));

    /// <summary>How far past the art its invisible frame reaches, in the art's own units (before it is scaled).</summary>
    public static readonly StyledProperty<Thickness> FrameProperty = AvaloniaProperty.Register<CardArt, Thickness>(nameof(Frame));

    /// <summary>
    /// How near its target a part's transform counts as there, in the art's units: a transition that settles a hair short
    /// of its target doesn't leave the art drawn live for good.
    /// </summary>
    public const double RestTolerance = 1e-4;

    private readonly List<Control> _parts = [];

    /// <summary>The image the art draws through at rest (Styles/Cards.axaml); null draws it live.</summary>
    public CacheMode? RestCache
    {
        get => GetValue(RestCacheProperty);
        set => SetValue(RestCacheProperty, value);
    }

    public Thickness Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>No part is moving: the art draws through <see cref="RestCache"/>.</summary>
    public bool IsAtRest => _parts.All(IsStill);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unwatch();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty && VisualRoot is not null)
        {
            Unwatch();
            Watch();
        }
        else if (change.Property == RestCacheProperty)
        {
            UpdateRest();
        }
        else if (change.Property == FrameProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>The invisible frame, scaled with the art and out to whole pixels.</summary>
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        var size = Child?.Bounds.Size ?? default;
        var scale = size.Width > 0 && size.Height > 0 ? Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height) : 1;
        var pixels = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double Out(double units) => Math.Ceiling(units * scale * pixels) / pixels;
        context.FillRectangle(Brushes.Transparent, new Rect(
            -Out(Frame.Left), -Out(Frame.Top), Bounds.Width + Out(Frame.Left) + Out(Frame.Right), Bounds.Height + Out(Frame.Top) + Out(Frame.Bottom)));
    }

    private void Watch()
    {
        _parts.AddRange(Child switch
        {
            Panel panel => panel.Children,
            { } child => [child],
            null => [],
        });
        foreach (var part in _parts)
        {
            part.PropertyChanged += OnPartChanged;
        }

        UpdateRest();
    }

    private void Unwatch()
    {
        foreach (var part in _parts)
        {
            part.PropertyChanged -= OnPartChanged;
        }

        _parts.Clear();
    }

    private void OnPartChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RenderTransformProperty)
        {
            UpdateRest();
        }
    }

    private void UpdateRest() => CacheMode = IsAtRest ? RestCache : null;

    /// <summary>Its transform is the one its styles (or its own value) give it, not one a transition is between.</summary>
    private static bool IsStill(Control part)
    {
        var now = part.RenderTransform?.Value ?? Matrix.Identity;
        var target = part.GetBaseValue(RenderTransformProperty).GetValueOrDefault()?.Value ?? Matrix.Identity;
        return Near(now.M11, target.M11) && Near(now.M12, target.M12) && Near(now.M21, target.M21)
               && Near(now.M22, target.M22) && Near(now.M31, target.M31) && Near(now.M32, target.M32);

        static bool Near(double a, double b) => Math.Abs(a - b) < RestTolerance;
    }
}
