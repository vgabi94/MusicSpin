using System.Text.Json;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Equalization;
using MusicSpin.Core.Normalization;
using MusicSpin.Core.Playback;

namespace MusicSpin.Core.Remote;

/// <summary>What the transport should do after a request was answered.</summary>
public enum ProtocolAction
{
    None,

    /// <summary><c>quit</c>: shut the player down (serve: exit; background player: stop the process).</summary>
    Quit,

    /// <summary><c>subscribe</c>: start pushing events on this connection.</summary>
    Subscribe,
}

/// <summary>One answered request: the JSON line to send back and the follow-up action.</summary>
/// <param name="Spectrum">A <c>subscribe</c> that asked for <c>spectrum</c> events too.</param>
public readonly record struct ProtocolReply(string Line, ProtocolAction Action, bool Spectrum = false);

/// <summary>Wire shapes of the JSON-lines protocol.</summary>
public static class ProtocolMessages
{
    public sealed record Response(JsonElement? Id, bool Ok, object? Data, ProtocolError? Error);

    public sealed record ProtocolError(string Code, int ExitCode, string Message);

    public sealed record Event(string Name, object Data)
    {
        // Serialized as {"event":"...","data":{...}}.
        public string ToJson() => ProtocolJson.Serialize(new EventWire(Name, Data));
    }

    private sealed record EventWire(string Event, object Data);

    public static string Ok(JsonElement? id, object? data) => ProtocolJson.Serialize(new Response(id, true, data ?? new { }, null));

    public static string Fail(JsonElement? id, int exitCode, string message)
        => ProtocolJson.Serialize(new Response(id, false, null, new ProtocolError(ExitCodes.Name(exitCode), exitCode, message)));
}

/// <summary>
/// JSON-lines player protocol (v1), transport independent. One request per line:
/// <c>{"id":1,"cmd":"pause"}</c> → <c>{"id":1,"ok":true,"data":{...status...}}</c> or
/// <c>{"id":1,"ok":false,"error":{"code":"invalid_state","exitCode":8,"message":"..."}}</c>.
/// Events (no id): <c>{"event":"stateChanged","data":{...}}</c>.
/// <para>Commands: hello, status, load(path), play(index?), pause, resume, toggle, stop, next, prev,
/// seek(seconds | delta), volume(value | delta; unmutes), mute(mode? = on|off|toggle), info(path), queue(offset?, limit?),
/// queue.set(items, start?, play?, startSeconds?, shuffle?, seed?), queue.play(items, start), queue.add(items, position? = end|next|upnext),
/// queue.remove(index), queue.move(from, to), queue.clear(upNext?),
/// repeat(mode? = off|one|all), shuffle(mode? = on|off|toggle, seed?),
/// normalize(mode?, target?, preamp?), eq(enabled?, preset?, gains?, band?, gain?, preamp?, save?, delete?), radio(filter?, play?), radio.end, subscribe(spectrum?), quit.
/// <c>queue.play</c> plays entry <c>start</c> of the list <c>items</c> (an album, a playlist: what a user clicked in): when that
/// list is what the queue is playing, the queue is kept and playback jumps to the entry (up-next entries follow it), otherwise it
/// replaces the queue like <c>queue.set</c> (see <see cref="QueuePlayer.PlayFromContext"/>).
/// <c>items</c> are paths or <see cref="QueueItem"/> objects. repeat/shuffle/mute without a mode just return the status.</para>
/// </summary>
public sealed class PlayerProtocol(QueuePlayer player, IAudioDecoderFactory decoders, Func<object>? hello = null)
{
    public const int Version = 1;

    public static IReadOnlyList<string> Commands { get; } =
    [
        "hello", "status", "load", "play", "pause", "resume", "toggle", "stop", "next", "prev", "seek", "volume", "mute", "info",
        "queue", "queue.set", "queue.play", "queue.add", "queue.remove", "queue.move", "queue.clear", "repeat", "shuffle", "radio", "radio.end", "subscribe", "quit",
        "normalize",
        "eq",
    ];

    public QueuePlayer Player { get; } = player ?? throw new ArgumentNullException(nameof(player));

    /// <summary>Handles one request line. Never throws for bad input; errors become error responses.</summary>
    public ProtocolReply Handle(string line)
    {
        JsonElement? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Request must be a JSON object.");
            }

            if (root.TryGetProperty("id", out var idElement))
            {
                id = idElement.Clone();
            }

