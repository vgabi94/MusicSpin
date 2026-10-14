using System.Globalization;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.Services;

/// <summary>
/// The player bar's format label (mockup: <c>techLabel</c>): the format in bold, then the details. "FLAC" + "24 bit,
/// 192 kHz, 6431k, Stereo"; lossy files have no bit depth ("MP3" + "44.1 kHz, 320k, Stereo"); DSD is named by its rate
/// ("DSD64" + "1 bit, 2.8 MHz, 5645k, Stereo").
/// </summary>
public sealed record TrackFormat(string Name, string Details)
{
    private const int DsdBaseRate = 44_100;

    public static TrackFormat? Of(TrackDto? track)
    {
        if (track is null)
        {
            return null;
        }

        var format = AudioFileTypes.FormatId(track.Codec, Path.GetExtension(track.Path));
        var dsd = format == "dsd";
        var parts = new List<string>(4);
        if ((track.Lossless || dsd) && track.BitsPerSample is { } bits)
        {
            parts.Add(Invariant($"{bits} bit"));
        }

        if (track.SampleRate > 0)
        {
            parts.Add(Rate(track.SampleRate));
        }

        if (track.BitRate is > 0 and var bitRate)
        {
            parts.Add(Invariant($"{Math.Round(bitRate / 1000.0)}k"));
        }

        parts.Add(track.Channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            var n => Invariant($"{n} channels"),
        });
        return new(DisplayName(format, track), string.Join(", ", parts));
    }

    private static string DisplayName(string format, TrackDto track) => format switch
    {
        "dsd" => Invariant($"DSD{track.SampleRate / DsdBaseRate}"),
        "pcm" => Path.GetExtension(track.Path).TrimStart('.').ToLowerInvariant() is "aif" or "aiff" or "aifc" ? "AIFF" : "WAV",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "wavpack" => "WavPack",
        "musepack" => "Musepack",
        "wmalossless" => "WMA Lossless",
        _ => format.ToUpperInvariant(),
    };

    /// <summary>"44.1 kHz", "192 kHz", "2.8 MHz".</summary>
    private static string Rate(int hz)
        => hz >= 1_000_000 ? Invariant($"{Math.Round(hz / 1_000_000.0, 1)} MHz") : Invariant($"{Math.Round(hz / 1000.0, 1)} kHz");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
