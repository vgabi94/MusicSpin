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

    /// <summary>The data folder (next to the executable, or the override) cannot be created or written.</summary>
    DataDirectoryNotWritable,

    /// <summary>The library database file is corrupt or not a MusicSpin/LiteDB database.</summary>
    LibraryCorrupt,

    /// <summary>The library database is in use by another MusicSpin process.</summary>
    LibraryLocked,

    /// <summary>A requested library item (folder, album, artist) does not exist.</summary>
    NotFound,

    /// <summary>The request is invalid or ambiguous (maps to the CLI usage exit code).</summary>
    InvalidArgument,
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
