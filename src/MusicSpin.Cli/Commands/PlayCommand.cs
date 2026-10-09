using System.CommandLine;
using MusicSpin.Cli.Output;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Commands;

internal static class PlayCommand
{
    public static Command Create(CommandRunner runner)
    {
        var file = new Argument<string>("file") { Description = "Audio file to play." };
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var volume = new Option<float>("--volume")
        {
            Description = "Volume 0.0-1.0. 1.0 (default) leaves samples untouched (bit-perfect path).",
            DefaultValueFactory = _ => 1f,
        };
        volume.Validators.Add(r =>
        {
            var v = r.GetValueOrDefault<float>();
            if (v is < 0f or > 1f)
            {
                r.AddError("--volume must be between 0.0 and 1.0.");
            }
        });
        var start = new Option<double>("--start") { Description = "Start position in seconds." };
        var duration = new Option<double?>("--duration") { Description = "Stop after this many seconds (default: play to the end)." };

        var command = new Command("play", "Play a file to the end (blocks). Ctrl+C stops (exit 130).")
        {
            file, backend, device, exclusive, volume, start, duration,
        };

        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var env = runner.Env;
            var provider = env.OutputProviders(parseResult.GetValue(backend)!);
            var options = new PlayerOptions { Output = new AudioOutputOptions(parseResult.GetValue(device), parseResult.GetValue(exclusive)) };
            using var player = new Player(env.Decoders, provider, options);
            try
            {
                var track = player.Load(parseResult.GetValue(file)!);
                var startAt = TimeSpan.FromSeconds(Math.Max(0, parseResult.GetValue(start)));
                if (startAt > TimeSpan.Zero)
                {
                    player.Seek(startAt);
                }

                player.Volume = parseResult.GetValue(volume);

                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackEnded += (_, _) => ended.TrySetResult();

                if (!json)
                {
                    env.Out.WriteLine($"Playing {Title(track)}");
                    env.Out.WriteLine($"  {Human.Describe(track)} via {provider.Name}");
                }

                player.Play();

                var limit = parseResult.GetValue(duration) is { } seconds && seconds > 0
                    ? Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken)
                    : Task.Delay(Timeout.Infinite, cancellationToken);

                string reason;
                try
                {
                    var finished = await Task.WhenAny(ended.Task, limit).ConfigureAwait(false);
                    await finished.ConfigureAwait(false);
                    reason = finished == ended.Task ? "ended" : "duration";
                }
                catch (OperationCanceledException)
                {
                    reason = "cancelled";
                }

                var played = (player.State == PlayerState.Stopped && reason == "ended" ? track.Duration : player.Position) - startAt;
                player.Stop();

                if (reason == "cancelled")
                {
                    return runner.Fail(json, "play", ExitCodes.Cancelled, "Playback cancelled.");
                }

                if (json)
                {
                    JsonOutput.WriteSuccess(env.Out, "play",
                        new PlayResultDto(TrackDto.From(track), startAt.TotalSeconds, Math.Round(Math.Max(0, played.TotalSeconds), 3), reason));
                }
                else
                {
                    env.Out.WriteLine($"Stopped ({reason}) after {played.TotalSeconds:0.0}s.");
                }

                return ExitCodes.Success;
            }
            finally
            {
                player.Dispose();
                (provider as IDisposable)?.Dispose();
            }
        });

        return command;
    }

    private static string Title(Core.Metadata.TrackInfo track)
        => track.Title is { } title
            ? (track.Artist is { } artist ? $"{artist} - {title}" : title)
            : Path.GetFileName(track.Path);
}
