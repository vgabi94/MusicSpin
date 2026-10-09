namespace MusicSpin.Core.Library;

/// <summary>Which files the scanner considers and how codecs map to a short format id.</summary>
public static class AudioFileTypes
{
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".m4a", ".mp4", ".m4b", ".caf", ".alac", ".wav", ".w64", ".rf64", ".aif", ".aiff", ".aifc",
        ".dsf", ".dff", ".wv", ".ape", ".tta", ".mp3", ".mp2", ".aac", ".ogg", ".oga", ".opus", ".wma", ".mpc", ".mka",
    };

    public static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path));

    private static readonly HashSet<string> LosslessFormats = new(StringComparer.Ordinal)
    {
        "flac", "alac", "pcm", "dsd", "wavpack", "ape", "tta", "mlp", "wmalossless",
    };

    public static bool IsLosslessFormat(string format) => LosslessFormats.Contains(format);

    /// <summary>
    /// Short format id ("flac", "alac", "aac", "mp3", "pcm", "dsd", "vorbis", "opus", ...) from a codec name or
    /// description (FFmpeg codec names and TagLib# descriptions both work), falling back to the extension.
    /// </summary>
    public static string FormatId(string? codec, string extension)
    {
        var c = codec?.ToLowerInvariant() ?? string.Empty;
        if (c.Contains("alac", StringComparison.Ordinal)) return "alac";
        if (c.Contains("flac", StringComparison.Ordinal)) return "flac";
        if (c.StartsWith("dsd", StringComparison.Ordinal) || c.Contains("dsf", StringComparison.Ordinal)) return "dsd";
        if (c.StartsWith("pcm", StringComparison.Ordinal)) return "pcm";
        if (c.Contains("wavpack", StringComparison.Ordinal)) return "wavpack";
        if (c.Contains("monkey", StringComparison.Ordinal) || c == "ape") return "ape";
        if (c.Contains("layer 3", StringComparison.Ordinal) || c.StartsWith("mp3", StringComparison.Ordinal)) return "mp3";
        if (c.Contains("aac", StringComparison.Ordinal) || c.Contains("mpeg-4 audio", StringComparison.Ordinal)) return "aac";
        if (c.Contains("opus", StringComparison.Ordinal)) return "opus";
        if (c.Contains("vorbis", StringComparison.Ordinal)) return "vorbis";
        if (c.Contains("wmalossless", StringComparison.Ordinal)) return "wmalossless";
        if (c.StartsWith("wma", StringComparison.Ordinal)) return "wma";
        if (c == "tta" || c.Contains("true audio", StringComparison.Ordinal)) return "tta";

        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "flac" => "flac",
            "wav" or "w64" or "rf64" or "aif" or "aiff" or "aifc" => "pcm",
            "dsf" or "dff" => "dsd",
            "wv" => "wavpack",
            "ape" => "ape",
            "tta" => "tta",
            "mp3" or "mp2" => "mp3",
            "aac" => "aac",
            "opus" => "opus",
            "ogg" or "oga" => "vorbis",
            "wma" => "wma",
            "mpc" => "musepack",
            "m4a" or "mp4" or "m4b" => "aac",
            var other => other,
        };
    }

    /// <summary>Hi-res: lossless and &gt;16-bit or &gt;48 kHz; DSD always counts.</summary>
    public static bool IsHiRes(string format, bool lossless, int? bitsPerSample, int sampleRate)
        => format == "dsd" || (lossless && (bitsPerSample > 16 || sampleRate > 48_000));
}
