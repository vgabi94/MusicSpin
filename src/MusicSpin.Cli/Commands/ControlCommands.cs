using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;

namespace MusicSpin.Cli.Commands;

/// <summary>Quick commands that talk to the background player and return immediately.</summary>
internal static class ControlCommands
{
    public static IEnumerable<Command> Create(CommandRunner runner)
    {
        yield return Simple(runner, "pause", "pause", "Pause playback (no-op when not playing).");
        yield return Resume(runner);
        yield return Simple(runner, "toggle", "toggle", "Pause when playing, otherwise resume.");
        yield return Simple(runner, "stop", "stop", "Stop and rewind the current track; the queue is kept. The player exits after the idle timeout.");
        yield return Simple(runner, "next", "next", "Play the next queue entry (skipping unplayable files). Exit 8 at the end of the queue.");
        yield return Simple(runner, "prev", "prev", "Play the previous queue entry. Exit 8 on the first entry (use 'seek 0' to restart).");
        yield return Seek(runner);
        yield return Volume(runner);
        yield return Repeat(runner);
        yield return Shuffle(runner);
        yield return Simple(runner, "status", "status",
            "Player status: state, track (codec, sample rate, bit depth, tags), position, duration, volume, queue index/length, output device, exclusive mode.");
        yield return Queue(runner);
        yield return Quit(runner, new Command("quit", "Shut the background player down (stops playback, removes its endpoint)."));
        yield return Player(runner);
    }

