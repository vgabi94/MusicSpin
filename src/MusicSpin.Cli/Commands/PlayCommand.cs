using System.CommandLine;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Library;
using MusicSpin.Core.Metadata;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Commands;

internal static class PlayCommand
{
    public static Command Create(CommandRunner runner)
    {
        var file = new Argument<string?>("file") { Description = "Audio file to play (or use --album / --artist to play from the library).", Arity = ArgumentArity.ZeroOrOne };
        var album = new Option<string?>("--album") { Description = "Play an album from the library (title, case-insensitive, or album id). Combine with --artist to disambiguate." };
        var artist = new Option<string?>("--artist") { Description = "Play all library tracks by an artist (albums by year), or narrow --album." };
        var shuffle = new Option<bool>("--shuffle") { Description = "Shuffle the library queue." };
        var seed = new Option<int?>("--seed") { Description = "Shuffle seed for a reproducible order (default: random, reported in the JSON result)." };
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var volume = new Option<float>("--volume")
        {
            Description = "Volume 0.0-1.0. 1.0 (default) leaves samples untouched (bit-perfect path).",
            DefaultValueFactory = _ => 1f,
        };
        Options.Validated(volume, v => v is < 0f or > 1f ? "--volume must be between 0.0 and 1.0." : null);
        var start = new Option<double>("--start") { Description = "Start position in seconds (first track only)." };
        var duration = new Option<double?>("--duration") { Description = "Stop after this many seconds (default: play to the end). With a library queue: per track." };

        var command = new Command("play",
            "Play a file, or a library queue (--album / --artist [--shuffle]), to the end (blocks). Ctrl+C stops (exit 130). " +
            "Queue: unplayable/missing files are skipped and reported.")
        {
            file, album, artist, shuffle, seed, backend, device, exclusive, volume, start, duration,
        };
        command.Validators.Add(r =>
        {
            var hasFile = !string.IsNullOrWhiteSpace(Options.Get(r, file));
            var hasLibrary = Options.Get(r, album) is not null || Options.Get(r, artist) is not null;
            if (hasFile == hasLibrary)
            {
                r.AddError(hasFile
                    ? "Give either a file or --album/--artist, not both."
                    : "Nothing to play: give a file, --album <title> or --artist <name>.");
            }
            else if (hasFile && (Options.Get(r, shuffle) || Options.Get(r, seed) is not null))
            {
                r.AddError("--shuffle/--seed only apply to --album/--artist queues.");
            }
        });

        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var env = runner.Env;
            var settings = new Settings(
                TimeSpan.FromSeconds(Math.Max(0, parseResult.GetValue(start))),
                parseResult.GetValue(duration) is { } limitSeconds && limitSeconds > 0 ? TimeSpan.FromSeconds(limitSeconds) : null,
                parseResult.GetValue(volume));

            if (parseResult.GetValue(file) is { Length: > 0 } path)
            {
                return await PlayFileAsync(runner, parseResult, json, path, settings, backend, device, exclusive, cancellationToken).ConfigureAwait(false);
            }

            // Build the queue and release the library lock before playing.
            IReadOnlyList<TrackRecord> queue;
            using (var library = runner.OpenLibraryForRead(parseResult))
            {
                queue = parseResult.GetValue(album) is { } albumName
                    ? library.AlbumQueue(albumName, parseResult.GetValue(artist))
                    : library.ArtistQueue(parseResult.GetValue(artist)!);
            }

            int? usedSeed = null;
            if (parseResult.GetValue(shuffle))
            {
#pragma warning disable CA5394 // not security sensitive
                usedSeed = parseResult.GetValue(seed) ?? Random.Shared.Next();
#pragma warning restore CA5394
                queue = MusicLibrary.Shuffle(queue, usedSeed.Value);
            }

            var provider = env.OutputProviders(parseResult.GetValue(backend)!);
            var options = new PlayerOptions { Output = new AudioOutputOptions(parseResult.GetValue(device), parseResult.GetValue(exclusive)) };
            using var player = new Player(env.Decoders, provider, options);
            var played = new List<QueueEntryDto>();
            var skipped = new List<QueueSkipDto>();
            var stopReason = "ended";
            try
            {
                if (!json)
                {
                    env.Out.WriteLine($"Queue: {queue.Count} track(s){(usedSeed is { } s ? $", shuffled (seed {s})" : string.Empty)} via {provider.Name}");
                }

                for (var i = 0; i < queue.Count; i++)
                {
                    var entry = queue[i];
                    TrackInfo info;
                    try
                    {
                        info = player.Load(entry.Path);
                    }
                    catch (MusicSpinException ex) when (ex.Code is MusicSpinErrorCode.FileNotFound or MusicSpinErrorCode.UnsupportedFormat or MusicSpinErrorCode.DecodeFailed)
                    {
                        skipped.Add(new QueueSkipDto(entry.Path, ExitCodes.Name(ExitCodes.From(ex.Code)), ex.Message));
                        if (!json)
                        {
                            env.Error.WriteLine($"musicspin play: skipping {entry.Path}: {ex.Message}");
                        }

                        continue;
                    }

                    if (!json)
                    {
                        env.Out.WriteLine($"[{i + 1}/{queue.Count}] {Title(info)}  ({Human.Describe(info)})");
                    }

                    var (seconds, reason) = await PlayLoadedAsync(player, info, i == 0 ? settings : settings with { Start = TimeSpan.Zero }, cancellationToken)
                        .ConfigureAwait(false);
                    played.Add(new QueueEntryDto(LibraryTrackDto.From(entry), seconds, reason));
                    if (reason == "cancelled")
                    {
                        stopReason = "cancelled";
                        break;
                    }
                }
            }
            finally
            {
                player.Dispose();
                (provider as IDisposable)?.Dispose();
            }

            var result = new QueuePlayResultDto("queue", parseResult.GetValue(album), parseResult.GetValue(artist), usedSeed is not null, usedSeed,
                queue.Count, played, skipped, stopReason);
            if (stopReason == "cancelled")
            {
                return runner.Fail(json, "play", ExitCodes.Cancelled, "Playback cancelled.", result);
            }

            if (played.Count == 0)
            {
                return runner.Fail(json, "play", ExitCodes.FileNotFound,
                    $"None of the {queue.Count} queued file(s) could be played (moved or deleted? run 'musicspin library scan').", result);
            }

            if (json)
            {
                JsonOutput.WriteSuccess(env.Out, "play", result);
            }
            else
            {
                env.Out.WriteLine($"Played {played.Count} of {queue.Count} track(s){(skipped.Count > 0 ? $", skipped {skipped.Count}" : string.Empty)}.");
            }

            return ExitCodes.Success;
        });

        return command;
    }

    private sealed record Settings(TimeSpan Start, TimeSpan? Limit, float Volume);

    private static async Task<int> PlayFileAsync(CommandRunner runner, ParseResult parseResult, bool json, string path, Settings settings,
        Option<string> backend, Option<string?> device, Option<bool> exclusive, CancellationToken cancellationToken)
    {
        var env = runner.Env;
        var provider = env.OutputProviders(parseResult.GetValue(backend)!);
        var options = new PlayerOptions { Output = new AudioOutputOptions(parseResult.GetValue(device), parseResult.GetValue(exclusive)) };
        using var player = new Player(env.Decoders, provider, options);
        try
        {
            var track = player.Load(path);
            if (!json)
            {
                env.Out.WriteLine($"Playing {Title(track)}");
                env.Out.WriteLine($"  {Human.Describe(track)} via {provider.Name}");
            }

            var (played, reason) = await PlayLoadedAsync(player, track, settings, cancellationToken).ConfigureAwait(false);
            if (reason == "cancelled")
            {
                return runner.Fail(json, "play", ExitCodes.Cancelled, "Playback cancelled.");
            }

            if (json)
            {
                JsonOutput.WriteSuccess(env.Out, "play", new PlayResultDto(TrackDto.From(track), settings.Start.TotalSeconds, played, reason));
            }
            else
            {
                env.Out.WriteLine($"Stopped ({reason}) after {played:0.0}s.");
            }

            return ExitCodes.Success;
        }
        finally
        {
            player.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <returns>Seconds played and the stop reason: ended | duration | cancelled.</returns>
    private static async Task<(double Seconds, string Reason)> PlayLoadedAsync(Player player, TrackInfo track, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Start > TimeSpan.Zero)
        {
            player.Seek(settings.Start);
        }

        player.Volume = settings.Volume;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded(object? sender, EventArgs e) => ended.TrySetResult();
        player.PlaybackEnded += OnEnded;
        try
        {
            player.Play();
            var limit = settings.Limit is { } l ? Task.Delay(l, cancellationToken) : Task.Delay(Timeout.Infinite, cancellationToken);
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

            var played = (player.State == PlayerState.Stopped && reason == "ended" ? track.Duration : player.Position) - settings.Start;
            player.Stop();
            return (Math.Round(Math.Max(0, played.TotalSeconds), 3), reason);
        }
        finally
        {
            player.PlaybackEnded -= OnEnded;
        }
    }

    private static string Title(TrackInfo track)
        => track.Title is { } title
            ? (track.Artist is { } artist ? $"{artist} - {title}" : title)
            : Path.GetFileName(track.Path);
}
