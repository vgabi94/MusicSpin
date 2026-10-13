using MusicSpin.App.Services;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// A real background player (<see cref="PlayerHost"/>) in the test process, on the real transport, with fake decoders
/// (<see cref="Decoders"/>: register the paths a test plays) and a silent output that runs in real time. It starts when
/// the app first connects, as <c>music-spin player run</c> would, unless <see cref="AllowStart"/> is false.
/// </summary>
internal sealed class InProcessPlayer(DataDirectory data) : IPlayerConnector, IAsyncDisposable
{
    private readonly Lock _gate = new();

    public PlayerEndpoint Endpoint { get; } = PlayerEndpoint.For(data);

    public FakeDecoderFactory Decoders { get; } = new();

    /// <summary>The running host, or null.</summary>
    public PlayerHost? Host { get; private set; }

    /// <summary>How many times the app started the player.</summary>
    public int AppStarts { get; private set; }

    /// <summary>False: the app can't start a player (as when the CLI is missing), only connect to a running one.</summary>
    public bool AllowStart { get; set; } = true;

    /// <summary>Adds fake audio for <paramref name="paths"/> (each <paramref name="seconds"/> long).</summary>
    public void AddTracks(IEnumerable<string> paths, double seconds = 30)
    {
        foreach (var path in paths)
        {
            Decoders.With(TestAudio.Track(path, sampleRate: 8000, seconds: seconds));
        }
    }

    /// <summary>Starts the player as another program would (the CLI); the app didn't start it.</summary>
    public PlayerHost Start()
    {
        lock (_gate)
        {
            if (Host is { } running && !running.Completion.IsCompleted)
            {
                return running;
            }

            Host = PlayerHost.TryStart(new PlayerHostOptions
            {
                DataDirectory = data,
                Decoders = Decoders,
                Outputs = Outputs.Realtime(),
                IdleTimeout = null,
                PersistQueue = false,
                ResolveFromLibrary = false,
            }) ?? throw new InvalidOperationException("Another player holds the data folder.");
            return Host;
        }
    }

    /// <summary>Stops the player (a crash or a quit from elsewhere): the app loses its connections.</summary>
    public async Task StopAsync()
    {
        PlayerHost? host;
        lock (_gate)
        {
            (host, Host) = (Host, null);
        }

        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>A separate client, as the CLI would use.</summary>
    public Task<PlayerClient> ConnectOtherAsync() => PlayerClient.ConnectAsync(Endpoint, cancellationToken: TestContext.Current.CancellationToken);

    public Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(CancellationToken cancellationToken)
        => PlayerConnector.ConnectOrStartAsync(Endpoint, _ =>
        {
            if (!AllowStart)
            {
                throw new Core.MusicSpinException(Core.MusicSpinErrorCode.PlayerUnavailable, "The test doesn't let the app start a player.");
            }

            Start();
            AppStarts++;
            return Task.FromResult<IPlayerProcess?>(null);
        }, TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

    public Task<PlayerClient> ConnectAsync(CancellationToken cancellationToken)
        => PlayerClient.ConnectAsync(Endpoint, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync() => await StopAsync();
}
