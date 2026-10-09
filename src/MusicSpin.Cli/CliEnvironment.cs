using MusicSpin.Core.Audio;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Core.Library;
using MusicSpin.Core.Output;
using MusicSpin.Core.Output.SoundFlow;

namespace MusicSpin.Cli;

/// <summary>Everything the CLI touches in the outside world, injectable for in-process tests.</summary>
public sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required TextReader In { get; init; }

    public required IAudioDecoderFactory Decoders { get; init; }

    /// <summary>Creates the audio output backend by name: "auto" (SoundFlow/miniaudio) or "null".</summary>
    public required Func<string, IAudioOutputProvider> OutputProviders { get; init; }

    /// <summary>FFmpeg load status, for `doctor`/`formats`.</summary>
    public Func<FFmpegStatus?> FFmpegStatus { get; init; } = () => FFmpegRuntime.Status;

    /// <summary>Environment variable accessor (MUSICSPIN_DATA_DIR, ...).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Executable directory used for the portable <c>.usrdata</c> folder.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>How long to wait for another MusicSpin process to release the library.</summary>
    public TimeSpan LibraryLockTimeout { get; init; } = MusicLibrary.DefaultLockTimeout;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public static CliEnvironment CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        In = Console.In,
        Decoders = new FFmpegDecoderFactory(),
        OutputProviders = CreateOutputProvider,
    };

    public static IAudioOutputProvider CreateOutputProvider(string backend) => backend switch
    {
        "null" => new NullAudioOutputProvider(realtime: true),
        _ => new SoundFlowOutputProvider(),
    };
}