            var cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var (data, action) = Dispatch(cmd, root);
            var spectrum = action == ProtocolAction.Subscribe && OptionalBool(root, "spectrum") == true;
            return new ProtocolReply(ProtocolMessages.Ok(id, data), action, spectrum);
        }
        catch (JsonException ex)
        {
            return new ProtocolReply(ProtocolMessages.Fail(id, ExitCodes.Usage, $"Invalid JSON: {ex.Message}"), ProtocolAction.None);
        }
        catch (FormatException ex)
        {
            return new ProtocolReply(ProtocolMessages.Fail(id, ExitCodes.Usage, ex.Message), ProtocolAction.None);
        }
        catch (MusicSpinException ex)
        {
            return new ProtocolReply(ProtocolMessages.Fail(id, ExitCodes.From(ex.Code), ex.Message), ProtocolAction.None);
        }
        catch (ObjectDisposedException)
        {
            return new ProtocolReply(ProtocolMessages.Fail(id, ExitCodes.PlayerUnavailable, "The player is shutting down."), ProtocolAction.None);
        }
        catch (Exception ex)
        {
            return new ProtocolReply(ProtocolMessages.Fail(id, ExitCodes.Error, $"{ex.GetType().Name}: {ex.Message}"), ProtocolAction.None);
        }
    }

    private (object? Data, ProtocolAction Action) Dispatch(string? cmd, JsonElement root)
    {
        switch (cmd)
        {
            case "hello":
                return (hello?.Invoke() ?? new { protocol = Version, decoder = decoders.Name }, ProtocolAction.None);
            case "status":
                return (Player.Status(), ProtocolAction.None);
            case "load":
                Player.Load(RequiredString(root, "path"));
                return (Player.Status().Track, ProtocolAction.None);
            case "play":
                return (Player.Play(OptionalInt(root, "index")), ProtocolAction.None);
            case "pause":
                return (Player.Pause(), ProtocolAction.None);
            case "resume":
                return (Player.Resume(), ProtocolAction.None);
            case "toggle":
                return (Player.Toggle(), ProtocolAction.None);
            case "stop":
                return (Player.Stop(), ProtocolAction.None);
            case "next":
                return (Player.Next(), ProtocolAction.None);
            case "prev":
            case "previous":
                return (Player.Previous(), ProtocolAction.None);
            case "seek":
                if (OptionalNumber(root, "delta") is { } delta)
                {
                    return (Player.SeekBy(TimeSpan.FromSeconds(delta)), ProtocolAction.None);
                }

                return (Player.Seek(TimeSpan.FromSeconds(RequiredNumber(root, "seconds"))), ProtocolAction.None);
            case "volume":
                if (OptionalNumber(root, "delta") is { } volumeDelta)
                {
                    return (Player.ChangeVolume(volumeDelta), ProtocolAction.None);
                }

                return (Player.SetVolume(RequiredNumber(root, "value")), ProtocolAction.None);
            case "mute":
                return (Mute(root), ProtocolAction.None);
            case "info":
                return (TrackDto.From(decoders.Probe(RequiredString(root, "path"))), ProtocolAction.None);
            case "queue":
                return (Player.List(OptionalInt(root, "offset") ?? 0, OptionalInt(root, "limit") ?? 100), ProtocolAction.None);
            case "queue.set":
                return (Player.SetQueue(Items(root), OptionalInt(root, "start"), OptionalBool(root, "play") ?? true,
                    OptionalNumber(root, "startSeconds") is { } s ? TimeSpan.FromSeconds(s) : null,
                    OptionalBool(root, "shuffle"), OptionalInt(root, "seed")), ProtocolAction.None);
            case "queue.play":
                return (Player.PlayFromContext(Items(root), RequiredInt(root, "start")), ProtocolAction.None);
            case "queue.add":
                return (Player.Add(Items(root), Position(root)), ProtocolAction.None);
            case "queue.remove":
                return (Player.Remove(RequiredInt(root, "index")), ProtocolAction.None);
            case "queue.move":
                return (Player.Move(RequiredInt(root, "from"), RequiredInt(root, "to")), ProtocolAction.None);
            case "queue.clear":
                return (OptionalBool(root, "upNext") == true ? Player.ClearUpNext() : Player.Clear(), ProtocolAction.None);
            case "repeat":
                return (OptionalString(root, "mode") is { } repeat
                    ? Player.SetRepeat(PlaybackModes.TryParseRepeat(repeat, out var mode)
                        ? mode
                        : throw new FormatException($"Unknown repeat mode '{repeat}'. Use {PlaybackModes.RepeatValues}."))
                    : Player.Status(), ProtocolAction.None);
            case "shuffle":
                return (Shuffle(root), ProtocolAction.None);
            case "normalize":
                return (Normalize(root), ProtocolAction.None);
            case "eq":
                return (Equalize(root), ProtocolAction.None);
            case "radio":
                return (Player.StartRadio(RadioFilterOf(root), OptionalBool(root, "play") ?? true), ProtocolAction.None);
            case "radio.end":
                return (Player.EndRadio(), ProtocolAction.None);
            case "subscribe":
                return (Player.Status(), ProtocolAction.Subscribe);
            case "quit":
                return (new { bye = true }, ProtocolAction.Quit);
            default:
                throw new FormatException(cmd is null
                    ? "Missing \"cmd\"."
                    : $"Unknown cmd '{cmd}'. Expected one of: {string.Join('|', Commands)}.");
        }
    }

    /// <summary><c>mode</c>: on | off | toggle; without it, just the status.</summary>
    private PlayerStatus Mute(JsonElement root)
    {
        if (OptionalString(root, "mode") is not { } text)
        {
            return Player.Status();
        }

        bool? muted = text.Trim().Equals("toggle", StringComparison.OrdinalIgnoreCase) ? null
            : PlaybackModes.TryParseShuffle(text, current: false, out var on) ? on
            : throw new FormatException($"Unknown mute mode '{text}'. Use on|off|toggle.");
        return Player.SetMuted(muted);
    }

    private PlayerStatus Shuffle(JsonElement root)
    {
        var seed = OptionalInt(root, "seed");
        bool enabled;
        if (OptionalBool(root, "enabled") is { } flag)
        {
            enabled = flag;
        }
        else if (OptionalString(root, "mode") is { } text)
        {
            if (!PlaybackModes.TryParseShuffle(text, Player.Shuffle, out enabled))
            {
                throw new FormatException($"Unknown shuffle mode '{text}'. Use on|off|toggle.");
            }
        }
        else
        {
            return seed is null ? Player.Status() : throw new FormatException("\"seed\" needs \"mode\": \"on\".");
        }

        return Player.SetShuffle(enabled, seed);
    }

    /// <summary><c>mode</c>: off | track | album | auto, <c>target</c> (LUFS) and <c>preamp</c> (dB); without any, just the status.</summary>
    private PlayerStatus Normalize(JsonElement root)
    {
        var mode = OptionalString(root, "mode");
        var target = OptionalNumber(root, "target");
        var preamp = OptionalNumber(root, "preamp");
        if (mode is null && target is null && preamp is null)
        {
            return Player.Status();
        }

        var normalizer = Player.Normalizer
            ?? throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "This player has no volume levelling.");
        var current = normalizer.Settings;
        var next = current;
        if (mode is not null)
        {
            next = NormalizationSettings.TryParseMode(mode, out var parsed)
                ? next with { Mode = parsed }
                : throw new FormatException($"Unknown normalization mode '{mode}'. Use {NormalizationSettings.ModeValues}.");
        }

        if (target is { } t)
        {
            next = t is >= NormalizationSettings.MinTargetLufs and <= NormalizationSettings.MaxTargetLufs
                ? next with { TargetLufs = t }
                : throw new FormatException($"\"target\" must be between {NormalizationSettings.MinTargetLufs} and {NormalizationSettings.MaxTargetLufs} LUFS.");
        }

        if (preamp is { } p)
        {
            next = p is >= NormalizationSettings.MinPreampDb and <= NormalizationSettings.MaxPreampDb
                ? next with { PreampDb = p }
                : throw new FormatException($"\"preamp\" must be between {NormalizationSettings.MinPreampDb} and {NormalizationSettings.MaxPreampDb} dB.");
        }

        normalizer.Configure(next);
        return Player.Status();
    }

    /// <summary>
    /// The graphic equalizer (#141). Any of: <c>delete</c> (a saved preset's name), <c>preset</c> (apply one; turns the
    /// equalizer on), <c>gains</c> (ten dB values), <c>band</c> (1..10 or a frequency like "1k") with <c>gain</c> (dB),
    /// <c>preamp</c> (dB), <c>enabled</c>, then <c>save</c> (save the resulting gains under a name). Everything is checked
    /// before anything changes; without any, just the status.
    /// </summary>
    private PlayerStatus Equalize(JsonElement root)
    {
        var enabled = OptionalBool(root, "enabled");
        var preset = OptionalString(root, "preset");
        var preamp = OptionalNumber(root, "preamp");
        var gain = OptionalNumber(root, "gain");
        var save = OptionalString(root, "save");
        var delete = OptionalString(root, "delete");
        var band = OptionalBand(root);
        var gains = OptionalGains(root);
        if (enabled is null && preset is null && preamp is null && gain is null && save is null && delete is null && band is null && gains is null)
        {
            return Player.Status();
        }

        var eq = Player.Equalizer
            ?? throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "This player has no equalizer.");
        if ((band is null) != (gain is null))
        {
            throw new FormatException("\"band\" and \"gain\" go together.");
        }

        if (gain is { } g && g is < EqualizerSettings.MinGainDb or > EqualizerSettings.MaxGainDb)
        {
            throw new FormatException($"\"gain\" must be between {EqualizerSettings.MinGainDb} and {EqualizerSettings.MaxGainDb} dB.");
        }

        if (preamp is { } p && p is < EqualizerSettings.MinPreampDb or > EqualizerSettings.MaxPreampDb)
        {
            throw new FormatException($"\"preamp\" must be between {EqualizerSettings.MinPreampDb} and {EqualizerSettings.MaxPreampDb} dB.");
        }

        if (save is not null && EqualizerSettings.CheckPresetName(save) is { } badName)
        {
            throw new FormatException(badName);
        }

        if (delete is not null)
        {
            var name = EqualizerSettings.NormalizeName(delete);
            if (EqualizerSettings.BuiltIn(name) is not null)
            {
                throw new FormatException($"'{name}' is a built-in preset and cannot be deleted.");
            }

            if (eq.Find(name) is null)
            {
                throw new FormatException($"There is no saved equalizer preset '{name}'.");
            }
        }

        EqualizerPreset? chosen = null;
        if (preset is not null)
        {
            chosen = eq.Find(preset);
            if (chosen is null || (delete is not null && chosen.Name == EqualizerSettings.NormalizeName(delete)))
            {
                throw new FormatException($"Unknown equalizer preset '{preset}'. Presets: {string.Join(", ", eq.Presets.Select(x => x.Name))}.");
            }
        }

        if (save is not null && eq.Find(save) is null && eq.UserPresets.Count >= EqualizerSettings.MaxUserPresets)
        {
            throw new FormatException($"At most {EqualizerSettings.MaxUserPresets} presets can be saved; delete one first.");
        }

        // All checked: apply.
        if (delete is not null)
        {
            eq.DeletePreset(delete);
        }

        var next = eq.Settings;
        if (chosen is not null)
        {
            next = next with { Enabled = true, Gains = [.. chosen.Gains], Preset = chosen.Name };
        }

        if (gains is not null)
        {
            next = next with { Gains = gains, Preset = eq.Presets.FirstOrDefault(x => x.Gains.SequenceEqual(gains))?.Name };
        }

        if (band is { } b && gain is { } bandGain)
        {
            var edited = next.BandGains.ToArray();
            edited[b] = bandGain;
            next = next with { Gains = edited, Preset = eq.Presets.FirstOrDefault(x => x.Gains.SequenceEqual(edited))?.Name };
        }

        if (preamp is { } pre)
        {
            next = next with { PreampDb = pre };
        }

        if (enabled is { } on)
        {
            next = next with { Enabled = on };
        }

        eq.Configure(next);
        if (save is not null)
        {
            eq.SavePreset(save);
        }

        return Player.Status();
    }

    /// <summary><c>band</c>: 1..10 or a centre frequency ("31", "1k", "16000"); returned 0-based.</summary>
    private static int? OptionalBand(JsonElement root)
    {
        if (!root.TryGetProperty("band", out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
        return EqualizerSettings.TryParseBand(text, out var band)
            ? band
            : throw new FormatException($"Unknown equalizer band '{text ?? v.GetRawText()}'. Use 1..{EqualizerSettings.BandCount} or a centre frequency: {string.Join(", ", Enumerable.Range(0, EqualizerSettings.BandCount).Select(EqualizerSettings.BandName))}.");
    }

    /// <summary><c>gains</c>: exactly ten finite numbers, each -12..+12 dB.</summary>
    private static double[]? OptionalGains(JsonElement root)
    {
        if (!root.TryGetProperty("gains", out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != EqualizerSettings.BandCount)
        {
            throw new FormatException($"\"gains\" must be an array of {EqualizerSettings.BandCount} numbers (dB).");
        }

        var gains = new double[EqualizerSettings.BandCount];
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var d) || !double.IsFinite(d))
            {
                throw new FormatException($"\"gains\" must be an array of {EqualizerSettings.BandCount} numbers (dB).");
            }

            gains[i++] = d;
        }

        return EqualizerSettings.CheckGains(gains) is { } problem ? throw new FormatException(problem) : gains;
    }

    /// <summary><c>"filter": {artists, albums, genres, years: [{from, to}], formats, searches, hiRes}</c>; missing = whole library.</summary>
    private static MusicSpin.Core.Library.RadioFilter RadioFilterOf(JsonElement root)
    {
        if (!root.TryGetProperty("filter", out var f) || f.ValueKind == JsonValueKind.Null)
        {
            return new MusicSpin.Core.Library.RadioFilter();
        }

        if (f.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("\"filter\" must be an object.");
        }

        var filter = ProtocolJson.Deserialize<MusicSpin.Core.Library.RadioFilter>(f) ?? new MusicSpin.Core.Library.RadioFilter();
        return filter.Validate() is { } error ? throw new FormatException(error) : filter;
    }

    /// <summary><c>"position"</c>: end (default), next or upnext.</summary>
    private static QueuePosition Position(JsonElement root)
        => OptionalString(root, "position")?.ToLowerInvariant() switch
        {
            null or "end" => QueuePosition.End,
            "next" => QueuePosition.Next,
            "upnext" => QueuePosition.UpNext,
            var other => throw new FormatException($"Unknown position '{other}'. Use end|next|upnext."),
        };

    private static int RequiredInt(JsonElement root, string name)
        => OptionalInt(root, name) ?? throw new FormatException($"Missing integer \"{name}\".");

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new FormatException($"\"{name}\" must be a string.");
    }

    private static List<QueueItem> Items(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Missing array \"items\" (paths or {\"path\":...} objects).");
        }

        var list = new List<QueueItem>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            var parsed = item.ValueKind switch
            {
                JsonValueKind.String => new QueueItem(item.GetString()!),
                JsonValueKind.Object => ProtocolJson.Deserialize<QueueItem>(item),
                _ => null,
            };
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Path))
            {
                throw new FormatException("Each item must be a path string or an object with a non-empty \"path\".");
            }

            list.Add(parsed);
        }

        return list;
    }

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new FormatException($"Missing string \"{name}\".");

    private static double RequiredNumber(JsonElement root, string name)
        => OptionalNumber(root, name) ?? throw new FormatException($"Missing number \"{name}\".");

    private static double? OptionalNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? d
            : throw new FormatException($"\"{name}\" must be a finite number.");
    }

    private static int? OptionalInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : throw new FormatException($"\"{name}\" must be an integer.");
    }

    private static bool? OptionalBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"\"{name}\" must be true or false."),
        };
    }
}

