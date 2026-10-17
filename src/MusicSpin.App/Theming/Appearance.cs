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

    private void ApplyAccent()
    {
        var accent = _base is { } b ? AccentColors.Accent(b) : DefaultAccent;
        var ink = Theme == AppTheme.Light ? AccentColors.Ink(accent) : accent;
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

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);
}
