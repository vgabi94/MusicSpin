using System.Globalization;
using Avalonia.Data.Converters;
using MusicSpin.App.Services;

namespace MusicSpin.App.Theming;

/// <summary>Text converters for templates (CSS text-transform has no Avalonia equivalent).</summary>
public static class TextConverters
{
    /// <summary>The text in capitals (mockup: text-transform: uppercase).</summary>
    public static FuncValueConverter<string?, string?> Upper { get; } = new(s => s?.ToUpper(CultureInfo.CurrentCulture));

    /// <summary>Seconds as a clock ("4:05"); nothing for no value.</summary>
    public static FuncValueConverter<double?, string> Clock { get; } = new(s => s is { } seconds ? Formats.Time(seconds) : string.Empty);
}
