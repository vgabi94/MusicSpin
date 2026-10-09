using System.Text.Json;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Serve;

/// <summary>
/// JSON-lines control protocol (v1) over stdin/stdout, so an agent can drive one long-lived player.
/// <para>Request:  {"id":1,"cmd":"load","path":"/music/a.flac"}</para>
/// <para>Response: {"id":1,"ok":true,"data":{...}} or {"id":1,"ok":false,"error":{"code":"file_not_found","exitCode":3,"message":"..."}}</para>
/// <para>Event:    {"event":"stateChanged","data":{"state":"playing"}} (no id; may arrive at any time)</para>
/// Commands: load(path), play, pause, stop, seek(seconds), volume(value 0..1), status, info(path), quit.
/// TODO: expose the same operations as an MCP server (see README).
/// </summary>
public sealed class JsonLinesServer
{
    public const int ProtocolVersion = 1;

    private readonly IPlayer _player;
    private readonly IAudioDecoderFactory _decoders;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Lock _writeLock = new();

    public JsonLinesServer(IPlayer player, IAudioDecoderFactory decoders, TextReader input, TextWriter output)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _player.StateChanged += OnStateChanged;
        _player.TrackLoaded += OnTrackLoaded;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.Error += OnError;
        try
        {
            Emit(new EventMessage("ready", new { protocol = ProtocolVersion, decoder = _decoders.Name }));
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break; // stdin closed
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!Handle(line))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _player.StateChanged -= OnStateChanged;
            _player.TrackLoaded -= OnTrackLoaded;
            _player.PlaybackEnded -= OnPlaybackEnded;
            _player.Error -= OnError;
            _player.Stop();
        }
    }

    /// <summary>Handles one request line. Returns false when the server should exit.</summary>
    internal bool Handle(string line)
    {
        JsonElement? id = null;
        string? cmd = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Request must be a JSON object.");
            }

            if (root.TryGetProperty("id", out var idElement))
            {
                id = idElement.Clone();
            }

            cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            switch (cmd)
            {
                case "load":
                    Respond(id, TrackDto.From(_player.Load(RequiredString(root, "path"))));
                    break;
                case "play":
                    _player.Play();
                    Respond(id, Status());
                    break;
                case "pause":
                    _player.Pause();
                    Respond(id, Status());
                    break;
                case "stop":
                    _player.Stop();
                    Respond(id, Status());
                    break;
                case "seek":
                    _player.Seek(TimeSpan.FromSeconds(RequiredNumber(root, "seconds")));
                    Respond(id, Status());
                    break;
                case "volume":
                    _player.Volume = (float)RequiredNumber(root, "value");
                    Respond(id, Status());
                    break;
                case "status":
                    Respond(id, Status());
                    break;
                case "info":
                    Respond(id, TrackDto.From(_decoders.Probe(RequiredString(root, "path"))));
                    break;
                case "quit":
                    Respond(id, new { bye = true });
                    return false;
                default:
                    RespondError(id, ExitCodes.Usage,
                        cmd is null ? "Missing \"cmd\"." : $"Unknown cmd '{cmd}'. Expected load|play|pause|stop|seek|volume|status|info|quit.");
                    break;
            }
        }
        catch (JsonException ex)
        {
            RespondError(id, ExitCodes.Usage, $"Invalid JSON: {ex.Message}");
        }
        catch (FormatException ex)
        {
            RespondError(id, ExitCodes.Usage, ex.Message);
        }
        catch (MusicSpinException ex)
        {
            RespondError(id, ExitCodes.From(ex.Code), ex.Message);
        }
        catch (Exception ex)
        {
            RespondError(id, ExitCodes.Error, $"{ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }

    private StatusDto Status() => new(
        _player.State,
        Math.Round(_player.Position.TotalSeconds, 3),
        Math.Round(_player.Duration.TotalSeconds, 3),
        _player.Volume,
        _player.CurrentTrack is { } t ? TrackDto.From(t) : null);

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new FormatException($"Missing string \"{name}\".");

    private static double RequiredNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : throw new FormatException($"Missing number \"{name}\".");

    private void Respond(JsonElement? id, object data) => Emit(new ResponseMessage(id, true, data, null));

    private void RespondError(JsonElement? id, int exitCode, string message)
        => Emit(new ResponseMessage(id, false, null, new JsonOutput.ErrorInfo(ExitCodes.Name(exitCode), exitCode, message)));

    private void OnStateChanged(object? sender, PlayerStateChangedEventArgs e)
        => Emit(new EventMessage("stateChanged", new { state = e.NewState, previous = e.OldState }));

    private void OnTrackLoaded(object? sender, TrackLoadedEventArgs e) => Emit(new EventMessage("trackLoaded", TrackDto.From(e.Track)));

    private void OnPlaybackEnded(object? sender, EventArgs e) => Emit(new EventMessage("ended", new { }));

    private void OnError(object? sender, PlayerErrorEventArgs e) => Emit(new EventMessage("error", new { message = e.Exception.Message }));

    private void Emit(object message)
    {
        var json = JsonOutput.Serialize(message);
        lock (_writeLock)
        {
            _output.WriteLine(json);
            _output.Flush();
        }
    }

    public sealed record StatusDto(PlayerState State, double PositionSeconds, double DurationSeconds, float Volume, TrackDto? Track);

    private sealed record ResponseMessage(JsonElement? Id, bool Ok, object? Data, JsonOutput.ErrorInfo? Error);

    private sealed record EventMessage(string Event, object Data);
}
