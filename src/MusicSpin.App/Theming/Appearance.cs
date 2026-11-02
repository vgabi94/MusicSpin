using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using MusicSpin.App.Services;

namespace MusicSpin.App.Theming;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>Applies the theme, the Motion setting and the album accent to the running app.</summary>
public interface IAppearance
{
    AppTheme Theme { get; }

    void SetTheme(AppTheme theme);

    void SetMotion(MotionMode motion);

    /// <summary>The playing album's accent base colour; null restores the default accent.</summary>
    void SetAccent(Rgb? color);
}

/// <summary>
/// Sets <see cref="Application.RequestedThemeVariant"/> (the theme dictionaries in Styles/Tokens.axaml follow it) and
/// replaces <c>AccentBrush</c> / <c>AccentInkBrush</c> in the application resources (views use them as DynamicResource).
/// The accent and its ink are made to read on the theme (#194): see <see cref="Colors"/>.
/// </summary>
public sealed class Appearance(Application application) : IAppearance
{
    /// <summary>The mockup's default accent (before an album with art plays).</summary>
    public static readonly Rgb DefaultAccent = new(0xE8, 0x6A, 0x3C);

    /// <summary>The accent at 18% (0x2E): the current track in Now Playing.</summary>
    private const byte TintAlpha = 0x2E;

    /// <summary>The accent at 32% (0x52): a row just added to the queue flashes from it (mockup: @keyframes qflash).</summary>
    private const byte FlashAlpha = 0x52;

    /// <summary>The accent at 8% (0x14): the queue's empty box while something is dragged over it (mockup: .queue.over .qempty).</summary>
    private const byte WashAlpha = 0x14;

    /// <summary>The accent at 40% (0x66): a playlist just saved flashes from it in the sidebar (mockup: @keyframes navflash).</summary>
    private const byte HighlightAlpha = 0x66;

    /// <summary>The accent at 50% (0x80): the edge of what is on air (mockup: .radio-btn.on, .onair borders).</summary>
    private const byte RingAlpha = 0x80;

    /// <summary>The accent at 75% (0xBF): an included tag's edge (mockup: .tag.inc border).</summary>
    private const byte StrongRingAlpha = 0xBF;

    /// <summary>The accent at 20% (0x33): an included tag's fill, over the panel colour (mockup: .tag.inc).</summary>
    private const byte ChipAlpha = 0x33;

    /// <summary>The accent at 45% (0x73): the live station's edge, a crate's Radio on air (mockup: .station.live, .crate-box .radio-btn.on).</summary>
    private const byte LiveAlpha = 0x73;

    /// <summary>The accent at 14% (0x24): the RADIO pill under the pointer (mockup: .onair:hover).</summary>
    private const byte HoverAlpha = 0x24;

    /// <summary>The accent at 16% (0x29), fading out by 70%: the live station's wash (mockup: .station.live, linear-gradient(160deg, …)).</summary>
    private const byte GlowAlpha = 0x29;

    /// <summary>
    /// The theme's backgrounds, the page first: accent text is drawn on each (the page, the sidebar and bars, cards and
    /// panels), and the tinted current row on the page.
    /// </summary>
    private static readonly string[] SurfaceKeys = ["BgBrush", "Bg2Brush", "Bg3Brush"];

    private Rgb? _base;

    public AppTheme Theme { get; private set; } = AppTheme.Dark;

    public static AppTheme ParseTheme(string? value)
        => string.Equals(value?.Trim(), "light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        application.RequestedThemeVariant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyAccent();
    }

    public void SetMotion(MotionMode motion) => MotionScale.Current.Set(motion);

    public void SetAccent(Rgb? color)
    {
        _base = color;
        ApplyAccent();
    }

