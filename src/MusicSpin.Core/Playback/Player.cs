using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Playback;

/// <summary>
/// Default <see cref="IPlayer"/>: one decoder feeding one output. The output pulls samples on its
/// audio thread through <see cref="PlaybackSource"/>, which applies volume (skipped at 1.0) and
/// detects end of stream.
/// </summary>
public sealed class Player : IPlayer
{
    private readonly IAudioDecoderFactory _decoders;
    private readonly IAudioOutputProvider _outputs;
    private readonly PlayerOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();

    private IAudioDecoder? _decoder;
    private PlaybackSource? _source;
    private IAudioOutput? _output;
    private ITimer? _positionTimer;
    private PlayerState _state = PlayerState.Stopped;
    private float _volume = 1f;
    private bool _ended;
    private bool _disposed;

    public Player(
        IAudioDecoderFactory decoders,
        IAudioOutputProvider outputs,
        PlayerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        _outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
        _options = options ?? new PlayerOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<TrackLoadedEventArgs>? TrackLoaded;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<PlayerErrorEventArgs>? Error;

    public PlayerState State
    {
        get { lock (_sync) { return _state; } }
    }

    public TrackInfo? CurrentTrack
    {
        get { lock (_sync) { return _decoder?.Track; } }
    }

    public TimeSpan Position => _source?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration
    {
        get { lock (_sync) { return _decoder?.Track.Duration ?? TimeSpan.Zero; } }
    }

    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f));
    }

    public TrackInfo Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Open first so a bad file doesn't interrupt the current track.
        var decoder = _decoders.Open(path);
        TrackInfo track;
        lock (_sync)
        {
            ReleaseOutput();
            _source?.Dispose();
            _decoder?.Dispose();
            _decoder = decoder;
            _source = new PlaybackSource(decoder, () => Volume);
            _source.Ended += OnSourceEnded;
            _ended = false;
            track = decoder.Track;
            SetState(PlayerState.Stopped);
        }

        TrackLoaded?.Invoke(this, new TrackLoadedEventArgs(track));
        RaisePosition();
        return track;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_source is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "No track loaded.");
            }

            if (_state == PlayerState.Playing)
            {
                return;
            }

            if (_ended)
            {
                _source.Seek(TimeSpan.Zero);
                _ended = false;
            }

            try
            {
                if (_output is null || !_output.IsOpen)
                {
                    _output ??= _outputs.Create(_options.Output);
                    _output.Open(_source);
                }

                _output.Start();
            }
            catch (Exception ex) when (ex is not MusicSpinException)
            {
                ReleaseOutput();
                throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"Could not start audio output: {ex.Message}", ex);
            }

            StartPositionTimer();
            SetState(PlayerState.Playing);
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_state != PlayerState.Playing)
            {
                return;
            }

            _output?.Pause();
            StopPositionTimer();
            SetState(PlayerState.Paused);
        }

        RaisePosition();
    }

    public void Stop()
    {
        lock (_sync)
        {
            ReleaseOutput();
            _source?.Seek(TimeSpan.Zero);
            _ended = false;
            SetState(PlayerState.Stopped);
        }

        RaisePosition();
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_source is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "No track loaded.");
            }

            _source.Seek(position < TimeSpan.Zero ? TimeSpan.Zero : position);
            _ended = false;
        }

        RaisePosition();
    }

    public void Cue(TimeSpan position)
    {
        lock (_sync)
        {
            if (_source is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "No track loaded.");
            }

            if (_state == PlayerState.Playing)
            {
                _output?.Pause();
                StopPositionTimer();
            }

            _source.Seek(position < TimeSpan.Zero ? TimeSpan.Zero : position);
            _ended = false;
            SetState(PlayerState.Paused);
        }

        RaisePosition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            ReleaseOutput();
            _output?.Dispose();
            _output = null;
            _source?.Dispose();
            _decoder?.Dispose();
            _source = null;
            _decoder = null;
        }
    }

    // Raised on the audio thread: hop off it before touching the device.
    private void OnSourceEnded(object? sender, EventArgs e)
        => ThreadPool.UnsafeQueueUserWorkItem(static self => self.HandleEnded(), this, preferLocal: false);

    private void HandleEnded()
    {
        lock (_sync)
        {
            if (_disposed || _state != PlayerState.Playing)
            {
                return;
            }

            ReleaseOutput();
            _ended = true;
            SetState(PlayerState.Stopped);
        }

        RaisePosition();
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseOutput()
    {
        StopPositionTimer();
        try
        {
            _output?.Stop();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, new PlayerErrorEventArgs(ex));
        }
    }

    private void StartPositionTimer()
    {
        _positionTimer?.Dispose();
        var interval = _options.PositionInterval;
        _positionTimer = _time.CreateTimer(static s => ((Player)s!).RaisePosition(), this, interval, interval);
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private void RaisePosition() => PositionChanged?.Invoke(this, new PlayerPositionChangedEventArgs(Position, Duration));

    private void SetState(PlayerState state)
    {
        var old = _state;
        if (old == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new PlayerStateChangedEventArgs(old, state));
    }
}
