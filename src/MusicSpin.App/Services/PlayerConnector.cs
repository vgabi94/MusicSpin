using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.Services;

/// <summary>
/// The output a player the app starts plays through (Settings › Audio, saved as <c>outputDevice</c> and
/// <c>exclusiveMode</c>): a device by its name (null: the system default), opened exclusively or shared.
/// </summary>
public sealed record PlayerOutput(string? Device, bool Exclusive)
{
    public static readonly PlayerOutput Default = new(null, false);

    public static PlayerOutput From(MusicSpin.Core.Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PlayerOutput(string.IsNullOrWhiteSpace(settings.OutputDevice) ? null : settings.OutputDevice, settings.ExclusiveMode);
    }
}

/// <summary>Reaches the background player for the app's data folder.</summary>
public interface IPlayerConnector
{
    PlayerEndpoint Endpoint { get; }

    /// <summary>Connects to the running player, or starts one. <c>Started</c>: this call started it.</summary>
    Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(CancellationToken cancellationToken);

    /// <summary>As <see cref="ConnectOrStartAsync(CancellationToken)"/>; a player it starts plays through <paramref name="output"/>.</summary>
    Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(PlayerOutput output, CancellationToken cancellationToken)
        => ConnectOrStartAsync(cancellationToken);

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
        => ConnectOrStartAsync(PlayerOutput.Default, cancellationToken);

    public Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(PlayerOutput output, CancellationToken cancellationToken)
        => PlayerConnector.ConnectOrStartAsync(Endpoint, _ => Task.FromResult<IPlayerProcess?>(Launch(output)), StartTimeout, cancellationToken: cancellationToken);

    public Task<PlayerClient> ConnectAsync(CancellationToken cancellationToken)
        => PlayerClient.ConnectAsync(Endpoint, cancellationToken: cancellationToken);

    private IPlayerProcess Launch(PlayerOutput output)
    {
        AppPaths.EnsureWritable(directory);
        List<string> args = ["player", "run", "--data-dir", directory.Path];
        if (output.Device is { } device)
        {
            args.AddRange(["--device", device]);
        }

        if (output.Exclusive)
        {
            args.Add("--exclusive");
        }

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
