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
        var source = new QueueSource();
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var volume = new Option<float?>("--volume")
        {
            Description = "Volume 0.0-1.0. 1.0 (default) leaves samples untouched (bit-perfect path).",
        };
        Options.Validated(volume, v => v is < 0f or > 1f ? "--volume must be between 0.0 and 1.0." : null);
        var start = new Option<double>("--start") { Description = "Start position in seconds (first track only)." };
        Options.Validated(start, v => v < 0 || !double.IsFinite(v) ? "--start must be 0 or greater." : null);
        var foreground = new Option<bool>("--foreground", "--wait")
        {
            Description = "Play in this process and block until done (the pre-background behaviour). Ctrl+C stops (exit 130).",
        };
        var duration = new Option<double?>("--duration")
        {
            Description = "Stop after this many seconds (implies --foreground). With a library queue: per track.",
        };

        var command = new Command("play",
            "Play file(s) or a library queue (--album / --artist / --genre / --search / --playlist [--shuffle]) in the background player and return " +
            "immediately with its status. Starts the player for this data folder if needed, or hands the queue to the running one. " +
            "Control it with pause/resume/toggle/stop/next/prev/seek/volume/status/queue/quit. --foreground keeps the old blocking mode.");
        source.AddTo(command);
        command.Add(backend);
        command.Add(device);
        command.Add(exclusive);
        command.Add(volume);
        command.Add(start);
        command.Add(foreground);
        command.Add(duration);
        command.Validators.Add(r =>
        {
            source.Validate(r);
            var files = Options.Get(r, source.Files) ?? [];
            var blocking = Options.Get(r, foreground) || Options.Get(r, duration) is not null;
            if (blocking && files.Length > 1)
            {
                r.AddError("--foreground/--duration play one file or a library queue; pass several files without them to queue them in the background player.");
            }
            else if (blocking && files.Length == 1 && Options.Get(r, source.Shuffle))
            {
                r.AddError("--shuffle/--seed only apply to queues.");
            }
            else if (blocking && Options.Get(r, source.Playlist) is not null)
            {
                r.AddError("--playlist plays in the background player; --foreground/--duration support files, --album, --artist, --genre and --search.");
            }
        });

        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var blocking = parseResult.GetValue(foreground) || parseResult.GetValue(duration) is not null;
            if (blocking)
            {
                return await ForegroundAsync(runner, parseResult, json, source, backend, device, exclusive, volume, start, duration, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Build the queue (and release the library) before talking to the player.
            // --shuffle is the player's shuffle mode (so 'shuffle off' restores the album order); same order as before for a seed.
            var resolved = source.Resolve(runner, parseResult, shuffle: false);
            var items = resolved.Items;
            var seed = QueueSource.PickSeed(parseResult.GetValue(source.Shuffle), parseResult.GetValue(source.Seed));
            var requestedBackend = parseResult.GetValue(backend)!;
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, requestedBackend,
                parseResult.GetValue(device), parseResult.GetValue(exclusive), cancellationToken).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var warnings = resolved.Warnings;
                if (!started)
                {
                    var info = await client.HelloAsync(cancellationToken).ConfigureAwait(false);
                    if (OutputDiffers(parseResult, backend, device, exclusive, info.Output))
                    {
                        warnings.Add($"The running player uses output '{info.Output.Backend}' ({info.Output.Device ?? "default device"}, " +
                            $"{(info.Output.Exclusive ? "exclusive" : "shared")}); --backend/--device/--exclusive apply when it starts. " +
                            "Run 'music-spin quit' first to change them.");
                    }
                }

                var startSeconds = parseResult.GetValue(start);
                var data = await client.SendAsync("queue.set", new
                {
                    items,
                    play = true,
                    startSeconds = startSeconds > 0 ? startSeconds : (double?)null,
                    shuffle = seed is not null ? true : (bool?)null,
                    seed,
                }, cancellationToken).ConfigureAwait(false);
                if (parseResult.GetValue(volume) is { } v)
                {
                    data = await client.SendAsync("volume", new { value = v }, cancellationToken).ConfigureAwait(false);
                }

                PlayerSupport.WriteStatus(runner, json, "play", data, new Dictionary<string, object?>
                {
                    ["mode"] = "background",
                    ["playerStarted"] = started,
                    ["seed"] = seed,
                    ["warnings"] = warnings.Count > 0 ? warnings : null,
                });
                return ExitCodes.Success;
            }
        });

        return command;
    }

    private static bool OutputDiffers(ParseResult parseResult, Option<string> backend, Option<string?> device, Option<bool> exclusive, OutputInfo running)
    {
        var backendGiven = parseResult.GetResult(backend) is { Implicit: false };
        var wantsNull = parseResult.GetValue(backend) == "null";
        var isNull = running.Backend.StartsWith("null", StringComparison.Ordinal);
        return (backendGiven && wantsNull != isNull)
            || (parseResult.GetValue(device) is { } d && !string.Equals(d, running.Device, StringComparison.Ordinal))
            || (parseResult.GetResult(exclusive) is { Implicit: false } && parseResult.GetValue(exclusive) != running.Exclusive);
    }

    private sealed record Settings(TimeSpan Start, TimeSpan? Limit, float Volume);

    private static async Task<int> ForegroundAsync(CommandRunner runner, ParseResult parseResult, bool json, QueueSource source,
        Option<string> backend, Option<string?> device, Option<bool> exclusive, Option<float?> volume, Option<double> start, Option<double?> duration,
        CancellationToken cancellationToken)
    {
        var env = runner.Env;
        var settings = new Settings(
            TimeSpan.FromSeconds(Math.Max(0, parseResult.GetValue(start))),
            parseResult.GetValue(duration) is { } limitSeconds && limitSeconds > 0 ? TimeSpan.FromSeconds(limitSeconds) : null,
            parseResult.GetValue(volume) ?? 1f);

        if (source.HasFiles(parseResult))
        {
            return await PlayFileAsync(runner, parseResult, json, parseResult.GetValue(source.Files)![0], settings, backend, device, exclusive, cancellationToken)
                .ConfigureAwait(false);
        }

        // Build the queue and release the library lock before playing.
        var warnings = new List<string>();
        var queue = source.LibraryTracks(runner, parseResult, warnings)!;
        var usedSeed = source.ApplyShuffle(parseResult, ref queue);

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
                foreach (var warning in warnings)
                {
                    env.Error.WriteLine($"music-spin play: warning: {warning}");
                }

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
                        env.Error.WriteLine($"music-spin play: skipping {entry.Path}: {ex.Message}");
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

        var result = new QueuePlayResultDto("queue", parseResult.GetValue(source.Album), parseResult.GetValue(source.Artist), usedSeed is not null, usedSeed,
            queue.Count, played, skipped, stopReason, warnings.Count > 0 ? warnings : null);
        if (stopReason == "cancelled")
        {
            return runner.Fail(json, "play", ExitCodes.Cancelled, "Playback cancelled.", result);
        }

        if (played.Count == 0)
        {
            return runner.Fail(json, "play", ExitCodes.FileNotFound,
                $"None of the {queue.Count} queued file(s) could be played (moved or deleted? run 'music-spin library scan').", result);
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
    }

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
