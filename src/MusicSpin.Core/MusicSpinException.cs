namespace MusicSpin.Core;

/// <summary>Machine-readable error categories. Front-ends (CLI, app) map these to exit codes / messages.</summary>
public enum MusicSpinErrorCode
{
    Unknown = 0,
    FileNotFound,
    UnsupportedFormat,
    DecodeFailed,
    DecoderUnavailable,
    AudioDeviceError,
    InvalidState,
}

/// <summary>Exception carrying a <see cref="MusicSpinErrorCode"/> so callers can react without parsing messages.</summary>
public class MusicSpinException : Exception
{
    public MusicSpinException() : this(MusicSpinErrorCode.Unknown, "Unknown MusicSpin error.") { }

    public MusicSpinException(string message) : this(MusicSpinErrorCode.Unknown, message) { }

    public MusicSpinException(string message, Exception innerException)
        : this(MusicSpinErrorCode.Unknown, message, innerException) { }

    public MusicSpinException(MusicSpinErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public MusicSpinErrorCode Code { get; }
}
