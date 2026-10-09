using FFmpeg.AutoGen;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Decoding.FFmpeg;

/// <summary><see cref="IAudioDecoderFactory"/> backed by FFmpeg shared libraries loaded at runtime.</summary>
public sealed class FFmpegDecoderFactory : IAudioDecoderFactory
{
    public string Name => FFmpegRuntime.Status is { Available: true } s ? $"FFmpeg {s.Version}" : "FFmpeg (unavailable)";

    public bool IsAvailable => FFmpegRuntime.IsAvailable;

    public TrackInfo Probe(string path)
    {
        using var decoder = FFmpegAudioDecoder.Open(path);
        return decoder.Track;
    }

    public IAudioDecoder Open(string path) => FFmpegAudioDecoder.Open(path);

    public IReadOnlyList<AudioFormatSupport> GetSupportedFormats()
    {
        var available = FFmpegRuntime.IsAvailable;
        return KnownFormats.All
            .Select(f =>
            {
                var decoders = available ? f.Decoders.Where(HasDecoder).ToArray() : [];
                return new AudioFormatSupport(f.Id, f.Name, f.Extensions, f.Lossless, f.HiResCapable, decoders.Length > 0, decoders);
            })
            .ToArray();
    }

    /// <summary>Every audio decoder compiled into the loaded FFmpeg build (name, long name).</summary>
    public static unsafe IReadOnlyList<(string Name, string? LongName)> GetAllAudioDecoders()
    {
        FFmpegRuntime.EnsureAvailable();
        var result = new List<(string, string?)>();
        void* opaque = null;
        AVCodec* codec;
        while ((codec = ffmpeg.av_codec_iterate(&opaque)) is not null)
        {
            if (codec->type == AVMediaType.AVMEDIA_TYPE_AUDIO && ffmpeg.av_codec_is_decoder(codec) != 0)
            {
                result.Add((FFmpegHelpers.Str(codec->name) ?? "?", FFmpegHelpers.Str(codec->long_name)));
            }
        }

        return result.OrderBy(c => c.Item1, StringComparer.Ordinal).ToArray();
    }

    private static unsafe bool HasDecoder(string name) => ffmpeg.avcodec_find_decoder_by_name(name) is not null;
}
