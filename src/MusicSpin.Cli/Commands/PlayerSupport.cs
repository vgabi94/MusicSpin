using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Commands;

/// <summary>Connecting to / starting the background player and printing its answers.</summary>
internal static class PlayerSupport
{
    public static PlayerEndpoint Endpoint(CommandRunner runner, ParseResult parseResult)
        => PlayerEndpoint.For(runner.DataDirectory(parseResult));

    /// <summary>Connects to the running player.</summary>
    /// <exception cref="MusicSpinException">PlayerNotRunning (with a hint, and a note about a crashed player's leftovers).</exception>
    public static async Task<PlayerClient> ConnectAsync(CommandRunner runner, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(runner, parseResult);
        try
        {
            return await PlayerClient.ConnectAsync(endpoint, requestTimeout: runner.Env.PlayerRequestTimeout, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.PlayerNotRunning)
        {
            throw NotRunning(endpoint);
        }
    }

    public static MusicSpinException NotRunning(PlayerEndpoint endpoint)
    {
        var stale = PlayerHost.ReadInfo(endpoint) is { } info && !PlayerHost.IsLockHeld(endpoint)
            ? $" (a previous player, pid {info.Pid}, did not shut down cleanly; its leftovers are replaced on the next start)"
            : string.Empty;
        return new MusicSpinException(MusicSpinErrorCode.PlayerNotRunning,
            $"No background player is running for '{endpoint.DataDirectory}'{stale}. Start one with 'music-spin play <file> | --album <title> | --artist <name>'.");
    }

    /// <summary>Connects to the running player, or starts one with the requested output.</summary>
    public static async Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(CommandRunner runner, ParseResult parseResult,
        string backend, string? device, bool exclusive, CancellationToken cancellationToken)
    {
        var env = runner.Env;
        var dir = runner.DataDirectory(parseResult);
        MusicSpin.Core.Hosting.AppPaths.EnsureWritable(dir); // clear exit 9 before trying to spawn anything
        var endpoint = PlayerEndpoint.For(dir);
        var args = new List<string> { "player", "run", "--data-dir", dir.Path, "--backend", backend };
        if (device is not null)
        {
            args.AddRange(["--device", device]);
        }

        if (exclusive)
        {
            args.Add("--exclusive");
        }

        var request = new PlayerLaunchRequest(dir, endpoint, args, backend, device, exclusive);
        var launch = env.PlayerLauncher ?? ((r, _) => Task.FromResult<IPlayerProcess?>(PlayerProcessLauncher.Start(r.Endpoint, r.Arguments)));
        return await PlayerConnector.ConnectOrStartAsync(endpoint, ct => launch(request, ct), env.PlayerStartTimeout, env.PlayerRequestTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs one request against the running player and prints the resulting status.</summary>
    public static void Control(CommandRunner runner, Command command, string cmd, Func<ParseResult, object?>? args = null, string? name = null)
    {
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            await using var client = await ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var data = await client.SendAsync(cmd, args?.Invoke(parseResult), cancellationToken).ConfigureAwait(false);
            WriteStatus(runner, json, name ?? command.Name, data);
            return ExitCodes.Success;
        }, name);
    }

    public static void WriteStatus(CommandRunner runner, bool json, string command, JsonElement data, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var env = runner.Env;
        if (json)
        {
            if (extra is null || extra.Count == 0 || data.ValueKind != JsonValueKind.Object)
            {
                JsonOutput.WriteSuccess(env.Out, command, data);
                return;
            }

            // Status fields first, extras (playerStarted, seed, warnings...) merged in at the same level.
            var node = JsonNode.Parse(data.GetRawText())!.AsObject();
            foreach (var (key, value) in extra)
            {
                if (value is not null)
                {
                    node[key] = JsonSerializer.SerializeToNode(value, JsonOutput.Options);
                }
            }

            JsonOutput.WriteSuccess(env.Out, command, node);
            return;
        }

        if (extra is not null && extra.TryGetValue("warnings", out var w) && w is IEnumerable<string> warnings)
        {
            foreach (var warning in warnings)
            {
                env.Error.WriteLine($"music-spin {command}: warning: {warning}");
            }
        }

        var status = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("state", out _) && data.TryGetProperty("queueLength", out _)
            ? ProtocolJson.Deserialize<PlayerStatus>(data)
            : null;
        if (status is null)
        {
            env.Out.WriteLine(data.ValueKind == JsonValueKind.Undefined ? "ok" : data.GetRawText());
            return;
        }

        foreach (var line in Describe(status))
        {
            env.Out.WriteLine(line);
        }
    }

    public static IEnumerable<string> Describe(PlayerStatus s)
    {
        var state = s.Ended ? "Ended" : s.State switch
        {
            PlayerState.Playing => "Playing",
            PlayerState.Paused => "Paused",
            _ => "Stopped",
        };
        if (s.QueueIndex is not { } index)
        {
            yield return $"{state} (queue empty)";
            yield break;
        }

        var title = s.Track?.Title ?? s.Item?.Title ?? Path.GetFileName(s.Item?.Path ?? s.Track?.Path ?? "?");
        var artist = s.Track?.Artist ?? s.Item?.Artist;
        var album = s.Track?.Album ?? s.Item?.Album;
        yield return $"{state}  [{index + 1}/{s.QueueLength}]  {(artist is null ? title : $"{artist} - {title}")}{(album is null ? string.Empty : $"  ({album})")}";
        var format = s.Track is { } t
            ? $"{t.Codec.ToUpperInvariant()} {(t.BitsPerSample is { } b ? $"{b}-bit / " : string.Empty)}{t.SampleRate / 1000.0:0.#} kHz · {t.Channels} ch"
            : "?";
        yield return string.Create(CultureInfo.InvariantCulture,
            $"  {format}    {Human.Time(TimeSpan.FromSeconds(s.PositionSeconds))} / {Human.Time(TimeSpan.FromSeconds(s.DurationSeconds))}    volume {s.Volume * 100:0}%");
        if (s.Output is { } o)
        {
            yield return $"  Output: {o.Backend} · {o.Device ?? "default device"} · {(o.Exclusive ? "exclusive" : "shared")}";
        }

        if (s.LastError is { } error)
        {
            yield return $"  Last error: {error}";
        }

        if (s.Skipped is { Count: > 0 } skipped)
        {
            yield return $"  Skipped {skipped.Count} unplayable file(s), e.g. {skipped[^1].Path}";
        }
    }

    /// <summary>Parses a seek target: seconds ("90", "12.5"), [h:]mm:ss ("1:30", "1:02:03"), or relative with +/-.</summary>
    public static bool TryParseSeek(string text, out double seconds, out bool relative, out string? error)
    {
        seconds = 0;
        relative = false;
        error = null;
        var t = text.Trim();
        var sign = 1;
        if (t.StartsWith('+') || t.StartsWith('-'))
        {
            relative = true;
            sign = t[0] == '-' ? -1 : 1;
            t = t[1..];
        }

        var parts = t.Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length == 0))
        {
            error = $"Invalid position '{text}'. Use seconds (90), mm:ss (1:30), h:mm:ss, or +/- for relative (+10, -0:30).";
            return false;
        }

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
                || (!isLast && v != Math.Floor(v)) || (i > 0 && v >= 60))
            {
                error = $"Invalid position '{text}'. Use seconds (90), mm:ss (1:30), h:mm:ss, or +/- for relative (+10, -0:30).";
                return false;
            }

            total = total * 60 + v;
        }

        seconds = sign * total;
        return true;
    }

    /// <summary>Parses a volume: 0..1 ("0.5"), percent ("50%"), or relative ("+0.1", "-10%").</summary>
    public static bool TryParseVolume(string text, out double value, out bool relative, out string? error)
    {
        value = 0;
        relative = false;
        error = null;
        var t = text.Trim();
        var sign = 1;
        if (t.StartsWith('+') || t.StartsWith('-'))
        {
            relative = true;
            sign = t[0] == '-' ? -1 : 1;
            t = t[1..];
        }

        var percent = t.EndsWith('%');
        if (percent)
        {
            t = t[..^1];
        }

        if (!double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
        {
            error = $"Invalid volume '{text}'. Use 0..1 (0.5), a percentage (50%), or +/- for a change (+0.1, -10%).";
            return false;
        }

        value = sign * (percent ? v / 100 : v);
        if (!relative && value is < 0 or > 1)
        {
            error = $"Volume must be between 0.0 and 1.0 (or 0%-100%); got '{text}'.";
            return false;
        }

        return true;
    }
}
