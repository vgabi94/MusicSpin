using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace MusicSpin.Core.Decoding.FFmpeg;

internal static unsafe class FFmpegHelpers
{
    // EAGAIN differs per OS (FFmpeg's AVERROR(EAGAIN) = -EAGAIN of the platform's libc).
    public static readonly int ErrorAgain = -(OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsFreeBSD() ? 35 : 11);

    public static readonly int ErrorEof = ffmpeg.AVERROR_EOF;

    public static string? Str(byte* p) => p is null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    public static string ErrorString(int error)
    {
        const int size = 1024;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return Str(buffer) ?? $"FFmpeg error {error}";
    }

    public static string? DescribeLayout(AVChannelLayout* layout)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_channel_layout_describe(layout, buffer, size) > 0 ? Str(buffer) : null;
    }

    public static void ReadDictionary(AVDictionary* dict, Dictionary<string, string> target)
    {
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(dict, string.Empty, entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) is not null)
        {
            var key = Str(entry->key)?.ToLowerInvariant();
            var value = Str(entry->value);
            if (key is not null && value is not null)
            {
                target.TryAdd(key, value);
            }
        }
    }

    public static bool IsDsd(AVCodecID id) => id is AVCodecID.AV_CODEC_ID_DSD_LSBF or AVCodecID.AV_CODEC_ID_DSD_MSBF
        or AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR or AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR;
}
