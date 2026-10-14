using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.Services;

/// <summary>Reaches the background player for the app's data folder.</summary>
public interface IPlayerConnector
{
    PlayerEndpoint Endpoint { get; }

    /// <summary>Connects to the running player, or starts one. <c>Started</c>: this call started it.</summary>
    Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(CancellationToken cancellationToken);

    /// <summary>Connects to the running player only. PlayerNotRunning when there is none.</summary>
    Task<PlayerClient> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The real player: the CLI's <c>music-spin player run</c>, started from the executable next to the app (the published
/// layout of the self-contained builds); in a development build, the one <c>MUSICSPIN_CLI</c> names.
/// </summary>
public sealed class BackgroundPlayerConnector(DataDirectory directory) : IPlayerConnector
{
    /// <summary>Development builds: the music-spin executable to start the player with (the CLI is in its own output folder).</summary>
    public const string CliVariable = "MUSICSPIN_CLI";

    public static TimeSpan StartTimeout { get; } = TimeSpan.FromSeconds(20);

    public PlayerEndpoint Endpoint { get; } = PlayerEndpoint.For(directory);

    public Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(CancellationToken cancellationToken)
        => PlayerConnector.ConnectOrStartAsync(Endpoint, _ => Task.FromResult<IPlayerProcess?>(Launch()), StartTimeout, cancellationToken: cancellationToken);

    public Task<PlayerClient> ConnectAsync(CancellationToken cancellationToken)
        => PlayerClient.ConnectAsync(Endpoint, cancellationToken: cancellationToken);

    private IPlayerProcess Launch()
    {
        AppPaths.EnsureWritable(directory);
        string[] args = ["player", "run", "--data-dir", directory.Path];
        var folder = AppContext.BaseDirectory;
        var exe = Environment.GetEnvironmentVariable(CliVariable) is { Length: > 0 } cli ? cli
            : Path.Combine(folder, OperatingSystem.IsWindows() ? "music-spin.exe" : "music-spin");
        if (File.Exists(exe))
        {
            return PlayerProcessLauncher.Start(Endpoint, args, exe);
        }

        throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
            $"The music-spin command-line tool is not at {exe}, so the app can't start the background player. In a development build, set {CliVariable} to the music-spin executable.");
    }
}
