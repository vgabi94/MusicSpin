using System.Globalization;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Core.Metadata;
using MusicSpin.Core.Remote;

namespace MusicSpin.Cli.Output;

public sealed record DecoderDto(string Name, bool Available, string? Version, string? License, bool? Lgpl, string? Error, string? Path = null)
{
    public static DecoderDto From(FFmpegStatus? s) => s is null
        ? new DecoderDto("ffmpeg", false, null, null, null, "unknown")
        : new DecoderDto("ffmpeg", s.Available, s.Version, s.License, s.License is null ? null : s.IsLgpl, s.Error,
            string.IsNullOrEmpty(s.LibraryPath) ? null : s.LibraryPath);
}

public sealed record FormatsDto(DecoderDto Decoder, IReadOnlyList<AudioFormatSupport> Formats, IReadOnlyList<CodecDto>? AllDecoders);

public sealed record CodecDto(string Name, string? LongName);

public sealed record DevicesDto(string Backend, IReadOnlyList<AudioDeviceInfo> Devices);

public sealed record PlayResultDto(TrackDto Track, double StartSeconds, double PlayedSeconds, string StopReason, string Mode = "file");

public sealed record DoctorDto(string Version, string Runtime, string Os, DecoderDto Decoder, string? AudioBackend, string? AudioError, int PlaybackDevices,
    DataDirDto DataDir, string ExecutableDirectory);

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
