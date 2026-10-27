using System.Globalization;
using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>
/// Opens a playback device at the source's channels. Shared mode opens it at the OS mixer's own rate and hands it
/// float32: a source of another rate is converted here (libsamplerate's best sinc, <see cref="StreamRoute"/>), once,
/// instead of by the mixer, and one at the mixer's rate passes bit for bit. Exclusive mode opens the source's own rate
/// and hands the device the source's own depth (s16/s24, f32 for 32-bit and float sources), so with
/// volume 1.0 the original samples reach the DAC unchanged; a device that takes only 16-bit gets a deeper track dithered
/// to s16, with a notice saying so (still the track's own rate, unlike the shared mixer). When the device goes away mid-play, or the system
/// default changes under a stream that cannot follow it, the output reopens on the system default from the same source
/// (<see cref="Rerouted"/>); only when that fails does <see cref="Failed"/> fire and the output close. An exclusive
/// device is released while paused (a stopped exclusive stream still holds the device against every other app) and
/// reopened on the same source when playback resumes.
///
/// Locking: <c>_gate</c> serializes everything that opens, closes, starts or pauses a device, and is held while a
/// device is disposed, which waits for that device's audio and notification threads. Those threads report device
/// changes through <see cref="Signal"/>, which therefore never takes the gate: it only takes a tiny lock around the
/// pending-change fields, and drops reports from any device that is no longer the current one. Events are raised with
/// no lock held.
/// </summary>
internal sealed class MiniAudioOutput(MiniAudioOutputProvider provider, AudioOutputOptions options) : IAudioOutput
{
    // Serializes open/close/start/pause/stop and the worker's reopen; held across stream Dispose. Signal never takes it.
    private readonly Lock _gate = new();

    // Read without the gate (IsOpen, Opened, tests) and written under it; a closed or released stream is null.
    private volatile IPlaybackStream? _stream;
    private volatile OpenedOutput? _opened;

    // Under _gate.
    private IPcmSource? _source;

    // What the open stream actually pulls from: it reads whatever this points at, so a track change can move it to
    // another source without closing the device. Null while no stream is open.
    private volatile SwitchableSource? _pump;
    private bool _started;

    // An exclusive device was released by Pause: the source is kept and Start reopens the device for it.
    private bool _suspended;

    // The chosen device went away and playback moved to the system default: later opens use the default too, until
    // the chosen device is listed again.
    private string? _missingDevice;

    // Device changes waiting for the worker, under _pendingLock only (the audio threads reach them through Signal while
    // the gate may be held across a Dispose that waits for them). Repeated notifications fold into one, the strongest
    // winning; _pendingFor says which stream they are about.
    private readonly Lock _pendingLock = new();
    private IPlaybackStream? _pendingFor;
    private Change _change;
    private string? _lostReason;
    private bool _workerQueued;

    private enum Change
    {
        None,
        Rerouted,
        DefaultChanged,
        Lost,
    }

    /// <summary>Open, whether playing or paused (an exclusive device released by a pause still counts: it reopens on Start).</summary>
    public bool IsOpen => _stream is not null || _suspended;

    /// <inheritdoc />
    public OpenedOutput? Opened => _opened;

