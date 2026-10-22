using System.Globalization;
using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>
/// Opens a playback device at the source's own sample rate and channels. Shared mode hands the OS mixer float32;
/// exclusive mode hands the device the source's own depth (s16/s24, f32 for 32-bit and float sources), so with
/// volume 1.0 the original samples reach the DAC unchanged. When the device goes away mid-play, or the system
/// default changes under a stream that cannot follow it, the output reopens on the system default from the same source
/// (<see cref="Rerouted"/>); only when that fails does <see cref="Failed"/> fire and the output close.
/// </summary>
internal sealed class MiniAudioOutput(MiniAudioOutputProvider provider, AudioOutputOptions options) : IAudioOutput
{
    private readonly Lock _lock = new();
    private IPlaybackStream? _stream;
    private OpenedOutput? _opened;
    private IPcmSource? _source;
    private bool _started;

    // The chosen device went away and playback moved to the system default: later opens use the default too, until
    // the chosen device is listed again.
    private string? _missingDevice;

    // Device changes waiting for the worker; repeated notifications fold into one (the strongest wins).
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

    public bool IsOpen
    {
        get { lock (_lock) { return _stream is not null; } }
    }

    /// <inheritdoc />
    public OpenedOutput? Opened
    {
        get { lock (_lock) { return _opened; } }
    }

    public void Open(IPcmSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stop();

        lock (_lock)
        {
            try
            {
                _source = source;
                OpenCore(TargetDevice(), source);
            }
            catch (Exception ex)
            {
                DisposeDevice();
                throw Wrap(ex);
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_stream is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "Output is not open.");
            }

            _stream.Start();
            _started = true;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _started = false;
            _stream?.Pause();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            DisposeDevice();
            _source = null;
        }
    }

    public void Dispose() => Stop();

    /// <inheritdoc />
    public event EventHandler<AudioOutputFailedEventArgs>? Failed;

    /// <inheritdoc />
    public event EventHandler<AudioOutputReroutedEventArgs>? Rerouted;

    /// <summary>The open device stream, for tests.</summary>
    internal IPlaybackStream? Stream
    {
        get { lock (_lock) { return _stream; } }
    }

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

    private void OpenCore(BackendDevice? device, IPcmSource source)
    {
        var stream = provider.Engine.Open(device, BuildRequest(source.Format, options), source);
        _stream = stream;
        stream.Lost += (_, reason) => Signal(stream, Change.Lost, reason);
        stream.Rerouted += (_, _) => Signal(stream, Change.Rerouted, null);
        stream.DefaultDeviceChanged += (_, _) => Signal(stream, Change.DefaultChanged, null);
        _opened = Review(stream.Opened, source.Format, options, _missingDevice);
    }

    /// <summary>
    /// A device notification from a backend thread (the audio thread cannot close its own device, and Windows'
    /// notification thread must not block): record it and hand off to one worker. Notifications that arrive before the
    /// worker runs fold into one; ones from a device already replaced are dropped.
    /// </summary>
    private void Signal(IPlaybackStream stream, Change change, string? reason)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_stream, stream))
            {
                return;
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
        AudioOutputReroutedEventArgs? rerouted = null;
        string? failed = null;
        lock (_lock)
        {
            var change = _change;
            var reason = _lostReason;
            _change = Change.None;
            _lostReason = null;
            _workerQueued = false;
            if (_stream is not { } stream || _source is not { } source || change == Change.None)
            {
                return; // closed or reopened meanwhile
            }

            if (change == Change.Rerouted)
            {
                _opened = Review(stream.Opened, source.Format, options, _missingDevice);
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
            Rerouted?.Invoke(this, rerouted);
        }
    }

    /// <summary>
    /// Moves to the system default (the device was lost, or the default changed under a stream that cannot follow it),
    /// pulling on from the same source, so playback goes on from where it was. Started again only if it was playing.
    /// </summary>
    private (AudioOutputReroutedEventArgs? Rerouted, string? Failed) Reopen(IPcmSource source, string? lostReason)
    {
        var wasStarted = _started;
        var previous = _opened?.Device;
        DisposeDevice();
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
            DisposeDevice();
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

    private void DisposeDevice()
    {
        _stream?.Dispose();
        _stream = null;
        _opened = null;
        _started = false;
        _change = Change.None; // a pending notification was about this device: drop it
        _lostReason = null;
    }
}