    /// <summary>
    /// The accent and its ink for a cover's colour (<paramref name="cover"/>, null without art) on a theme whose
    /// backgrounds are <paramref name="surfaces"/> (the page first), with <paramref name="onAccent"/> as the text on
    /// accent fills:
    /// <list type="bullet">
    /// <item>A grey cover (<see cref="AccentColors.IsGrey"/>), or none, gives <see cref="DefaultAccent"/>.</item>
    /// <item>The accent is a fill under <paramref name="onAccent"/> text in both themes, and on the dark theme it is the
    /// text colour as well. It is lightened (darkened, were <paramref name="onAccent"/> light), keeping its hue, until it
    /// has <see cref="AccentColors.MinContrast"/> against <paramref name="onAccent"/> and, on the dark theme, against
    /// each surface and against the page under the accent tint (the current row in Now Playing).</item>
    /// <item>On the light theme the ink starts as the accent mixed with black and is darkened, keeping its hue, until it
    /// has that contrast against each surface and the page under the tint.</item>
    /// </list>
    /// The tinted brushes are made from the returned accent, so they follow it.
    /// </summary>
    public static (Rgb Accent, Rgb Ink) Colors(Rgb? cover, AppTheme theme, IReadOnlyList<Rgb> surfaces, Rgb onAccent)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        var picked = cover is { } c && !AccentColors.IsGrey(c) ? AccentColors.Accent(c) : DefaultAccent;
        var light = theme == AppTheme.Light;
        var accent = AccentColors.Readable(
            picked,
            a => AccentColors.Contrast(a, onAccent) >= AccentColors.MinContrast && (light || AccentColors.ReadsOn(a, Behind(a, surfaces))),
            lighten: AccentColors.Luminance(onAccent) < 0.5);
        var ink = light
            ? AccentColors.Readable(AccentColors.Ink(accent), i => AccentColors.ReadsOn(i, Behind(accent, surfaces)), lighten: false)
            : accent;
        return (accent, ink);
    }

    /// <summary>What accent text can be drawn on: each surface, and the page (the first) under the accent tint.</summary>
    private static IEnumerable<Rgb> Behind(Rgb accent, IReadOnlyList<Rgb> surfaces)
        => surfaces.Count == 0 ? [] : surfaces.Append(Tinted(accent, surfaces[0]));

    /// <summary><paramref name="surface"/> under <c>AccentTintBrush</c>.</summary>
    public static Rgb Tinted(Rgb accent, Rgb surface) => AccentColors.Mix(accent, surface, TintAlpha / 255.0);

    /// <summary><see cref="Colors"/> on <paramref name="theme"/>'s backgrounds and accent text, as the app's resources have them.</summary>
    public (Rgb Accent, Rgb Ink) ColorsFor(Rgb? cover, AppTheme theme)
    {
        var variant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        var surfaces = SurfaceKeys.Select(key => Resource(key, variant)).OfType<Rgb>().ToList();
        return Colors(cover, theme, surfaces, Resource("OnAccentBrush", variant) ?? new Rgb(0, 0, 0));
    }

    private void ApplyAccent()
    {
        var (accent, ink) = ColorsFor(_base, Theme);
        application.Resources["AccentBrush"] = new SolidColorBrush(ToColor(accent));
        application.Resources["AccentInkBrush"] = new SolidColorBrush(ToColor(ink));
        application.Resources["AccentTintBrush"] = new SolidColorBrush(Color.FromArgb(TintAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentFlashBrush"] = new SolidColorBrush(Color.FromArgb(FlashAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentWashBrush"] = new SolidColorBrush(Color.FromArgb(WashAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentHighlightBrush"] = new SolidColorBrush(Color.FromArgb(HighlightAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentRingBrush"] = new SolidColorBrush(Color.FromArgb(RingAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentStrongRingBrush"] = new SolidColorBrush(Color.FromArgb(StrongRingAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentChipBrush"] = new SolidColorBrush(Color.FromArgb(ChipAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentLiveBrush"] = new SolidColorBrush(Color.FromArgb(LiveAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromArgb(HoverAlpha, accent.R, accent.G, accent.B));
        application.Resources["AccentGlowBrush"] = Glow(accent);
    }

    /// <summary>The live station's wash: the accent at 16% top left, gone by 70% of the way down (160deg).</summary>
    private static LinearGradientBrush Glow(Rgb accent) => new()
    {
        StartPoint = new RelativePoint(0.33, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.67, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(GlowAlpha, accent.R, accent.G, accent.B), 0),
            new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.7),
        },
    };

    /// <summary>A solid brush of the theme (Styles/Tokens.axaml) as an opaque colour; null when it isn't there.</summary>
    private Rgb? Resource(string key, ThemeVariant variant)
        => application.TryGetResource(key, variant, out var value) && value is ISolidColorBrush { Color: var c } ? new Rgb(c.R, c.G, c.B) : null;

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);
}