    public void Open(IPcmSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            CloseStream();
            _source = null;
            _suspended = false;
            try
            {
                _source = source;
                OpenCore(TargetDevice(), source);
            }
            catch (Exception ex)
            {
                CloseStream();
                _source = null;
                throw Wrap(ex);
            }
        }
    }

    public void Start()
    {
        AudioOutputReroutedEventArgs? moved = null;
        lock (_gate)
        {
            if (_source is not { } source || (_stream is null && !_suspended))
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "Output is not open.");
            }

            if (_suspended)
            {
                moved = Resume(source);
            }

            _stream!.Start();
            _started = true;
        }

        if (moved is not null)
        {
            RaiseRerouted(moved);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _started = false;
            if (_stream is not { } stream)
            {
                return;
            }

            _ = stream.Pause(); // a stream that did not confirm is closed just below (exclusive) or at the next Stop/Open
            if (_opened is { Exclusive: true })
            {
                // Hand the device back to the system: another app (or this one's next track) can use it while paused.
                var opened = _opened;
                CloseStream();
                _opened = opened;
                _suspended = true;
            }
        }
    }

    public bool Retarget(IPcmSource next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            if (_source is not { } current || (_stream is null && !_suspended))
            {
                return false;
            }

            if (!SameDevice(BuildRequest(current.Format, options), BuildRequest(next.Format, options)))
            {
                return false; // the device would have to be reconfigured
            }

            // Shared mode: any rate plays on the open stream, converted to the rate it reads at.
            StreamRoute? route = null;
            if (_pump is { } open && !TryRoute(next, open.Route.Format.SampleRate, Exclusive, out route))
            {
                return false; // no converter: the caller reopens at the track's format... and the mixer converts
            }

            // Stop reading the old source first (a stopped stream makes no more callbacks), then point the stream at the
            // new one: the old source is never read again and the new one not before Start. A released exclusive device
            // stays released: Start reopens it for _source, which is now the new one.
            if (_started)
            {
                var stopped = _stream!.Pause();
                _started = false;
                if (!stopped)
                {
                    route?.Dispose();
                    // The device thread did not stop in time (WASAPI waits a bounded 2 s): it may still be reading the old
                    // source, so the stream must never be pointed at another one. Close it; the caller opens it again.
                    CloseStream();
                    _source = null;
                    _suspended = false;
                    return false;
                }
            }

            if (_pump is { } pump && route is not null)
            {
                pump.Switch(route).Dispose(); // not read any more: the stream is stopped
            }

            _source = next;
            return true;
        }
    }

    /// <inheritdoc />
    public IPcmConverter? CreateConverter(PcmFormat from, PcmFormat to) => Exclusive ? null : Converter(from, to);

    private MiniAudioConverter? Converter(PcmFormat from, PcmFormat to)
    {
        if (from.Channels <= 0 || to.Channels <= 0 || from.SampleRate <= 0 || to.SampleRate <= 0)
        {
            return null; // nothing to convert from or to
        }

        try
        {
            _ = provider.Engine; // loads and checks the native layer
            return new MiniAudioConverter(from, to);
        }
        catch (Exception ex) when (ex is MusicSpinException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null; // the caller reopens the device instead
        }
    }

    /// <inheritdoc />
    public PcmFormat? StreamFormat => Exclusive ? null : _pump?.Route.Format;

    /// <summary>
    /// Whether the device plays each track at its own rate: what it opened as, once open (an exclusive open the device
    /// refused falls back to shared mode at the mixer's rate, and must then be converted to it like any shared stream),
    /// else what was asked. Never the option alone: a shared stream read as exclusive plays every track at the wrong speed.
    /// </summary>
    private bool Exclusive => _stream is not null || _suspended ? _openedExclusive : options.Exclusive;

    public void Stop()
    {
        lock (_gate)
        {
            CloseStream();
            _source = null;
            _suspended = false;
        }
    }

    public void Dispose() => Stop();

    /// <inheritdoc />
    public event EventHandler<AudioOutputFailedEventArgs>? Failed;

    /// <inheritdoc />
    public event EventHandler<AudioOutputReroutedEventArgs>? Rerouted;

    /// <summary>The open device stream, for tests.</summary>
    internal IPlaybackStream? Stream => _stream;

    /// <summary>The sample format we write: float32 in shared mode (the OS mixer is float), the source's depth in exclusive.</summary>
    internal static DeviceSample DeviceSampleFormat(int sourceBits, bool exclusive) => !exclusive ? DeviceSample.F32 : sourceBits switch
    {
        > 0 and <= 16 when sourceBits != 1 => DeviceSample.S16,
        > 16 and <= 24 => DeviceSample.S24,
        // 32-bit int sources, float sources and DSD (decoded to float PCM) go out as float.
        _ => DeviceSample.F32,
    };

    /// <summary>
    /// Whether one open device serves both requests. The request's <see cref="DeviceSample"/> already says what the device
    /// is opened with (float32 in shared mode, the source's depth class in exclusive), so the track's own bit depth, which
    /// only picks that, does not matter further: a 16-bit and a 24-bit track share a shared-mode device, and a 32-bit
    /// integer and a float track share an exclusive float32 one.
    /// </summary>
    private static bool SameDevice(OpenRequest a, OpenRequest b)
        => a with { SourceBits = 0 } == b with { SourceBits = 0 };

    /// <summary>Shared mode asks for rate 0: the device's own (the mixer's), whatever the track's.</summary>
    internal static OpenRequest BuildRequest(PcmFormat format, AudioOutputOptions options)
        => new(DeviceSampleFormat(format.SourceBitsPerSample, options.Exclusive), options.Exclusive ? format.SampleRate : 0, format.Channels, options.Exclusive, format.SourceBitsPerSample);

    /// <summary>
    /// How <paramref name="source"/> is read at <paramref name="rate"/>: as it is when it is at that rate (or the stream
    /// reads each source at its own, exclusive), else through a converter. False when no converter is available.
    /// </summary>
    private bool TryRoute(IPcmSource source, int rate, bool exclusive, out StreamRoute? route)
    {
        route = null;
        var from = source.Format;
        if (exclusive || rate <= 0 || from.SampleRate == rate)
        {
            route = new StreamRoute(source, null);
            return true;
        }

        if (Converter(from, new PcmFormat(rate, from.Channels, from.SourceBitsPerSample)) is not { } converter)
        {
            return false;
        }

        route = new StreamRoute(source, converter);
        return true;
    }

    /// <summary>
    /// Reopens the device a pause released, for the same source: the source was not read since, so playback goes on from
    /// the same sample. The chosen device is tried first; when it has gone away meanwhile the system default is used
    /// (and said so). Fails (and stays paused, retryable) when the device does not open. Returns what to report when the device or
    /// format differs from before.
    /// </summary>
    private AudioOutputReroutedEventArgs? Resume(IPcmSource source)
    {
        var before = _opened;
        try
        {
            BackendDevice? device;
            try
            {
                device = TargetDevice();
            }
            catch (MusicSpinException) when (options.DeviceId is { } chosen)
            {
                // The chosen device is gone (unplugged while paused: nothing was open to notice): the default, with a notice.
                _missingDevice = before?.Device ?? chosen;
                device = null;
            }

            OpenCore(device, source);
        }
        catch (Exception ex)
        {
            CloseStream();
            _opened = before; // still the paused device, so the player keeps its output and a later Play retries
            throw Wrap(ex);
        }

        _suspended = false;
        var opened = _opened!;
        if (before is not null && Same(before, opened))
        {
            return null;
        }

        var message = _missingDevice is not null ? opened.Notice
            : options.Exclusive && !opened.Exclusive ? opened.Notice
            : null;
        return new AudioOutputReroutedEventArgs(opened, message);
    }

    private static bool Same(OpenedOutput a, OpenedOutput b)
        => a.Device == b.Device && a.Exclusive == b.Exclusive && a.SampleRate == b.SampleRate && a.Channels == b.Channels
           && a.SampleFormat == b.SampleFormat && a.BitsPerSample == b.BitsPerSample && a.Notice == b.Notice;

    /// <summary>The chosen device; null (the system default) when none is chosen or the chosen one has gone away.</summary>
    private BackendDevice? TargetDevice()
    {
        if (options.DeviceId is not { } id)
        {
            return null;
        }

        try
        {
            var device = provider.ResolveDevice(id);
            _missingDevice = null; // it is back: use it again
            return device;
        }
        catch (MusicSpinException) when (_missingDevice is not null)
        {
            return null;
        }
    }

    // What the open (or pause-released) stream opened as: see Exclusive. Read off the gate by StreamFormat.
    private volatile bool _openedExclusive;

    /// <summary>Opens a stream (under the gate, none being open) and starts listening to it.</summary>
    private void OpenCore(BackendDevice? device, IPcmSource source)
    {
        // The stream does not read before Start, so the route can be fitted to the rate it opened at first.
        var pump = new SwitchableSource(new StreamRoute(source, null));
        var stream = provider.Engine.Open(device, BuildRequest(source.Format, options), pump);
        var wasExclusive = _openedExclusive;
        _openedExclusive = stream.Opened.Exclusive; // a refused exclusive open comes back shared: converted like one
        if (!TryRoute(source, stream.ClientSampleRate, _openedExclusive, out var route))
        {
            _openedExclusive = wasExclusive;
            stream.Dispose();
            throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError,
                string.Create(CultureInfo.InvariantCulture, $"Cannot convert {source.Format.SampleRate} Hz to the device's {stream.ClientSampleRate} Hz."));
        }

        pump.Switch(route!).Dispose();
        _pump = pump;
        _stream = stream;
        _opened = null;
        stream.Lost += (_, reason) => Signal(stream, Change.Lost, reason);
        stream.Rerouted += (_, _) => Signal(stream, Change.Rerouted, null);
        stream.DefaultDeviceChanged += (_, _) => Signal(stream, Change.DefaultChanged, null);
        _opened = Review(stream.Opened, source.Format, options, _missingDevice);
    }

    /// <summary>
    /// A device notification from a backend thread (the audio thread cannot close its own device, and Windows'
    /// notification thread must not block): record it and hand off to one worker. Notifications that arrive before the
    /// worker runs fold into one; ones from a device already replaced or released are dropped. Never takes the gate: the
    /// gate is held while a device is disposed, and disposing waits for the very thread that is calling here.
    /// </summary>
    private void Signal(IPlaybackStream stream, Change change, string? reason)
    {
        if (!ReferenceEquals(_stream, stream))
        {
            return; // closed or replaced (possibly right now, by the thread that is waiting for this one)
        }

        lock (_pendingLock)
        {
            if (!ReferenceEquals(_pendingFor, stream))
            {
                _pendingFor = stream;
                _change = Change.None;
                _lostReason = null;
            }

            if (change > _change)
            {
                _change = change;
                _lostReason = reason;
            }

            if (_workerQueued)
            {
                return;
            }

            _workerQueued = true;
        }

        provider.Post(HandleChange);
    }

    private void HandleChange()
    {
        IPlaybackStream? about;
        Change change;
        string? reason;
        lock (_pendingLock)
        {
            about = _pendingFor;
            change = _change;
            reason = _lostReason;
            _pendingFor = null;
            _change = Change.None;
            _lostReason = null;
            _workerQueued = false;
        }

        AudioOutputReroutedEventArgs? rerouted = null;
        string? failed = null;
        lock (_gate)
        {
            if (change == Change.None || about is null || !ReferenceEquals(_stream, about) || _source is not { } source)
            {
                return; // closed, released or replaced meanwhile
            }

            if (change == Change.Rerouted)
            {
                _opened = Review(about.Opened, source.Format, options, _missingDevice);
                rerouted = new AudioOutputReroutedEventArgs(_opened, null);
            }
            else
            {
                (rerouted, failed) = Reopen(source, change == Change.Lost ? reason : null);
            }
        }

        if (failed is not null)
        {
            Failed?.Invoke(this, new AudioOutputFailedEventArgs(failed));
        }
        else if (rerouted is not null)
        {
            RaiseRerouted(rerouted);
        }
    }

    private void RaiseRerouted(AudioOutputReroutedEventArgs e) => Rerouted?.Invoke(this, e);

    /// <summary>
    /// Moves to the system default (the device was lost, or the default changed under a stream that cannot follow it),
    /// pulling on from the same source, so playback goes on from where it was. Started again only if it was playing.
    /// </summary>
    private (AudioOutputReroutedEventArgs? Rerouted, string? Failed) Reopen(IPcmSource source, string? lostReason)
    {
        var wasStarted = _started;
        var previous = _opened?.Device;
        CloseStream();
        if (lostReason is not null && options.DeviceId is { } chosen && _missingDevice is null)
        {
            _missingDevice = previous ?? chosen;
        }

        try
        {
            OpenCore(null, source);
            if (wasStarted)
            {
                _stream!.Start();
                _started = true;
            }
        }
        catch (Exception ex)
        {
            CloseStream();
            _source = null;
            var why = Wrap(ex).Message;
            return (null, lostReason is null ? why : $"{lostReason} {why}");
        }

        var opened = _opened!;
        var message = lostReason is not null && options.DeviceId is not null ? opened.Notice
            : options.Exclusive && !opened.Exclusive ? opened.Notice
            : null;
        return (new AudioOutputReroutedEventArgs(opened, message), null);
    }

    private Exception Wrap(Exception ex) => ex as MusicSpinException
        ?? new MusicSpinException(
            MusicSpinErrorCode.AudioDeviceError,
            $"Could not open audio device{(options.Exclusive ? " in exclusive mode" : string.Empty)}: {ex.Message}", ex);

    /// <summary>
    /// Checks what the device opened at against what was asked: exclusive asked but shared given, or a verified rate
    /// that is not the track's, become a notice, so nothing claims exclusive or bit-perfect when it is not.
    /// </summary>
    internal static OpenedOutput Review(OpenedOutput opened, PcmFormat source, AudioOutputOptions options, string? missingDevice = null)
    {
        var notices = new List<string>();
        if (missingDevice is not null)
        {
            notices.Add($"'{missingDevice}' is not available: playing on the system default device.");
        }

        if (opened.Notice is { } notice)
        {
            notices.Add(notice);
        }
        else if (options.Exclusive && !opened.Exclusive)
        {
            notices.Add("Exclusive mode was requested, but the device opened in shared mode.");
        }

        if (opened.Verified && opened.SampleRate != source.SampleRate)
        {
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"The device runs at {opened.SampleRate / 1000.0:0.###} kHz, not the track's {source.SampleRate / 1000.0:0.###} kHz: the audio is resampled."));
        }

        return opened with { Notice = notices.Count == 0 ? null : string.Join(" ", notices) };
    }

    /// <summary>
    /// Swaps the current stream out first, so reports from it are dropped as stale from then on, then disposes it, which
    /// waits for its audio and notification threads. Under the gate only.
    /// </summary>
    private void CloseStream()
    {
        var stream = _stream;
        var pump = _pump;
        _stream = null;
        _pump = null;
        _opened = null;
        _started = false;
        lock (_pendingLock)
        {
            _pendingFor = null; // a pending notification was about this device: drop it
            _change = Change.None;
            _lostReason = null;
        }

        stream?.Dispose();
        pump?.Route.Dispose(); // after the stream, which no longer reads it
    }
}

