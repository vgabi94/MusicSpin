using System.Globalization;

namespace MusicSpin.App.Services;

/// <summary>Text formats the views share.</summary>
public static class Formats
{
    /// <summary>"4:05", or "1:02:03" from an hour.</summary>
    public static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
