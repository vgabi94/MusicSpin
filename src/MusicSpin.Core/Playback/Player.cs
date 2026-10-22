using System.Diagnostics;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Playback;

/// <summary>
/// Default <see cref="IPlayer"/>. A dedicated decode thread decodes ahead into a <see cref="PlaybackBuffer"/>; the output's
/// audio callback only copies from it (applying volume, skipped at 1.0). When the current track's decoder reaches its end
/// the thread opens the next entry given by <see cref="SetNextTracks"/> and, if it has the same PCM format, appends it
/// behind a boundary marker so the device plays straight on (gapless). Otherwise an end marker is written and the caller
/// starts the next track the normal way (device reopened).
/// <para>Lock order: <c>_sync</c> (player state), <c>_ioLock</c> (a decoder is being read/opened/sought), <c>_decodeLock</c>
/// (decoder bookkeeping, never held across file I/O so status calls never wait for a slow disk). The decode thread reads
/// outside <c>_decodeLock</c> and commits only if nothing changed meanwhile (<c>_generation</c>). Decoders that are dropped
/// while the thread may be reading them are retired and disposed by the thread itself.</para>
/// </summary>
public sealed class Player : IPlayer
{
    private const int MaxChunkFrames = 4096;

    private readonly IAudioDecoderFactory _decoders;
    private readonly IAudioOutputProvider _outputs;
    private readonly PlayerOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly Lock _decodeLock = new();
    private readonly Lock _signalLock = new();
    private readonly Lock _ioLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _progress = new(false);
    private readonly Notifier _notifier;

    // player state (_sync)
    private IAudioOutput? _output;
    private PlaybackBuffer? _outputSource;
    private ITimer? _positionTimer;
    private PlayerState _state = PlayerState.Stopped;
    private float _volume = 1f;
    private bool _ended;
    private volatile bool _disposed;
    private Thread? _thread;