/// <summary>
/// The source an open stream reads: forwards to a route that <see cref="Switch"/> can move to another source while the
/// stream is stopped and stays open. Read is a volatile load and a call.
/// </summary>
internal sealed class SwitchableSource(StreamRoute route) : IPcmSource
{
    private StreamRoute _route = route;

    /// <summary>What is read now.</summary>
    public StreamRoute Route => Volatile.Read(ref _route);

    /// <summary>The source read now (the route's).</summary>
    public IPcmSource Target => Route.Source;

    public PcmFormat Format => Route.Format;

    public int Read(Span<float> buffer) => Route.Read(buffer);

    /// <summary>Reads <paramref name="next"/> from now on; returns the route it replaces (the caller disposes it).</summary>
    public StreamRoute Switch(StreamRoute next) => Interlocked.Exchange(ref _route, next);
}

/// <summary>
/// A source as a shared-mode stream reads it: as it is, or converted to the stream's rate (the OS mixer's) a few
/// hundred frames at a time, so the source's position stays within milliseconds of what is played. Read on the audio
/// thread: no allocation, no lock. A seek of the source keeps the filter's last few milliseconds (like the device's own
/// buffer does). At the source's end (a read of 0) the filter's last frames, the track's last few milliseconds, are
/// drained into the same callback before the route reports its own end; a source that has more again after its end (a
/// seek back) starts the filter afresh.
/// </summary>
internal sealed class StreamRoute : IPcmSource, IDisposable
{
    private const int StagedFrames = 256;

