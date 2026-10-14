using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace MusicSpin.App.Theming;

/// <summary>Settings › Motion. Every animation duration is multiplied by <see cref="MotionModes.Multiplier"/>.</summary>
public enum MotionMode
{
    /// <summary>Full durations.</summary>
    Smooth,

    /// <summary>Half durations.</summary>
    Fast,

    /// <summary>Instant. The record still spins (only its spin-up and spin-down are skipped).</summary>
    None,
}

public static class MotionModes
{
    public static double Multiplier(MotionMode mode) => mode switch
    {
        MotionMode.Fast => 0.5,
        MotionMode.None => 0,
        _ => 1,
    };

    /// <summary>The <c>settings.json</c> value ("smooth" | "fast" | "none"); unknown values are Smooth.</summary>
    public static MotionMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fast" => MotionMode.Fast,
        "none" => MotionMode.None,
        _ => MotionMode.Smooth,
    };

    public static string ToSetting(MotionMode mode) => mode.ToString().ToLowerInvariant();
}

/// <summary>
/// The current Motion multiplier for views. Views don't scale durations themselves: they use
/// <see cref="MotionExtension"/>, which binds a duration to this. Code that animates by itself calls <see cref="Scale"/>.
/// </summary>
public sealed class MotionScale : INotifyPropertyChanged
{
    public static MotionScale Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Multiplier { get; private set; } = 1;

    public void Set(MotionMode mode)
    {
        var multiplier = MotionModes.Multiplier(mode);
        if (Multiplier.Equals(multiplier))
        {
            return;
        }

        Multiplier = multiplier;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Multiplier)));
    }

    public TimeSpan Scale(TimeSpan duration) => duration * Multiplier;
}

/// <summary>
/// <c>Duration="{theming:Motion HoverDuration}"</c>: the base duration from Styles/Motion.axaml, multiplied by the
/// Motion setting, and updated when the setting changes.
/// </summary>
public sealed class MotionExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (Application.Current?.TryGetResource(Key, ThemeVariant.Default, out var value) != true || value is not TimeSpan duration)
        {
            throw new InvalidOperationException($"No duration resource '{Key}' (see Styles/Motion.axaml).");
        }

        return new ReflectionBinding(nameof(MotionScale.Multiplier))
        {
            Source = MotionScale.Current,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<double, TimeSpan>(m => duration * m),
        };
    }
}
