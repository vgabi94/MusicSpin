using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Core.Output;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Runs the CLI in-process with fakes and captures output. Background players run in-process too.</summary>
internal sealed class CliHarness : IAsyncDisposable
{
    private readonly List<PlayerHost> _hosts = [];

    /// <summary>A fake (decoder-only) song, rooted the way the CLI normalizes paths on this OS (D:\music\... on Windows).</summary>
    public static readonly string Song = Path.GetFullPath("/music/song.flac");

    public FakeDecoderFactory Decoders { get; init; } = new FakeDecoderFactory().With(TestAudio.Track(Song, sampleRate: 96_000, seconds: 0.2));

    /// <summary>Replaces <see cref="Decoders"/> for the CLI itself (fault injection), when set.</summary>
    public IAudioDecoderFactory? DecoderOverride { get; init; }

    public Func<string, IAudioOutputProvider> Outputs { get; init; } = _ => new NullAudioOutputProvider(realtime: false);

    public FFmpegStatus? FFmpeg { get; init; } = new(true, "9.0-test", "LGPL version 2.1 or later", "", new Dictionary<string, int>(), null);

    public string Input { get; init; } = string.Empty;

    /// <summary>Pretend executable directory (portable data goes to &lt;BaseDirectory&gt;/.usrdata). Unique per harness.</summary>
    public string BaseDirectory { get; init; } = TempDir.Create("exe");

    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    public string DataDir => Path.Combine(BaseDirectory, ".usrdata");

    /// <summary>How `play` starts a background player. Default: an in-process <see cref="PlayerHost"/> (see <see cref="Hosts"/>).</summary>
    public Func<PlayerLaunchRequest, CancellationToken, Task<IPlayerProcess?>>? Launcher { get; set; }

    /// <summary>Idle timeout of in-process players (a safety net against leaks; tests dispose the harness).</summary>
    public TimeSpan? PlayerIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PlayerStartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long control commands wait for the player's answer (short in tests of an unresponsive player).</summary>
    public TimeSpan PlayerRequestTimeout { get; init; } = MusicSpin.Core.Remote.PlayerClient.DefaultRequestTimeout;

    public Func<(Stream Input, Stream Output)>? McpStreams { get; set; }

    /// <summary>Most tracks a --search source takes (lowered to test the cut-off warning without 10,000 files).</summary>
    public int SearchSourceLimit { get; set; } = MusicSpin.Core.Library.Paging.MaxLimit;

    public int Launches => Volatile.Read(ref _launches);

    private int _launches;

    public IReadOnlyList<PlayerHost> Hosts
    {
        get { lock (_hosts) { return [.. _hosts]; } }
    }

    public Task<IPlayerProcess?> LaunchInProcess(PlayerLaunchRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _launches);
        var settings = new SettingsStore(request.DataDirectory).Load(); // like `player run`: modes default from settings.json
        var host = PlayerHost.TryStart(new PlayerHostOptions
        {
            DataDirectory = request.DataDirectory,
            Decoders = Decoders,
            Outputs = Outputs(request.Backend),
            Output = new AudioOutputOptions(request.Device, request.Exclusive),
            IdleTimeout = PlayerIdleTimeout,
            InitialRepeat = PlaybackModes.TryParseRepeat(settings.Repeat, out var repeat) ? repeat : RepeatMode.Off,
            InitialShuffle = settings.Shuffle,
        });
        if (host is not null)
        {
            lock (_hosts)
            {
                _hosts.Add(host);
            }
        }

        return Task.FromResult<IPlayerProcess?>(null);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Hosts)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>Stdout of the command currently running (thread-safe snapshot via <see cref="LockedStringWriter.Snapshot"/>).</summary>
    public LockedStringWriter? LiveOut { get; private set; }

    public Task<CliResult> RunAsync(params string[] args) => RunAsync(TestContext.Current.CancellationToken, args);

    public async Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new LockedStringWriter();
        using var stderr = new LockedStringWriter();
        LiveOut = stdout;
        var env = new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            In = new StringReader(Input),
            Decoders = DecoderOverride ?? Decoders,
            OutputProviders = Outputs,
            FFmpegStatus = () => FFmpeg,
            BaseDirectory = BaseDirectory,
            GetEnvironmentVariable = name => EnvironmentVariables.GetValueOrDefault(name),
            LibraryLockTimeout = LockTimeout,
            PlayerLauncher = Launcher ?? LaunchInProcess,
            PlayerStartTimeout = PlayerStartTimeout,
            PlayerRequestTimeout = PlayerRequestTimeout,
            McpStreams = McpStreams,
            SearchSourceLimit = SearchSourceLimit,
        };

        var exitCode = await CliApp.RunAsync(args, env, cancellationToken);
        return new CliResult(exitCode, stdout.Snapshot(), stderr.Snapshot());
    }
}

internal sealed record CliResult(int ExitCode, string Out, string Err)
{
    /// <summary>Parses stdout as exactly one JSON document.</summary>
    public JsonElement Json()
    {
        var lines = Lines();
        Assert.True(lines.Length == 1, $"Expected one JSON line, got {lines.Length}:\n{Out}");
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    public string[] Lines() => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public JsonElement[] JsonLines() => [.. Lines().Select(l => JsonDocument.Parse(l).RootElement.Clone())];
}
