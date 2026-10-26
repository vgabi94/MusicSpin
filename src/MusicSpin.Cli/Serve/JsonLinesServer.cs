using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Serve;

/// <summary>
/// The player protocol (<see cref="PlayerProtocol"/>, the same JSON lines the background player speaks) over
/// stdin/stdout, for one private player owned by this process. Events are always on.
/// <para>Request:  {"id":1,"cmd":"load","path":"/music/a.flac"}</para>
/// <para>Response: {"id":1,"ok":true,"data":{...}} or {"id":1,"ok":false,"error":{"code":"file_not_found","exitCode":3,"message":"..."}}</para>
/// <para>Event:    {"event":"stateChanged","data":{"state":"playing"}} (no id; may arrive at any time)</para>
/// </summary>
public sealed class JsonLinesServer
{
    public const int ProtocolVersion = PlayerProtocol.Version;

    private readonly IPlayer _player;
    private readonly IAudioDecoderFactory _decoders;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly OutputInfo? _outputInfo;
    private readonly Lock _writeLock = new();

    /// <summary>Where the <c>radio</c> command gets its tracks (the data folder's library); null: radio is invalid_state.</summary>
    public Func<MusicSpin.Core.Library.RadioFilter, IReadOnlyList<MusicSpin.Core.Library.RadioCandidate>>? RadioPool { get; init; }

    /// <summary>
    /// Volume levelling for the tracks this server plays (the caller owns and disposes it); null: no levelling, and the
    /// <c>normalize</c> command is invalid_state.
    /// </summary>
    public MusicSpin.Core.Normalization.IGainResolver? Normalizer { get; init; }

    /// <summary>The graphic equalizer (#141); null for none.</summary>
    public MusicSpin.Core.Equalization.GraphicEqualizer? Equalizer { get; init; }

    public JsonLinesServer(IPlayer player, IAudioDecoderFactory decoders, TextReader input, TextWriter output, OutputInfo? outputInfo = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _outputInfo = outputInfo;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var queue = new QueuePlayer(_player, _outputInfo) { RadioPool = RadioPool, Normalizer = Normalizer, Equalizer = Equalizer };
        var protocol = new PlayerProtocol(queue, _decoders);
        using var events = PlayerEvents.Attach(queue, Emit);
        Emit(new ProtocolMessages.Event("ready", new { protocol = ProtocolVersion, decoder = _decoders.Name }).ToJson());
        try
        {
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

                var reply = protocol.Handle(line);
                Emit(reply.Line);
                if (reply.Action == ProtocolAction.Quit)
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
            queue.Stop();
        }
    }

    private void Emit(string json)
    {
        lock (_writeLock)
        {
            _output.WriteLine(json);
            _output.Flush();
        }
    }
}
