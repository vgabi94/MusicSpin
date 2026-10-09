using MusicSpin.Core;

namespace MusicSpin.Cli;

/// <summary>CLI view of <see cref="Core.ExitCodes"/> (kept so existing callers and tests compile unchanged).</summary>
public static class ExitCodes
{
    public const int Success = Core.ExitCodes.Success;
    public const int Error = Core.ExitCodes.Error;
    public const int Usage = Core.ExitCodes.Usage;
    public const int FileNotFound = Core.ExitCodes.FileNotFound;
    public const int UnsupportedFormat = Core.ExitCodes.UnsupportedFormat;
    public const int DecodeFailed = Core.ExitCodes.DecodeFailed;
    public const int DecoderUnavailable = Core.ExitCodes.DecoderUnavailable;
    public const int AudioDeviceError = Core.ExitCodes.AudioDeviceError;
    public const int InvalidState = Core.ExitCodes.InvalidState;
    public const int DataDirNotWritable = Core.ExitCodes.DataDirNotWritable;
    public const int LibraryCorrupt = Core.ExitCodes.LibraryCorrupt;
    public const int LibraryLocked = Core.ExitCodes.LibraryLocked;
    public const int NotFound = Core.ExitCodes.NotFound;
    public const int PlayerNotRunning = Core.ExitCodes.PlayerNotRunning;
    public const int PlayerUnavailable = Core.ExitCodes.PlayerUnavailable;
    public const int Cancelled = Core.ExitCodes.Cancelled;

    public const string HelpText = Core.ExitCodes.HelpText;

    public static int From(MusicSpinErrorCode code) => Core.ExitCodes.From(code);

    public static string Name(int exitCode) => Core.ExitCodes.Name(exitCode);
}
