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
    private IReadOnlyList<TrackGain> _gains = [];
    private TrackSegment? _writing;
    private bool _writingDone;
    private bool _endQueued;
    private IReadOnlyList<NextTrack> _candidates = [];
    private int _cursor;
    private long _processedCrossed;
    private long _underrunsBefore;
    private float[] _scratch = [];
    private long _generation;

    // Device opens by Play (RechainAfterOpen): a chain step that started before one asked the output for a converter before
    // it was open (none yet, or an exclusive open that then fell back to shared mode), so a "no" it got is tried again.
    // Not _generation: bumping that would also throw away a plain read in flight, whose samples the decoder is past.
    private long _opens;
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

    /// <summary>
    /// The graphic equalizer every stream filters with (#141); null for none. Changes take effect on the playing stream
    /// without a gap (it fades to them); setting a different instance takes effect from the next stream.
    /// </summary>
    public Equalization.GraphicEqualizer? Equalizer { get; set; }

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

            StopPositionTimer();

            // Keep the device open when the new track needs it as it is (the common case: the same rate and channels);
            // otherwise release it, and Play opens it for the new format. This comes first: the output stops reading the
            // old track before the new one replaces it, so no stale audio of the old buffer (or its end markers) reaches
            // the device, or the player's notifications, after the switch.
            // Shared mode: a track of another rate or channel count is converted to what the device is open at, so the
            // switch keeps the device too (the OS mixer resamples everything anyway, so nothing is lost).
            decoder = ConvertForOutput(decoder);
            var first = new TrackSegment(decoder, null, TimeSpan.Zero, [], []);
            var buffer = NewBuffer(first);
            RetargetOrReleaseOutput(buffer);
            lock (_decodeLock)
            {
                ResetDecoding(first, buffer);
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
        OpenedOutput? openedNow = null;
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
                        Volatile.Write(ref _output, _outputs.Create(_options.Output));
                        _output.Failed += OnOutputFailed;
                        _output.Rerouted += OnOutputRerouted;
                    }

                    _output.Open(buffer);
                    _outputSource = buffer;
                    openedNow = _output.Opened;
                    if (AdoptStreamFormat(_output, buffer) is { } adopted)
                    {
                        buffer = adopted;
                        WaitForPrefill(buffer);
                    }

                    RechainAfterOpen(buffer);
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

        // Subscribers run with no lock of ours held: one that calls back into the player must not deadlock.
        if (openedNow is not null)
        {
            OutputOpened?.Invoke(this, new OutputOpenedEventArgs(openedNow));
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_state != PlayerState.Playing)
            {
                // Nothing plays, but a track switch may have left an exclusive device held for the play that usually
                // follows: a caller that loads without playing says so with this, and the device is handed back.
                _output?.Pause();
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

            // Paused holds no exclusive device, whatever the player was doing before (a switch can leave it held).
            _output?.Pause();
            StopPositionTimer();
            SeekCore(position < TimeSpan.Zero ? TimeSpan.Zero : position);
            _ended = false;
            SetState(PlayerState.Paused);
        }

        RaisePosition();
    }

    public void SetGains(float current, IReadOnlyList<TrackGain> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (_disposed)
        {
            return;
        }

        lock (_decodeLock)
        {
            _gains = next;
            // By identity, never by position: the track that was loaded has no token (it takes `current`), every track that
            // came in gaplessly is found by its token, so a call that races a boundary cannot give the new track the old one's gain.
            foreach (var segment in _segments)
            {
                segment.Gain = segment.Token is null ? current : GainOf(segment.Token);
            }
        }
    }

    /// <summary>The levelling gain the caller gave an entry (1 when it gave none). Caller holds _decodeLock.</summary>
    private float GainOf(object? token)
    {
        foreach (var entry in _gains)
        {
            if (ReferenceEquals(entry.Token, token))
            {
                return entry.Gain;
            }
        }

        return 1f;
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
            else if (_endQueued && next.Count > chain.Count && buffer.TryRemoveTrailingEnd())
            {
                // Entries to try after what is decoded ahead: the end comes off and the decode thread chains them (or
                // writes it again). With none (the order sent again once the last track is current), the end stays: taking
                // it off and writing it again would leave a moment in which a device reading at the end finds neither.
                _endQueued = false;
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
                _output.Rerouted -= OnOutputRerouted;
                _output.Dispose();
                Volatile.Write(ref _output, null);
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

    private PlaybackBuffer NewBuffer(TrackSegment first)
    {
        var format = first.Decoder.Format;
        var frames = Math.Max(1024L, (long)Math.Max(format.SampleRate, 1) * _options.EffectiveBufferMilliseconds / 1000);
        var channels = Math.Max(format.Channels, 1);
        return new PlaybackBuffer(format, (int)Math.Min(frames * channels, int.MaxValue / 2), first, () => Volume, _notifier.Signal, SpectrumTap, Equalizer);
    }

    /// <summary>Makes <paramref name="first"/> (read through <paramref name="buffer"/>) the only track. Caller holds _decodeLock.</summary>
    private void ResetDecoding(TrackSegment first, PlaybackBuffer buffer)
    {
        _generation++;
        _retired.AddRange(_segments.Select(x => x.Decoder));
        _segments.Clear();
        var channels = Math.Max(first.Decoder.Format.Channels, 1);
        var old = _buffer;
        _underrunsBefore += old?.Underruns ?? 0;
        Volatile.Write(ref _buffer, buffer);
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
        long generation, opens;
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
                opens = _opens;
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

            // Shared mode chains a track of another rate or channel count too, converted to the stream's format.
            var output = Volatile.Read(ref _output);
            Func<PcmFormat, IPcmConverter?>? convert = output is null ? null : from => output.CreateConverter(from, buffer.Format);
            var next = OpenNext(_decoders, buffer.Format, candidates, cursor, scratch.AsMemory(0, want), convert);
            lock (_decodeLock)
            {
                // The next tracks may have been sent again meanwhile. On a track change the caller sends them relative
                // to the new track, so the one opened here is often still next: kept, not opened a second time.
                var unchanged = ReferenceEquals(candidates, _candidates) && cursor == _cursor;
                if (generation != _generation || !(unchanged || (next is not null && HasAt(_candidates, _cursor, next.Segment.Entries))))
                {
                    next?.Segment.Decoder.Dispose();
                    return Step.Progress;
                }

                if (next is null && opens != _opens)
                {
                    // Play opened the device while this step was opening the next track, so the output's answer about a
                    // converter may be from before (no output, or exclusive not yet fallen back to shared): try again rather
                    // than end the stream (RechainAfterOpen covers an end written before the open).
                    return Step.Progress;
                }

                if (next is not null)
                {
                    next.Segment.Gain = GainOf(next.Segment.Token); // before the output can reach the boundary
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
                _cursor += next.Segment.Entries.Count; // the entry opened and the ones skipped before it
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

    /// <summary>The track opened, and how many samples of its first block are in the scratch buffer. The entries it
    /// covers (those skipped before it, then it) are <c>Segment.Entries</c>: the cursor moves past that many.</summary>
    private sealed record Opened(TrackSegment Segment, int Read);

    /// <summary>
    /// Opens the first playable entry from <paramref name="cursor"/> on and decodes its first block into the scratch buffer.
    /// Null when nothing can follow gaplessly: no more entries, a different format, an empty or failing track (the caller
    /// ends the stream there and the normal path takes over, reporting errors and reopening the device as needed).
    /// </summary>
    private static Opened? OpenNext(IAudioDecoderFactory decoders, PcmFormat format, IReadOnlyList<NextTrack> candidates, int cursor, Memory<float> scratch, Func<PcmFormat, IPcmConverter?>? convert)
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
                // Same rate, channels and bit depth: the open device can play it as is. Another rate or channel count
                // goes on through a converter when the output gives one (shared mode). Anything else needs a reopen.
                if (decoder.Format != format && convert is not null)
                {
                    decoder = Converted(decoder, format, convert);
                }

                read = decoder.Format == format || decoder is ConvertingDecoder ? decoder.Read(scratch.Span) : 0;
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
            return new Opened(segment, read - (read % Math.Max(format.Channels, 1)));
        }

        return null;
    }

    /// <summary>
    /// <paramref name="decoder"/> read through a converter to <paramref name="format"/> when it has another rate or
    /// channel count and <paramref name="convert"/> gives one; else <paramref name="decoder"/> itself. A converter that
    /// cannot be made leaves the decoder as is (the caller then treats it as another format).
    /// </summary>
    private static IAudioDecoder Converted(IAudioDecoder decoder, PcmFormat format, Func<PcmFormat, IPcmConverter?> convert)
    {
        var from = decoder.Format;
        if (from.SampleRate == format.SampleRate && from.Channels == format.Channels)
        {
            return decoder;
        }

        IPcmConverter? converter;
        try
        {
            converter = convert(from);
        }
        catch (Exception)
        {
            converter = null;
        }

        if (converter is null)
        {
            return decoder;
        }

        try
        {
            return new ConvertingDecoder(decoder, converter);
        }
        catch (Exception)
        {
            converter.Dispose();
            return decoder;
        }
    }

    /// <summary>
    /// For a track switch: when the output is open (shared mode) at another rate or channel count than
    /// <paramref name="decoder"/>, the decoder converted to it (the stream's rate: the OS mixer's), so <see cref="RetargetOrReleaseOutput"/> keeps the device.
    /// Caller holds _sync.
    /// </summary>
    private IAudioDecoder ConvertForOutput(IAudioDecoder decoder)
    {
        if (_output is not { IsOpen: true } output || _outputSource is not { } current)
        {
            return decoder;
        }

        // Shared mode: the rate the device stream reads at (the OS mixer's), so the track is converted once, here.
        var target = output.StreamFormat is { } stream ? new PcmFormat(stream.SampleRate, stream.Channels, current.Format.SourceBitsPerSample) : current.Format;
        return Converted(decoder, target, from => output.CreateConverter(from, target));
    }

    // ------------------------------------------------------------------------------------------------ output side

    /// <summary>
    /// Shared mode, right after an open and before Start: the device reads at the OS mixer's rate, which is only known
    /// now. When the loaded track is at another rate the output would convert it on its side, and every track appended
    /// gaplessly to the buffer would be converted twice (to the buffer's rate, then to the mixer's). Instead the track is
    /// converted here, once, from where it is: a new buffer at the mixer's format replaces the old one (nothing of it was
    /// played) and the output is moved to it. The next tracks queued are kept and chained into it. Null when nothing
    /// needs to change or it cannot be done (a stream that cannot seek, no converter, an output that refuses): the output
    /// then converts, as before. Caller holds _sync; the output is open and not started.
    /// </summary>
    private PlaybackBuffer? AdoptStreamFormat(IAudioOutput output, PlaybackBuffer buffer)
    {
        if (output.StreamFormat is not { } stream
            || (stream.SampleRate == buffer.Format.SampleRate && stream.Channels == buffer.Format.Channels))
        {
            return null;
        }

        PlaybackBuffer? adopted;
        Exception? failed = null; // raised once no lock of the decoder's is held
        lock (_ioLock) // no decoding until it is done: the new decoder reads through the same file decoder
        {
            ConvertingDecoder converted;
            TrackSegment head;
            TimeSpan position;
            TimeSpan decodedTo;
            lock (_decodeLock)
            {
                if (!ReferenceEquals(_buffer, buffer) || _segments.Count == 0 || buffer.Crossed != 0)
                {
                    return null;
                }

                head = _segments[0];
                if (head.Decoder is ConvertingDecoder || !head.Decoder.CanSeek)
                {
                    return null; // already converted (to a mixer since changed), or cannot go back to what is heard
                }

                position = buffer.Position;
                decodedTo = head.Decoder.Position;
                var target = new PcmFormat(stream.SampleRate, stream.Channels, head.Decoder.Format.SourceBitsPerSample);
                if (Converted(head.Decoder, target, from => output.CreateConverter(from, target)) is not ConvertingDecoder c)
                {
                    return null;
                }

                converted = c;
            }

            var segment = new TrackSegment(converted, head.Token, position, head.Entries, head.Skipped) { Gain = head.Gain };
            adopted = NewBuffer(segment);
            bool moved;
            try
            {
                moved = output.Retarget(adopted); // reads nothing before Start
            }
            catch (Exception ex)
            {
                failed = ex;
                moved = false;
            }

            if (moved)
            {
                try
                {
                    converted.Seek(position); // back from what was decoded ahead to what is heard next
                }
                catch (Exception ex)
                {
                    // A damaged file: the output goes back to the buffer it had, to convert it as before, and the decoder
                    // to where that buffer left it, so the track plays as it would have. Reported only if that fails too.
                    moved = !MoveBack(output, buffer, head.Decoder, decodedTo, ex, out failed);
                }
            }

            if (!moved)
            {
                _ = converted.Detach(); // the head keeps its decoder; the output converts, as before
                adopted = null;
            }
            else
            {
                _outputSource = adopted;
                segment.Start = converted.Position;
                lock (_decodeLock)
                {
                    var candidates = _candidates;
                    _segments.RemoveAt(0); // its decoder lives on inside the new one: not retired
                    ResetDecoding(segment, adopted);
                    _candidates = candidates; // chained again, now at the mixer's rate
                }
            }
        }

        if (failed is not null)
        {
            Error?.Invoke(this, new PlayerErrorEventArgs(failed));
        }

        if (adopted is not null)
        {
            _wake.Set();
        }

        _wake.Set();
        return adopted;
    }

    /// <summary>
    /// After the converted track's decoder failed to seek with <paramref name="seekError"/>: the output back to
    /// <paramref name="buffer"/> and <paramref name="decoder"/> back to <paramref name="decodedTo"/>, where that buffer's
    /// decoding stopped. False (and the errors in <paramref name="failed"/>) when the output stays on the converted buffer;
    /// true, with <paramref name="failed"/> set only when the decoder could not go back. Caller holds _sync and _ioLock.
    /// </summary>
    private static bool MoveBack(IAudioOutput output, PlaybackBuffer buffer, IAudioDecoder decoder, TimeSpan decodedTo, Exception seekError, out Exception? failed)
    {
        failed = null;
        try
        {
            if (!output.Retarget(buffer))
            {
                failed = seekError; // plays on converted, from where the decoder is
                return false;
            }
        }
        catch (Exception ex)
        {
            failed = new AggregateException(seekError, ex);
            return false;
        }

        try
        {
            decoder.Seek(decodedTo);
        }
        catch (Exception ex)
        {
            failed = new AggregateException(seekError, ex); // plays on from wherever the decoder is
        }

        return true;
    }

    /// <summary>
    /// Before the first open there is no output to convert with, so a decode thread that reached the end of a short first
    /// track already may have ended the stream before a next track of another rate or channel count (which shared mode
    /// converts). Now that the device is open, and before it starts reading, that end comes off and the next tracks are
    /// tried again (an output that converts nothing ends the stream there again). A chain step still opening one sees the
    /// open through <c>_opens</c> and tries again too. Caller holds _sync.
    /// </summary>
    private void RechainAfterOpen(PlaybackBuffer buffer)
    {
        lock (_decodeLock)
        {
            _opens++; // a chain step in flight retries what the output refused before this open
            if (!_options.Gapless || !ReferenceEquals(_buffer, buffer) || !_endQueued || _cursor >= _candidates.Count
                || !buffer.TryRemoveTrailingEnd())
            {
                return;
            }

            _endQueued = false;
            _generation++;
        }

        _wake.Set();
    }

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
        if (_disposed || !ReferenceEquals(sender, Volatile.Read(ref _output)))
        {
            return;
        }

        Error?.Invoke(this, new PlayerErrorEventArgs(new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, e.Reason)));
        Pause();
    }

    /// <summary>
    /// The output moved to another device by itself (a new system default, or the default after the chosen device went
    /// away) and plays on from the same position: report where it is now, and say so when it is not what was asked.
    /// </summary>
    private void OnOutputRerouted(object? sender, AudioOutputReroutedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, Volatile.Read(ref _output)))
        {
            return;
        }

        OutputOpened?.Invoke(this, new OutputOpenedEventArgs(e.Opened));
        if (e.Message is { } message)
        {
            Error?.Invoke(this, new PlayerErrorEventArgs(new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, message)));
        }
    }

    /// <summary>
    /// A track change: moves the open output to <paramref name="buffer"/> without closing the device, which is what makes
    /// a switch instant (a new device stream can take far longer than opening and decoding the file). Falls back to
    /// releasing it, so <see cref="Play"/> reopens, when there is no open output, the device would need another
    /// configuration, or the output refuses. Caller holds _sync.
    /// </summary>
    private void RetargetOrReleaseOutput(PlaybackBuffer buffer)
    {
        if (_output is { IsOpen: true } output)
        {
            try
            {
                if (output.Retarget(buffer))
                {
                    _outputSource = buffer;
                    return;
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, new PlayerErrorEventArgs(ex));
            }
        }

        ReleaseOutput();
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

    /// <summary><paramref name="list"/> has <paramref name="entries"/>, in order, from <paramref name="at"/> on.</summary>
    private static bool HasAt(IReadOnlyList<NextTrack> list, int at, IReadOnlyList<NextTrack> entries)
        => at >= 0 && at <= list.Count && StartsWith(list.Skip(at).ToList(), entries);

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