    private readonly IPcmConverter? _converter;
    private readonly float[] _staged = [];
    private readonly int _channels;
    private int _stagedStart;
    private int _stagedEnd;

    // The source said it ended: the filter is being drained (and is empty once _drained).
    private bool _ending;
    private bool _drained;

    public StreamRoute(IPcmSource source, IPcmConverter? converter)
    {
        Source = source;
        _converter = converter;
        _channels = Math.Max(1, source.Format.Channels);
        Format = converter is null ? source.Format : new PcmFormat(converter.To.SampleRate, converter.To.Channels, source.Format.SourceBitsPerSample);
        if (converter is not null)
        {
            _staged = new float[StagedFrames * _channels];
        }
    }

    public IPcmSource Source { get; }

    /// <summary>The rate and channels the stream reads.</summary>
    public PcmFormat Format { get; }

    /// <summary>Whether the source is converted.</summary>
    public bool Converts => _converter is not null;

    public int Read(Span<float> buffer)
    {
        if (_converter is null)
        {
            return Source.Read(buffer);
        }

        var outChannels = _converter.To.Channels;
        var room = buffer.Length - (buffer.Length % outChannels);
        var written = 0;
        while (written < room)
        {
            if (_stagedStart == _stagedEnd)
            {
                // Read only about what the output needs, so little is taken ahead of what is heard.
                var wantFrames = (long)(room - written) / outChannels * _converter.From.SampleRate / Math.Max(1, _converter.To.SampleRate) + 1;
                var want = (int)Math.Min(StagedFrames, wantFrames) * _channels;
                var read = Source.Read(_staged.AsSpan(0, want));
                read -= read % _channels;
                _stagedStart = 0;
                _stagedEnd = Math.Max(read, 0);
                if (read <= 0)
                {
                    // The end (an underrun reads silence, not 0): what the filter still holds is the track's last frames.
                    if (!_ending)
                    {
                        _ending = true;
                        _drained = false;
                    }

                    if (!_drained)
                    {
                        var tail = _converter.Drain(buffer[written..room]);
                        if (tail < 0 || tail > room - written || tail % outChannels != 0)
                        {
                            tail = 0; // a broken converter: never write past the buffer on the audio thread
                        }

                        _drained = tail == 0;
                        written += tail;
                    }

                    break; // the rest (if any) in the next callback; the end once drained
                }

                if (_ending)
                {
                    // More after the end (the source was sought back): a fresh filter, not the drained one.
                    _converter.Reset();
                    _ending = false;
                    _drained = false;
                }

                foreach (ref var x in _staged.AsSpan(0, read))
                {
                    if (!float.IsFinite(x))
                    {
                        x = 0; // a NaN would smear through the filter
                    }
                }
            }

            var produced = _converter.Convert(_staged.AsSpan(_stagedStart, _stagedEnd - _stagedStart), buffer[written..room], out var consumed);
            if (produced <= 0 && consumed <= 0)
            {
                break; // cannot happen with whole frames on both sides; never spin on the audio thread
            }

            _stagedStart += consumed;
            written += produced;
        }

        return written;
    }

    public void Dispose() => _converter?.Dispose();
}
