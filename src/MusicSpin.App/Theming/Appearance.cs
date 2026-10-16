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
    }

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);
}
