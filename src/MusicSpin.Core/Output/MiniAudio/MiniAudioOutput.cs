using System.Globalization;
using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>
/// Opens a playback device at the source's own sample rate and channels. Shared mode hands the OS mixer float32;
/// exclusive mode hands the device the source's own depth (s16/s24, f32 for 32-bit and float sources), so with
/// volume 1.0 the original samples reach the DAC unchanged. When the device goes away mid-play, or the system
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

            stream.Pause();
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

    internal static OpenRequest BuildRequest(PcmFormat format, AudioOutputOptions options)
        => new(DeviceSampleFormat(format.SourceBitsPerSample, options.Exclusive), format.SampleRate, format.Channels, options.Exclusive, format.SourceBitsPerSample);

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

    /// <summary>Opens a stream (under the gate, none being open) and starts listening to it.</summary>
    private void OpenCore(BackendDevice? device, IPcmSource source)
    {
        var stream = provider.Engine.Open(device, BuildRequest(source.Format, options), source);
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
        _stream = null;
        _opened = null;
        _started = false;
        lock (_pendingLock)
        {
            _pendingFor = null; // a pending notification was about this device: drop it
            _change = Change.None;
            _lostReason = null;
        }

        stream?.Dispose();
    }
}
