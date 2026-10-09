using System.Globalization;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Cli.Output;

/// <summary>Stable JSON shapes. Add fields freely; never rename/remove without bumping <see cref="JsonOutput.SchemaVersion"/>.</summary>
public sealed record TrackDto(
    string Path,
    string Container,
    string Codec,
    string? CodecLongName,
    int SampleRate,
    int? BitsPerSample,
    int Channels,
    string? ChannelLayout,
    string? SampleFormat,
    long? BitRate,
    double DurationSeconds,
    bool Lossless,
    bool HiRes,
    string? Title,
    string? Artist,
    string? Album,
    IReadOnlyDictionary<string, string> Tags)
{
    public static TrackDto From(TrackInfo t) => new(
        t.Path, t.Container, t.Codec, t.CodecLongName, t.SampleRate, t.BitsPerSample, t.Channels, t.ChannelLayout,
        t.SampleFormat, t.BitRate, Math.Round(t.Duration.TotalSeconds, 3), t.IsLossless, t.IsHiRes,
        t.Title, t.Artist, t.Album, t.Tags);
}

public sealed record DecoderDto(string Name, bool Available, string? Version, string? License, bool? Lgpl, string? Error)
{
    public static DecoderDto From(FFmpegStatus? s) => s is null
        ? new DecoderDto("ffmpeg", false, null, null, null, "unknown")
        : new DecoderDto("ffmpeg", s.Available, s.Version, s.License, s.License is null ? null : s.IsLgpl, s.Error);
}

public sealed record FormatsDto(DecoderDto Decoder, IReadOnlyList<AudioFormatSupport> Formats, IReadOnlyList<CodecDto>? AllDecoders);

public sealed record CodecDto(string Name, string? LongName);

public sealed record DevicesDto(string Backend, IReadOnlyList<AudioDeviceInfo> Devices);

public sealed record PlayResultDto(TrackDto Track, double StartSeconds, double PlayedSeconds, string StopReason);

public sealed record DoctorDto(string Version, string Runtime, string Os, DecoderDto Decoder, string? AudioBackend, string? AudioError, int PlaybackDevices);

internal static class Human
{
    public static string Describe(TrackInfo t)
    {
        var parts = new List<string> { t.Codec.ToUpperInvariant() };
        var rate = t.SampleRate >= 1_000_000
            ? $"{t.SampleRate / 1_000_000.0:0.###} MHz"
            : $"{t.SampleRate / 1000.0:0.#} kHz";
        parts.Add(t.BitsPerSample is { } b ? $"{b}-bit / {rate}" : rate);
        parts.Add($"{t.Channels} ch" + (t.ChannelLayout is null ? "" : $" ({t.ChannelLayout})"));
        parts.Add(Time(t.Duration));
        if (t.IsHiRes)
        {
            parts.Add("hi-res");
        }
        else if (t.IsLossless)
        {
            parts.Add("lossless");
        }

        return string.Join(" · ", parts);
    }

    public static string Time(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