/// <summary>Turns <see cref="QueuePlayer"/> events into protocol event lines.</summary>
public static class PlayerEvents
{
    /// <summary>Forwards events to <paramref name="sink"/> until the returned handle is disposed. The sink must not block.</summary>
    public static IDisposable Attach(QueuePlayer player, Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(sink);

        void Send(string name, object data)
        {
            try
            {
                sink(new ProtocolMessages.Event(name, data).ToJson());
            }
            catch (Exception)
            {
                // a broken sink must never break playback
            }
        }

        EventHandler<PlayerStateChangedEventArgs> state = (_, e) => Send("stateChanged", new { state = e.NewState, previous = e.OldState });
        EventHandler<TrackLoadedEventArgs> loaded = (_, e) => Send("trackLoaded", TrackDto.From(e.Track));
        EventHandler ended = (_, _) => Send("ended", new { });
        EventHandler queueEnded = (_, _) => Send("queueEnded", new { });
        EventHandler queueChanged = (_, _) => Send("queueChanged", new { length = player.Count });
        EventHandler<TrackSkippedEventArgs> skipped = (_, e) => Send("trackSkipped", e.Item);
        EventHandler<PlayerErrorEventArgs> error = (_, e) => Send("error", new { message = e.Exception.Message });

        player.StateChanged += state;
        player.TrackLoaded += loaded;
        player.TrackEnded += ended;
        player.QueueEnded += queueEnded;
        player.QueueChanged += queueChanged;
        player.TrackSkipped += skipped;
        player.Error += error;
        return new Detach(() =>
        {
            player.StateChanged -= state;
            player.TrackLoaded -= loaded;
            player.TrackEnded -= ended;
            player.QueueEnded -= queueEnded;
            player.QueueChanged -= queueChanged;
            player.TrackSkipped -= skipped;
            player.Error -= error;
        });
    }

    private sealed class Detach(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
