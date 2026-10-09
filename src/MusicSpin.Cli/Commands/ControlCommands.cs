using System.CommandLine;
using System.Runtime.InteropServices;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Settings;

namespace MusicSpin.Cli.Commands;

/// <summary>Quick commands that talk to the background player and return immediately.</summary>
internal static class ControlCommands
{
    public static IEnumerable<Command> Create(CommandRunner runner)
    {
        yield return Simple(runner, "pause", "pause", "Pause playback (no-op when not playing).");
        yield return Simple(runner, "resume", "resume", "Resume (or start) playback of the current queue entry; after the queue ended, continue with newly added entries or restart.");
        yield return Simple(runner, "toggle", "toggle", "Pause when playing, otherwise resume.");
        yield return Simple(runner, "stop", "stop", "Stop and rewind the current track; the queue is kept. The player exits after the idle timeout.");
        yield return Simple(runner, "next", "next", "Play the next queue entry (skipping unplayable files). Exit 8 at the end of the queue.");
        yield return Simple(runner, "prev", "prev", "Play the previous queue entry. Exit 8 on the first entry (use 'seek 0' to restart).");
        yield return Seek(runner);
        yield return Volume(runner);
        yield return Simple(runner, "status", "status",
            "Player status: state, track (codec, sample rate, bit depth, tags), position, duration, volume, queue index/length, output device, exclusive mode.");
        yield return Queue(runner);
        yield return Quit(runner, new Command("quit", "Shut the background player down (stops playback, removes its endpoint)."));
        yield return Player(runner);
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
            if (Options.Get(r, position) is { } text && !PlayerSupport.TryParseSeek(text, out _, out _, out var error))
            {
                r.AddError(error!);
            }
        });
        PlayerSupport.Control(runner, command, "seek", p =>
        {
            if (!PlayerSupport.TryParseSeek(p.GetValue(position)!, out var seconds, out var relative, out var error))
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

    private static Command Queue(CommandRunner runner)
    {
        var limit = Options.Limit();
        var offset = Options.Offset();
        var queue = new Command("queue", "List the player's queue (index, current entry, path, tags). Subcommands: add, clear.") { limit, offset };
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
                runner.Env.Out.WriteLine($"Queue: {listing.Total} entr{(listing.Total == 1 ? "y" : "ies")}");
                foreach (var e in listing.Items)
                {
                    var title = e.Item.Title ?? Path.GetFileName(e.Item.Path);
                    runner.Env.Out.WriteLine($"{(e.Current ? ">" : " ")} {e.Index + 1,4}. {(e.Item.Artist is { } a ? $"{a} - {title}" : title)}");
                }
            }

            return ExitCodes.Success;
        });

        var source = new QueueSource();
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var add = new Command("add", "Append file(s) or library tracks (--album/--artist/--search [--shuffle]) to the queue. Starts the player (stopped) if needed.");
        source.AddTo(add);
        add.Add(backend);
        add.Add(device);
        add.Add(exclusive);
        add.Validators.Add(r => source.Validate(r));
        runner.SetAction(add, async (parseResult, json, cancellationToken) =>
        {
            var (items, seed) = source.Resolve(runner, parseResult);
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, parseResult.GetValue(backend)!,
                parseResult.GetValue(device), parseResult.GetValue(exclusive), cancellationToken).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var data = await client.SendAsync("queue.add", new { items }, cancellationToken).ConfigureAwait(false);
                PlayerSupport.WriteStatus(runner, json, "queue add", data, new Dictionary<string, object?>
                {
                    ["added"] = items.Count,
                    ["playerStarted"] = started,
                    ["seed"] = seed,
                });
            }

            return ExitCodes.Success;
        }, "queue add");
        queue.Add(add);

        var clear = new Command("clear", "Stop playback and empty the queue.");
        PlayerSupport.Control(runner, clear, "queue.clear", name: "queue clear");
        queue.Add(clear);
        return queue;
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
        var command = new Command("run", "Run the background player in this process (internal; 'musicspin play' starts it detached).")
        {
            backend, device, exclusive, idle,
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