    private static Command Resume(CommandRunner runner)
    {
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var command = new Command("resume",
            "Resume (or start) playback of the current queue entry; after the queue ended, continue with newly added entries or restart. " +
            "Without a running player: starts one if a saved queue exists (it continues where the last player stopped), otherwise exit 13. " +
            "--backend/--device/--exclusive apply only when the player is started.")
        {
            backend, device, exclusive,
        };
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var (data, started) = await PlayerSupport.ResumeAsync(runner, parseResult, parseResult.GetValue(backend)!,
                parseResult.GetValue(device), parseResult.GetValue(exclusive), cancellationToken).ConfigureAwait(false);
            PlayerSupport.WriteStatus(runner, json, "resume", data, started ? new Dictionary<string, object?> { ["playerStarted"] = true } : null);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Simple(CommandRunner runner, string name, string cmd, string description)
    {
        var command = new Command(name, description);
        PlayerSupport.Control(runner, command, cmd);
        return command;
    }

    private static Command Seek(CommandRunner runner)
    {
        var position = new Argument<string>("position")
        {
            Description = "Seconds (90), mm:ss (1:30), h:mm:ss, or relative: +10, -10, +0:30.",
        };
        var command = new Command("seek", "Seek within the current track. Absolute positions must be within the track (exit 2 otherwise); relative ones are clamped.")
        {
            position,
        };
        command.Validators.Add(r =>
        {
            if (Options.Get(r, position) is { } text && !SeekSyntax.TryParse(text, out _, out _, out var error))
            {
                r.AddError(error!);
            }
        });
        PlayerSupport.Control(runner, command, "seek", p =>
        {
            if (!SeekSyntax.TryParse(p.GetValue(position)!, out var seconds, out var relative, out var error))
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error!);
            }

            return relative ? new { delta = seconds } : (object)new { seconds };
        });
        return command;
    }

    private static Command Volume(CommandRunner runner)
    {
        var level = new Argument<string>("level")
        {
            Description = "0..1 (0.5), percent (50%), or a change: +0.1, -10%. 1.0 = bit-perfect.",
        };
        var command = new Command("volume", "Set or change the volume.") { level };
        command.Validators.Add(r =>
        {
            if (Options.Get(r, level) is { } text && !PlayerSupport.TryParseVolume(text, out _, out _, out var error))
            {
                r.AddError(error!);
            }
        });
        PlayerSupport.Control(runner, command, "volume", p =>
        {
            if (!PlayerSupport.TryParseVolume(p.GetValue(level)!, out var value, out var relative, out var error))
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error!);
            }

            return relative ? new { delta = value } : (object)new { value };
        });
        return command;
    }

    private static Command Repeat(CommandRunner runner)
    {
        var mode = new Argument<string?>("mode")
        {
            Description = "off | one | all. Without a mode: show the current one.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("repeat",
            "Show or set the repeat mode: off (play the queue once), one (a track that ends starts again; next/prev still move), " +
            "all (loop the queue; next/prev wrap around). The new mode is also saved as the default in settings.json.")
        {
            mode,
        };
        command.Validators.Add(r =>
        {
            if (Options.Get(r, mode) is { } text && !PlaybackModes.TryParseRepeat(text, out _))
            {
                r.AddError($"Unknown repeat mode '{text}'. Use {PlaybackModes.RepeatValues}.");
            }
        });
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var text = parseResult.GetValue(mode);
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var data = await client.SendAsync("repeat", text is null ? null : new { mode = text.Trim().ToLowerInvariant() }, cancellationToken)
                .ConfigureAwait(false);
            var warning = text is null ? null : PlayerSupport.SaveModes(runner, parseResult, data);
            PlayerSupport.WriteStatus(runner, json, "repeat", data, new Dictionary<string, object?>
            {
                ["warnings"] = warning is null ? null : new[] { warning },
            });
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Shuffle(CommandRunner runner)
    {
        var mode = new Argument<string?>("mode")
        {
            Description = "on | off | toggle. Without a mode: show the current one.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var seed = new Option<int?>("--seed") { Description = "Seed for a reproducible shuffle (with 'on' or 'toggle' turning it on)." };
        var command = new Command("shuffle",
            "Show or change shuffle on the running queue. 'on' shuffles the entries after the current track (it keeps playing; " +
            "again when already on), 'off' restores the original order at the current track. New queues follow the mode. " +
            "Saved as the default in settings.json.")
        {
            mode, seed,
        };
        command.Validators.Add(r =>
        {
            var text = Options.Get(r, mode);
            if (text is not null && !PlaybackModes.TryParseShuffle(text, false, out _))
            {
                r.AddError($"Unknown shuffle mode '{text}'. Use on|off|toggle.");
            }
            else if (Options.Get(r, seed) is not null && text?.Trim().ToLowerInvariant() is not ("on" or "true" or "1" or "yes" or "toggle"))
            {
                r.AddError("--seed only applies when turning shuffle on ('shuffle on --seed N').");
            }
        });
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var text = parseResult.GetValue(mode);
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var data = await client.SendAsync("shuffle", text is null ? null : new { mode = text.Trim().ToLowerInvariant(), seed = parseResult.GetValue(seed) },
                cancellationToken).ConfigureAwait(false);
            var warning = text is null ? null : PlayerSupport.SaveModes(runner, parseResult, data);
            PlayerSupport.WriteStatus(runner, json, "shuffle", data, new Dictionary<string, object?>
            {
                ["warnings"] = warning is null ? null : new[] { warning },
            });
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Queue(CommandRunner runner)
    {
        var limit = Options.Limit();
        var offset = Options.Offset();
        var queue = new Command("queue", "List the player's queue (index, current entry, up-next entries, path, tags). Subcommands: add, remove, move, clear, save.") { limit, offset };
        runner.SetAction(queue, async (parseResult, json, cancellationToken) =>
        {
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var data = await client.SendAsync("queue", new { offset = parseResult.GetValue(offset), limit = parseResult.GetValue(limit) }, cancellationToken)
                .ConfigureAwait(false);
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "queue", data);
            }
            else
            {
                var listing = ProtocolJson.Deserialize<QueueListing>(data)!;
                var upNext = listing.Items.Count(e => e.UpNext);
                runner.Env.Out.WriteLine($"Queue: {listing.Total} entr{(listing.Total == 1 ? "y" : "ies")}{(upNext > 0 ? " (+ = up next)" : string.Empty)}");
                foreach (var e in listing.Items)
                {
                    var title = e.Item.Title ?? Path.GetFileName(e.Item.Path);
                    runner.Env.Out.WriteLine($"{(e.Current ? ">" : e.UpNext ? "+" : " ")} {e.Index + 1,4}. {(e.Item.Artist is { } a ? $"{a} - {title}" : title)}");
                }
            }

            return ExitCodes.Success;
        });

        var source = new QueueSource();
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var next = new Option<bool>("--next") { Description = "Play the added tracks right after the current one (before tracks added earlier with --next/--up-next)." };
        var upNext = new Option<bool>("--up-next") { Description = "Play the added tracks after the current one and the tracks already added to play next." };
        var add = new Command("add",
            "Add file(s) or library tracks (--album/--artist/--search/--playlist [--shuffle]) to the end of the queue, or up next with --next/--up-next. " +
            "During radio, additions always play next. Starts the player (stopped) if needed.");
        source.AddTo(add);
        add.Add(next);
        add.Add(upNext);
        add.Add(backend);
        add.Add(device);
        add.Add(exclusive);
        add.Validators.Add(r => source.Validate(r));
        add.Validators.Add(r =>
        {
            if (Options.Get(r, next) && Options.Get(r, upNext))
            {
                r.AddError("Give --next or --up-next, not both.");
            }
        });
        runner.SetAction(add, async (parseResult, json, cancellationToken) =>
        {
            var (items, seed, warnings) = source.Resolve(runner, parseResult);
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, parseResult.GetValue(backend)!,
                parseResult.GetValue(device), parseResult.GetValue(exclusive), cancellationToken).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var position = parseResult.GetValue(next) ? "next" : parseResult.GetValue(upNext) ? "upnext" : "end";
                var data = await client.SendAsync("queue.add", new { items, position }, cancellationToken).ConfigureAwait(false);
                PlayerSupport.WriteStatus(runner, json, "queue add", data, new Dictionary<string, object?>
                {
                    ["added"] = items.Count,
                    ["playerStarted"] = started,
                    ["seed"] = seed,
                    ["warnings"] = warnings.Count > 0 ? warnings : null,
                });
            }

            return ExitCodes.Success;
        }, "queue add");
        queue.Add(add);

        var clearUpNext = new Option<bool>("--up-next") { Description = "Only remove the tracks added to play next; the current track and the rest keep playing." };
        var clear = new Command("clear", "Stop playback and empty the queue (or, with --up-next, only remove the up-next tracks).") { clearUpNext };
        PlayerSupport.Control(runner, clear, "queue.clear", p => p.GetValue(clearUpNext) ? new { upNext = true } : null, name: "queue clear");
        queue.Add(clear);

        var position = new Argument<int>("position") { Description = "1-based position, as listed by 'music-spin queue'." };
        var remove = new Command("remove", "Remove one queue entry. The current track can't be removed (exit 8): skip it with 'next' first.") { position };
        PlayerSupport.Control(runner, remove, "queue.remove", p => new { index = p.GetValue(position) - 1 }, name: "queue remove");
        queue.Add(remove);

        var from = new Argument<int>("from") { Description = "1-based position of the entry to move." };
        var to = new Argument<int>("to") { Description = "1-based position it should end up at." };
        var move = new Command("move",
            "Move a queue entry. An entry moved right after the current track, or among the up-next tracks, becomes up next; the current track keeps playing.")
        {
            from, to,
        };
        PlayerSupport.Control(runner, move, "queue.move", p => new { from = p.GetValue(from) - 1, to = p.GetValue(to) - 1 }, name: "queue move");
        queue.Add(move);
        queue.Add(QueueSave(runner));
        return queue;
    }

    private static Command QueueSave(CommandRunner runner)
    {
        var name = new Argument<string>("name") { Description = "Playlist name (case-insensitive, unique)." };
        var replace = new Option<bool>("--replace") { Description = "Overwrite an existing playlist with this name." };
        var save = new Command("save", "Save the player's queue, in its current play order, as a playlist.") { name, replace };
        save.Validators.Add(r => PlaylistCommands.ValidateName(r, name));
        runner.SetAction(save, async (parseResult, json, cancellationToken) =>
        {
            var change = await PlaylistCommands.SaveQueueAsync(runner, parseResult, parseResult.GetValue(name)!, parseResult.GetValue(replace), cancellationToken)
                .ConfigureAwait(false);
            return PlaylistCommands.WriteChange(runner, json, "queue save", change, $"Saved {change.Added} queue entr{(change.Added == 1 ? "y" : "ies")} as");
        }, "queue save");
        return save;
    }

    private static Command Quit(CommandRunner runner, Command command, string? name = null)
    {
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var info = await client.HelloAsync(cancellationToken).ConfigureAwait(false);
            await client.QuitAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, name ?? command.Name, new { stopped = true, pid = info.Pid, dataDir = info.DataDir });
            }
            else
            {
                runner.Env.Out.WriteLine($"Background player (pid {info.Pid}) stopped.");
            }

            return ExitCodes.Success;
        }, name);
        return command;
    }

    private static Command Player(CommandRunner runner)
    {
        var player = new Command("player", "Manage the background player process: status, quit/stop.");

        var status = new Command("status", "Is a background player running for this data folder? pid, endpoint, log file, uptime, output, idle timeout. Exit 0 either way (see 'running').");
        runner.SetAction(status, async (parseResult, json, cancellationToken) =>
        {
            var endpoint = PlayerSupport.Endpoint(runner, parseResult);
            object data;
            try
            {
                await using var client = await PlayerClient.ConnectAsync(endpoint, requestTimeout: runner.Env.PlayerRequestTimeout, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var info = await client.HelloAsync(cancellationToken).ConfigureAwait(false);
                var playerStatus = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
                data = new
                {
                    running = true,
                    info.Pid,
                    info.Version,
                    info.Protocol,
                    info.Endpoint,
                    info.DataDir,
                    info.LogFile,
                    info.StartedAt,
                    UptimeSeconds = Math.Round((runner.Env.Time.GetUtcNow() - info.StartedAt).TotalSeconds, 1),
                    info.IdleTimeoutSeconds,
                    info.Output,
                    State = playerStatus.State,
                    playerStatus.QueueLength,
                };
            }
            catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.PlayerNotRunning)
            {
                var stale = PlayerHost.ReadInfo(endpoint);
                data = new
                {
                    running = false,
                    endpoint = endpoint.Display,
                    dataDir = endpoint.DataDirectory,
                    logFile = File.Exists(endpoint.LogFile) ? endpoint.LogFile : null,
                    stalePid = stale is not null && !PlayerHost.IsLockHeld(endpoint) ? stale.Pid : (int?)null,
                };
            }

            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "player status", data);
            }
            else
            {
                runner.Env.Out.WriteLine(JsonOutput.Serialize(data));
            }

            return ExitCodes.Success;
        }, "player status");
        player.Add(status);

        player.Add(Quit(runner, new Command("quit", "Shut the background player down."), "player quit"));
        player.Add(Quit(runner, new Command("stop", "Same as 'player quit'."), "player stop"));
        player.Add(Run(runner));
        return player;
    }

    /// <summary>Hidden: the background player process itself (spawned detached by `play`).</summary>
    private static Command Run(CommandRunner runner)
    {
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var idle = new Option<double?>("--idle-timeout")
        {
            Description = "Seconds to stay alive while stopped with no client attached (default: playerIdleTimeoutSeconds in settings.json; 0 = forever).",
        };
        Options.Validated(idle, v => v is < 0 || (v is { } d && !double.IsFinite(d)) ? "--idle-timeout must be 0 or greater." : null);
        var noTray = Options.NoTray();
        var command = new Command("run", "Run the background player in this process (internal; 'music-spin play' starts it detached).")
        {
            backend, device, exclusive, idle, noTray,
        };
        command.Hidden = true;

        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var env = runner.Env;
            if (env.HandleSignals)
            {
                NativeProcess.TryDetachFromTerminal();
            }

            using var sighup = env.HandleSignals && !OperatingSystem.IsWindows()
                ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true)
                : null;

            var dir = runner.DataDirectory(parseResult);
            MusicSpin.Core.Hosting.AppPaths.EnsureWritable(dir);
            var (settings, _, _) = runner.EnsureSettings(dir);
            var endpoint = PlayerEndpoint.For(dir);
            var idleSeconds = parseResult.GetValue(idle) ?? settings.PlayerIdleTimeoutSeconds;
            if (!double.IsFinite(idleSeconds) || idleSeconds < 0)
            {
                idleSeconds = AppSettings.DefaultPlayerIdleTimeoutSeconds;
            }

            // Spawned by PlayerProcessLauncher on Linux/macOS: stdout/stderr already append to the log file, so log to
            // stderr (two independent writers on one file would overwrite each other). Otherwise open the log ourselves.
            var redirected = !string.IsNullOrEmpty(env.GetEnvironmentVariable(PlayerProcessLauncher.LogEnvironmentVariable));
            await using var ownLog = redirected ? null : OpenLog(endpoint);
            var log = redirected ? env.Error : ownLog;
            var provider = env.OutputProviders(parseResult.GetValue(backend)!);
            var host = PlayerHost.TryStart(new PlayerHostOptions
            {
                DataDirectory = dir,
                Decoders = env.Decoders,
                Outputs = provider,
                Output = new AudioOutputOptions(parseResult.GetValue(device), parseResult.GetValue(exclusive)),
                IdleTimeout = idleSeconds > 0 ? TimeSpan.FromSeconds(idleSeconds) : null,
                InitialVolume = (float)Math.Clamp(double.IsFinite(settings.Volume) ? settings.Volume : 1, 0, 1),
                InitialRepeat = PlaybackModes.TryParseRepeat(settings.Repeat, out var repeatMode) ? repeatMode : RepeatMode.Off,
                InitialShuffle = settings.Shuffle,
                BufferMilliseconds = settings.PlaybackBufferMs,
                Gapless = settings.Gapless,
                Log = log,
                Time = env.Time,
            });
            if (host is null)
            {
                (provider as IDisposable)?.Dispose();
                var other = PlayerHost.ReadInfo(endpoint);
                var message = $"A background player is already running for '{dir.Path}'{(other is null ? string.Empty : $" (pid {other.Pid})")}.";
                if (json)
                {
                    JsonOutput.WriteSuccess(env.Out, "player run", new { alreadyRunning = true, pid = other?.Pid, endpoint = endpoint.Display });
                }
                else
                {
                    env.Out.WriteLine(message);
                }

                return ExitCodes.Success;
            }

            if (json)
            {
                JsonOutput.WriteSuccess(env.Out, "player run", host.Info);
            }
            else
            {
                env.Out.WriteLine($"MusicSpin player (pid {host.Info.Pid}) listening on {host.Info.Endpoint}");
            }

            await env.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            // The tray icon (#83): a separate app process that exits with this player. Best effort, logged either way.
            var tray = (env.TrayLauncher ?? TrayLauncher.ForThisProcess(env.GetEnvironmentVariable))
                .Launch(dir.Path, settings.TrayIcon, parseResult.GetValue(noTray));
            host.Note(tray.Message);

            using (cancellationToken.Register(() => _ = host.StopAsync("signal")))
            {
                var reason = await host.Completion.ConfigureAwait(false);
                if (!json)
                {
                    env.Out.WriteLine($"MusicSpin player stopped ({reason}).");
                }
            }

            return ExitCodes.Success;
        }, "player run");
        return command;
    }

    private static StreamWriter? OpenLog(PlayerEndpoint endpoint)
    {
        try
        {
            var stream = new FileStream(endpoint.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
