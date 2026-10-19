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

    /// <summary>
    /// Starts a background player for a data folder. Default: spawn this executable as a detached
    /// <c>music-spin player run</c> process. Tests inject an in-process <see cref="PlayerHost"/>.
    /// </summary>
    public Func<PlayerLaunchRequest, CancellationToken, Task<IPlayerProcess?>>? PlayerLauncher { get; init; }

    /// <summary>
    /// Starts the tray icon for a new background player (#83). Default: <see cref="TrayLauncher.ForThisProcess"/> with
    /// <see cref="GetEnvironmentVariable"/>. Tests inject one with a fake environment and process start.
    /// </summary>
    public TrayLauncher? TrayLauncher { get; init; }

    /// <summary>How long `play` waits for a newly started background player to accept connections.</summary>
    public TimeSpan PlayerStartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a control command waits for the player's answer.</summary>
    public TimeSpan PlayerRequestTimeout { get; init; } = PlayerClient.DefaultRequestTimeout;

    /// <summary>Most library tracks a --search/--title/--album-contains/--artist-contains source takes (play, queue add, playlist add). Tests lower it.</summary>
    public int SearchSourceLimit { get; init; } = Paging.MaxLimit;

    /// <summary>Byte streams for `mcp` (MCP over stdio). Default: the process's stdin/stdout. Tests inject pipes.</summary>
    public Func<(Stream Input, Stream Output)>? McpStreams { get; init; }

    /// <summary>Process-level signals for `player run` (SIGTERM/SIGINT/SIGHUP); off in tests.</summary>
    public bool HandleSignals { get; init; }

    public static CliEnvironment CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        In = Console.In,
        Decoders = new FFmpegDecoderFactory(),
        OutputProviders = CreateOutputProvider,
        HandleSignals = true,
    };

    public static IAudioOutputProvider CreateOutputProvider(string backend) => backend switch
    {
        "null" => new NullAudioOutputProvider(realtime: true),
        _ => new SoundFlowOutputProvider(),
    };
}

/// <summary>What `play`/`queue add` need to start a background player.</summary>
/// <param name="Arguments">Command line for the new process, e.g. <c>player run --data-dir X --backend null</c>.</param>
public sealed record PlayerLaunchRequest(
    MusicSpin.Core.Hosting.DataDirectory DataDirectory,
    PlayerEndpoint Endpoint,
    IReadOnlyList<string> Arguments,
    string Backend,
    string? Device,
    bool Exclusive);
