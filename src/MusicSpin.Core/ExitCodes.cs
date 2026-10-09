namespace MusicSpin.Core;

/// <summary>
/// Process exit codes and their snake_case names (<c>error.code</c> in JSON). Stable contract for scripts and AI agents;
/// shared by the CLI and the background player protocol. Documented in README and `music-spin --help`.
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
    public const int DataDirNotWritable = 9;
    public const int LibraryCorrupt = 10;
    public const int LibraryLocked = 11;
    public const int NotFound = 12;
    public const int PlayerNotRunning = 13;
    public const int PlayerUnavailable = 14;
    public const int Cancelled = 130;

    public static int From(MusicSpinErrorCode code) => code switch
    {
        MusicSpinErrorCode.FileNotFound => FileNotFound,
        MusicSpinErrorCode.UnsupportedFormat => UnsupportedFormat,
        MusicSpinErrorCode.DecodeFailed => DecodeFailed,
        MusicSpinErrorCode.DecoderUnavailable => DecoderUnavailable,
        MusicSpinErrorCode.AudioDeviceError => AudioDeviceError,
        MusicSpinErrorCode.InvalidState => InvalidState,
        MusicSpinErrorCode.DataDirectoryNotWritable => DataDirNotWritable,
        MusicSpinErrorCode.LibraryCorrupt => LibraryCorrupt,
        MusicSpinErrorCode.LibraryLocked => LibraryLocked,
        MusicSpinErrorCode.NotFound => NotFound,
        MusicSpinErrorCode.InvalidArgument => Usage,
        MusicSpinErrorCode.PlayerNotRunning => PlayerNotRunning,
        MusicSpinErrorCode.PlayerUnavailable => PlayerUnavailable,
        _ => Error,
    };

    /// <summary>Inverse of <see cref="From"/> (for errors that crossed the player protocol).</summary>
    public static MusicSpinErrorCode ToErrorCode(int exitCode) => exitCode switch
    {
        FileNotFound => MusicSpinErrorCode.FileNotFound,
        UnsupportedFormat => MusicSpinErrorCode.UnsupportedFormat,
        DecodeFailed => MusicSpinErrorCode.DecodeFailed,
        DecoderUnavailable => MusicSpinErrorCode.DecoderUnavailable,
        AudioDeviceError => MusicSpinErrorCode.AudioDeviceError,
        InvalidState => MusicSpinErrorCode.InvalidState,
        DataDirNotWritable => MusicSpinErrorCode.DataDirectoryNotWritable,
        LibraryCorrupt => MusicSpinErrorCode.LibraryCorrupt,
        LibraryLocked => MusicSpinErrorCode.LibraryLocked,
        NotFound => MusicSpinErrorCode.NotFound,
        Usage => MusicSpinErrorCode.InvalidArgument,
        PlayerNotRunning => MusicSpinErrorCode.PlayerNotRunning,
        PlayerUnavailable => MusicSpinErrorCode.PlayerUnavailable,
        _ => MusicSpinErrorCode.Unknown,
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
        DataDirNotWritable => "data_dir_not_writable",
        LibraryCorrupt => "library_corrupt",
        LibraryLocked => "library_locked",
        NotFound => "not_found",
        PlayerNotRunning => "player_not_running",
        PlayerUnavailable => "player_unavailable",
        Cancelled => "cancelled",
        _ => "error",
    };

    public const string HelpText =
        "Exit codes: 0 ok, 1 error, 2 usage, 3 file_not_found, 4 unsupported_format, 5 decode_failed, " +
        "6 decoder_unavailable (FFmpeg missing), 7 audio_device_error, 8 invalid_state, 9 data_dir_not_writable, " +
        "10 library_corrupt, 11 library_locked (another MusicSpin process), 12 not_found, " +
        "13 player_not_running, 14 player_unavailable (background player could not be started/reached), 130 cancelled.";
}