    // decoder state (_decodeLock)
    private PlaybackBuffer? _buffer;
    private readonly List<TrackSegment> _segments = [];
    private TrackSegment? _writing;
    private bool _writingDone;
    private bool _endQueued;
    private IReadOnlyList<NextTrack> _candidates = [];
    private int _cursor;
    private long _processedCrossed;
    private long _underrunsBefore;
    private float[] _scratch = [];
    private long _generation;
    private readonly List<IAudioDecoder> _retired = [];

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
        _notifier = new Notifier(this);
    }

    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<TrackLoadedEventArgs>? TrackLoaded;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<PlayerErrorEventArgs>? Error;
    public event EventHandler<TrackAdvancedEventArgs>? TrackAdvanced;

    /// <summary>The device was (re)opened for a track's format; raised on the caller's thread, under the player lock.</summary>
    public event EventHandler<OutputOpenedEventArgs>? OutputOpened;

    public PlayerState State
    {
        get { lock (_sync) { return _state; } }
    }

    public TrackInfo? CurrentTrack
    {
        get { lock (_decodeLock) { return _segments.Count > 0 ? _segments[0].Track : null; } }
    }

    /// <summary>Time of the next sample the output will play (what was heard, not what was decoded).</summary>
    public TimeSpan Position => Volatile.Read(ref _buffer)?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration
    {
        get { lock (_decodeLock) { return _segments.Count > 0 ? _segments[0].Track.Duration : TimeSpan.Zero; } }
    }

    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(float.IsFinite(value) ? value : 1f, 0f, 1f));
    }

    public PlaybackDiagnostics Diagnostics
    {
        get
        {
            lock (_decodeLock)
            {
                var buffer = _buffer;
                return new PlaybackDiagnostics(
                    _underrunsBefore + (buffer?.Underruns ?? 0),
                    _options.Gapless,
                    _options.EffectiveBufferMilliseconds,
                    buffer is null ? 0 : Math.Round(buffer.Format.SamplesToTime(buffer.Buffered).TotalMilliseconds, 1));
            }
        }
    }

    /// <summary>The source the output pulls from (tests and diagnostics).</summary>
    internal PlaybackBuffer? Buffer => Volatile.Read(ref _buffer);

    /// <summary>What the output plays, for <see cref="SpectrumMeter"/> (copied only while enabled).</summary>
    internal SpectrumTap SpectrumTap { get; } = new();

    public TrackInfo Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Open first so a bad file doesn't interrupt the current track.
        var decoder = _decoders.Open(path);
        lock (_sync)
        {
            if (_disposed)
            {
                decoder.Dispose();
                throw new ObjectDisposedException(nameof(Player));
            }

            ReleaseOutput();
            lock (_decodeLock)
            {
                ResetDecoding(decoder);
            }

            _ended = false;
            SetState(PlayerState.Stopped);
            EnsureDecodeThread();
        }

        _wake.Set();
        TrackLoaded?.Invoke(this, new TrackLoadedEventArgs(decoder.Track));
        RaisePosition();
        return decoder.Track;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            var buffer = Volatile.Read(ref _buffer)
                         ?? throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "No track loaded.");
            if (_state == PlayerState.Playing)
            {
                return;
            }

            if (_ended)
            {
                SeekCore(TimeSpan.Zero);
                _ended = false;
            }

            WaitForPrefill(buffer);
            try
            {
                if (_output is null || !_output.IsOpen || !ReferenceEquals(_outputSource, buffer))
                {
                    if (_output is null)
                    {
                        _output = _outputs.Create(_options.Output);
                        _output.Failed += OnOutputFailed;
                    }

                    _output.Open(buffer);
                    _outputSource = buffer;
                    if (_output.Opened is { } opened)
                    {
                        OutputOpened?.Invoke(this, new OutputOpenedEventArgs(opened));
                    }
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

            _output?.Pause(); // the decode thread tops the buffer up, then idles
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
            if (Volatile.Read(ref _buffer) is not null)
            {
                SeekCore(TimeSpan.Zero);
            }

            _ended = false;
            SetState(PlayerState.Stopped);
        }

        RaisePosition();
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _buffer) is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "No track loaded.");
            }

            SeekCore(position < TimeSpan.Zero ? TimeSpan.Zero : position);
            _ended = false;
        }

        RaisePosition();
    }

    public void Cue(TimeSpan position)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _buffer) is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "No track loaded.");
            }

            if (_state == PlayerState.Playing)
            {
                _output?.Pause();
                StopPositionTimer();
            }

            SeekCore(position < TimeSpan.Zero ? TimeSpan.Zero : position);
            _ended = false;
            SetState(PlayerState.Paused);
        }

        RaisePosition();
    }

    public void SetNextTracks(IReadOnlyList<NextTrack> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (_disposed)
        {
            return;
        }

        lock (_decodeLock)
        {
            if (SameEntries(next, _candidates))
            {
                return;
            }

            var buffer = _buffer;
            if (buffer is null || buffer.EndReached)
            {
                _candidates = next;
                _cursor = 0;
                return;
            }

            // Keep what is already decoded ahead only if the new order starts with exactly those entries.
            var chain = Chain();
            if (!StartsWith(next, chain))
            {
                CutPending();
                chain = Chain();
                if (!StartsWith(next, chain))
                {
                    // The output already played into a track the caller has not been told about yet: wait for its
                    // TrackAdvanced (the caller then sends the order relative to that track).
                    _candidates = [];
                    _cursor = 0;
                    return;
                }
            }
            else if (_endQueued && buffer.TryRemoveTrailingEnd())
            {
                _endQueued = false; // more entries to try than when the end was written
                _generation++;
            }

            _candidates = next;
            _cursor = chain.Count;
        }

        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wake.Set();
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }

        lock (_sync)
        {
            ReleaseOutput();
            if (_output is not null)
            {
                _output.Failed -= OnOutputFailed;
                _output.Dispose();
                _output = null;
            }
            lock (_decodeLock)
            {
                foreach (var segment in _segments)
                {
                    segment.Decoder.Dispose();
                }

                foreach (var decoder in _retired)
                {
                    decoder.Dispose();
                }

                _segments.Clear();
                _retired.Clear();
                _writing = null;
                Volatile.Write(ref _buffer, null);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ decoding

    private void ResetDecoding(IAudioDecoder decoder)
    {
        _generation++;
        _retired.AddRange(_segments.Select(x => x.Decoder));
        _segments.Clear();
        var first = new TrackSegment(decoder, null, TimeSpan.Zero, [], []);
        var format = decoder.Format;
        var frames = Math.Max(1024L, (long)Math.Max(format.SampleRate, 1) * _options.EffectiveBufferMilliseconds / 1000);
        var channels = Math.Max(format.Channels, 1);
        var old = _buffer;
        _underrunsBefore += old?.Underruns ?? 0;
        Volatile.Write(ref _buffer, new PlaybackBuffer(format, (int)Math.Min(frames * channels, int.MaxValue / 2), first, () => Volume, _notifier.Signal, SpectrumTap));
        _segments.Add(first);
        _writing = first;
        _writingDone = false;
        _endQueued = false;
        _candidates = [];
        _cursor = 0;
        _processedCrossed = 0;
        var chunk = MaxChunkFrames * channels;
        if (_scratch.Length != chunk)
        {
            _scratch = new float[chunk];
        }
    }

    /// <summary>Repositions the track the caller considers current and drops everything decoded ahead. Caller holds _sync.</summary>
    private void SeekCore(TimeSpan position)
    {
        lock (_ioLock)
        lock (_decodeLock)
        {
            var buffer = _buffer!;
            var head = _segments[0];
            if (!head.Decoder.CanSeek)
            {
                return; // a stream that cannot seek keeps playing what is buffered
            }

            _generation++;
            for (var i = 1; i < _segments.Count; i++)
            {
                _retired.Add(_segments[i].Decoder);
            }

            _segments.RemoveRange(1, _segments.Count - 1);
            head.Decoder.Seek(position);

            head.Start = head.Decoder.Position;
            buffer.Flush(head);
            _processedCrossed = buffer.Crossed;
            _writing = head;
            _writingDone = false;
            _endQueued = false;
            _cursor = 0;
        }

        _wake.Set();
    }

    /// <summary>Drops pre-decoded next tracks (and a pending end) the output has not reached. Caller holds _decodeLock.</summary>
    private void CutPending()
    {
        var buffer = _buffer!;
        var dropped = buffer.CutAtFirstMarker();
        _generation++;
        for (var i = 0; i < dropped && _segments.Count > 1; i++)
        {
            _retired.Add(_segments[^1].Decoder);
            _segments.RemoveAt(_segments.Count - 1);
        }

        if (dropped > 0 || _endQueued)
        {
            _writing = _segments[^1];
            _writingDone = true; // a marker only ever follows a track that was decoded to its end
            _endQueued = false;
        }
    }

    /// <summary>Next-track entries used by the segments after the one the caller considers current.</summary>
    private List<NextTrack> Chain()
    {
        var chain = new List<NextTrack>();
        for (var i = 1; i < _segments.Count; i++)
        {
            chain.AddRange(_segments[i].Entries);
        }

        return chain;
    }

    private void EnsureDecodeThread()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "MusicSpin decoder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void DecodeLoop()
    {
        var idleWait = TimeSpan.FromMilliseconds(Math.Clamp(_options.EffectiveBufferMilliseconds / 5, 5, 20));
        while (true)
        {
            // Reset before checking _disposed: a Dispose that sets the event after this point is seen below or wakes the wait.
            _wake.Reset();
            if (_disposed)
            {
                return;
            }

            Step result;
            Exception? error = null;
            try
            {
                result = DecodeStep(out error);
            }
            catch (Exception ex)
            {
                // Never let the decode thread die: report and stop decoding this stream.
                result = Step.Idle;
                error = ex;
            }

            if (error is not null)
            {
                Error?.Invoke(this, new PlayerErrorEventArgs(error));
            }

            if (result == Step.Progress)
            {
                _progress.Set();
                continue;
            }

            _progress.Set();
            _wake.Wait(result == Step.Full ? idleWait : TimeSpan.FromSeconds(1));
        }
    }

    private enum Step
    {
        Progress,
        Full,
        Idle,
    }

    private Step DecodeStep(out Exception? error)
    {
        error = null;
        PlaybackBuffer buffer;
        TrackSegment writing;
        IReadOnlyList<NextTrack> candidates;
        int cursor, want;
        float[] scratch;
        long generation;
        bool chain;
        lock (_ioLock)
        {
            DisposeRetired();
            lock (_decodeLock)
            {
                if (_disposed || _buffer is null || _writing is null)
                {
                    return Step.Idle;
                }

                buffer = _buffer;
                writing = _writing;
                scratch = _scratch; // Load may swap in another array meanwhile
                var channels = Math.Max(buffer.Format.Channels, 1);
                want = Math.Min(scratch.Length, Math.Max(buffer.Capacity / 8, channels));
                want -= want % channels;
                if (_writingDone && _endQueued)
                {
                    return Step.Idle;
                }

                if (_writingDone && (!_options.Gapless || buffer.PendingMarkers >= PlaybackBuffer.MaxMarkers - 1))
                {
                    QueueEnd(buffer);
                    return Step.Progress;
                }

                if (buffer.Free < want)
                {
                    return Step.Full;
                }

                chain = _writingDone;
                candidates = _candidates;
                cursor = _cursor;
                generation = _generation;
            }

            // File I/O below runs without _decodeLock: status, next-track updates and Load never wait for it.
            if (!chain)
            {
                int read;
                try
                {
                    read = writing.Decoder.Read(scratch.AsSpan(0, want));
                }
                catch (Exception ex)
                {
                    // Reported as a player error; what was decoded still plays, then the stream continues with the next
                    // track (gapless) or ends, exactly as if the file ended here.
                    error = ex is MusicSpinException ? ex : new MusicSpinException(MusicSpinErrorCode.DecodeFailed, $"Decoding failed: {ex.Message}", ex);
                    read = 0;
                }

                lock (_decodeLock)
                {
                    if (generation != _generation)
                    {
                        return Step.Progress; // sought, cut or reloaded meanwhile: these samples are stale
                    }

                    if (read > 0)
                    {
                        buffer.Write(scratch.AsSpan(0, read - (read % Math.Max(buffer.Format.Channels, 1))));
                    }
                    else
                    {
                        _writingDone = true;
                    }
                }

                return Step.Progress;
            }

            var next = OpenNext(_decoders, buffer.Format, candidates, cursor, scratch.AsMemory(0, want));
            lock (_decodeLock)
            {
                if (generation != _generation || !ReferenceEquals(candidates, _candidates) || cursor != _cursor)
                {
                    next?.Segment.Decoder.Dispose();
                    return Step.Progress;
                }

                if (next is null || !buffer.TryAddMarker(MarkerKind.Boundary, next.Segment))
                {
                    next?.Segment.Decoder.Dispose();
                    QueueEnd(buffer);
                    return Step.Progress;
                }

                buffer.Write(scratch.AsSpan(0, next.Read));
                _segments.Add(next.Segment);
                _writing = next.Segment;
                _writingDone = false;
                _cursor = next.NextCursor;
                return Step.Progress;
            }
        }
    }

    private void QueueEnd(PlaybackBuffer buffer)
    {
        if (buffer.TryAddMarker(MarkerKind.End, null))
        {
            _endQueued = true;
        }
    }

    private void DisposeRetired()
    {
        IAudioDecoder[] retired;
        lock (_decodeLock)
        {
            if (_retired.Count == 0)
            {
                return;
            }

            retired = [.. _retired];
            _retired.Clear();
        }

        foreach (var decoder in retired)
        {
            decoder.Dispose();
        }
    }

    private sealed record Opened(TrackSegment Segment, int Read, int NextCursor);

    /// <summary>
    /// Opens the first playable entry from <paramref name="cursor"/> on and decodes its first block into the scratch buffer.
    /// Null when nothing can follow gaplessly: no more entries, a different format, an empty or failing track (the caller
    /// ends the stream there and the normal path takes over, reporting errors and reopening the device as needed).
    /// </summary>
    private static Opened? OpenNext(IAudioDecoderFactory decoders, PcmFormat format, IReadOnlyList<NextTrack> candidates, int cursor, Memory<float> scratch)
    {
        var skipped = new List<SkippedNextTrack>();
        for (var i = cursor; i < candidates.Count; i++)
        {
            var entry = candidates[i];
            IAudioDecoder decoder;
            try
            {
                decoder = decoders.Open(entry.Path);
            }
            catch (MusicSpinException ex) when (ex.Code is MusicSpinErrorCode.FileNotFound or MusicSpinErrorCode.UnsupportedFormat or MusicSpinErrorCode.DecodeFailed)
            {
                skipped.Add(new SkippedNextTrack(entry, ex));
                continue;
            }
            catch (Exception)
            {
                return null; // e.g. decoder unavailable: let the normal path report it
            }

            int read;
            try
            {
                // Same rate, channels and bit depth: the open device can play it as is. Anything else needs a reopen.
                read = decoder.Format == format ? decoder.Read(scratch.Span) : 0;
            }
            catch (Exception)
            {
                read = 0;
            }

            if (read <= 0)
            {
                decoder.Dispose();
                return null;
            }

            var entries = new List<NextTrack>(skipped.Select(x => x.Entry)) { entry };
            var segment = new TrackSegment(decoder, entry.Token, TimeSpan.Zero, entries, skipped);
            return new Opened(segment, read - (read % Math.Max(format.Channels, 1)), i + 1);
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------ output side

    private void WaitForPrefill(PlaybackBuffer buffer)
    {
        var target = Math.Min(buffer.Capacity / 2, buffer.Format.SamplesPerSecond / 4);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            lock (_decodeLock)
            {
                if (buffer.Buffered >= target || _endQueued || _writing is null)
                {
                    return;
                }
            }

            var left = _options.PrefillTimeout - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return; // start anyway; the buffer reports the underruns
            }

            _progress.Reset();
            _wake.Set();
            _progress.Wait(left < TimeSpan.FromMilliseconds(20) ? left : TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>Boundaries crossed / end reached by the output; runs on the thread pool, never on the audio thread.</summary>
    private void OnBufferSignal()
    {
        // Signals can overlap (one is queued while the previous runs): handle them strictly one after another.
        lock (_signalLock)
        {
            OnBufferSignalCore();
        }
    }

    private void OnBufferSignalCore()
    {
        if (_disposed)
        {
            return;
        }

        // One boundary at a time: a handler may stop or seek (which drops the rest), and the head segment must always be
        // the track the handler was just told about.
        var advancedAny = false;
        bool ended;
        while (true)
        {
            TrackAdvancedEventArgs? advanced = null;
            lock (_sync)
            {
                lock (_decodeLock)
                {
                    var buffer = _buffer;
                    if (buffer is null)
                    {
                        return;
                    }

                    if (_processedCrossed < buffer.Crossed && _segments.Count > 1)
                    {
                        _processedCrossed++;
                        var old = _segments[0];
                        var next = _segments[1];
                        _segments.RemoveAt(0);
                        _retired.Add(old.Decoder); // closed by the decode thread
                        var played = old.Start + buffer.Format.SamplesToTime(next.StartIndex - old.StartIndex);
                        advanced = new TrackAdvancedEventArgs(next.Track, next.Token!, played, next.Skipped);
                    }

                    ended = buffer.EndReached;
                }
            }

            if (advanced is null)
            {
                break;
            }

            advancedAny = true;
            TrackAdvanced?.Invoke(this, advanced);
        }

        if (advancedAny)
        {
            RaisePosition();
        }

        _wake.Set(); // room in the buffer, maybe more to chain
        if (ended)
        {
            HandleEnded();
        }
    }

    private void HandleEnded()
    {
        lock (_sync)
        {
            if (_disposed || _state != PlayerState.Playing || Volatile.Read(ref _buffer) is not { EndReached: true })
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

    /// <summary>
    /// The device went away mid-play (unplugged, invalidated); the output has closed itself. Report it and pause, so the
    /// position is kept and Play reopens the device (the same one if it is back, else the error says why).
    /// </summary>
    private void OnOutputFailed(object? sender, AudioOutputFailedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _output))
        {
            return;
        }

        Error?.Invoke(this, new PlayerErrorEventArgs(new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, e.Reason)));
        Pause();
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

    private static bool SameEntries(IReadOnlyList<NextTrack> a, IReadOnlyList<NextTrack> b)
        => a.Count == b.Count && StartsWith(a, b);

    private static bool StartsWith(IReadOnlyList<NextTrack> list, IReadOnlyList<NextTrack> prefix)
    {
        if (list.Count < prefix.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (!ReferenceEquals(list[i].Token, prefix[i].Token) || !string.Equals(list[i].Path, prefix[i].Path, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hops from the audio thread to the thread pool without allocating (one queued item at a time).</summary>
    private sealed class Notifier(Player owner) : IThreadPoolWorkItem
    {
        private int _queued;

        public void Signal()
        {
            if (Interlocked.CompareExchange(ref _queued, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        public void Execute()
        {
            Volatile.Write(ref _queued, 0);
            owner.OnBufferSignal();
        }
    }
}
