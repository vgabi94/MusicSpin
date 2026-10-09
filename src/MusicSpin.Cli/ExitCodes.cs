using MusicSpin.Core;

namespace MusicSpin.Cli;

/// <summary>
/// Process exit codes. Stable contract for scripts and AI agents; documented in README and `--help`.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int FileNotFound = 3;
    public const int UnsupportedFormat = 4;
    public const int DecodeFailed = 5;
    public const int DecoderUnavailable = 6;
    public const int AudioDeviceError = 7;
    public const int InvalidState = 8;
    public const int Cancelled = 130;

    public static int From(MusicSpinErrorCode code) => code switch
    {
        MusicSpinErrorCode.FileNotFound => FileNotFound,
        MusicSpinErrorCode.UnsupportedFormat => UnsupportedFormat,
        MusicSpinErrorCode.DecodeFailed => DecodeFailed,
        MusicSpinErrorCode.DecoderUnavailable => DecoderUnavailable,
        MusicSpinErrorCode.AudioDeviceError => AudioDeviceError,
        MusicSpinErrorCode.InvalidState => InvalidState,
        _ => Error,
    };

    /// <summary>snake_case error code used in JSON (<c>error.code</c>).</summary>
    public static string Name(int exitCode) => exitCode switch
    {
        Success => "ok",
        Usage => "usage",
        FileNotFound => "file_not_found",
        UnsupportedFormat => "unsupported_format",
        DecodeFailed => "decode_failed",
        DecoderUnavailable => "decoder_unavailable",
        AudioDeviceError => "audio_device_error",
        InvalidState => "invalid_state",
        Cancelled => "cancelled",
        _ => "error",
    };

    public const string HelpText =
        "Exit codes: 0 ok, 1 error, 2 usage, 3 file_not_found, 4 unsupported_format, 5 decode_failed, " +
        "6 decoder_unavailable (FFmpeg missing), 7 audio_device_error, 8 invalid_state, 130 cancelled.";
}
