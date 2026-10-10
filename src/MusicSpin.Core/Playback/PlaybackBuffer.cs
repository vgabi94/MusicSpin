using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Playback;

/// <summary>One track's run of samples in a <see cref="PlaybackBuffer"/> (the first track, or one appended gaplessly).</summary>
internal sealed class TrackSegment(IAudioDecoder decoder, object? token, TimeSpan start, IReadOnlyList<NextTrack> entries,
    IReadOnlyList<SkippedNextTrack> skipped)
{
    public IAudioDecoder Decoder { get; } = decoder;

    public TrackInfo Track => Decoder.Track;

    /// <summary>The caller's handle for the entry (a queue slot); null for a plain <see cref="Player.Load"/>.</summary>
    public object? Token { get; } = token;

    /// <summary>Track time of the segment's first sample (the seek target, or zero).</summary>
    public TimeSpan Start { get; set; } = start;

    /// <summary>Ring index (monotonic sample counter) of the segment's first sample. Written by the consumer when it crosses into it.</summary>
    public long StartIndex { get; set; }

    /// <summary>Next-track entries this segment used up: the skipped ones and its own (empty for the first segment).</summary>
    public IReadOnlyList<NextTrack> Entries { get; } = entries;

    public IReadOnlyList<SkippedNextTrack> Skipped { get; } = skipped;
}

internal enum MarkerKind
{
    /// <summary>The next sample belongs to <see cref="Marker.Segment"/>: the output plays straight on (gapless).</summary>
    Boundary,

    /// <summary>Nothing follows: the stream ends here.</summary>
    End,
}

internal sealed class Marker(MarkerKind kind, long position, TrackSegment? segment)
{
    public MarkerKind Kind { get; } = kind;

    public long Position { get; } = position;

    public TrackSegment? Segment { get; } = segment;
}

/// <summary>
/// Single-producer / single-consumer ring of interleaved float samples between the decode thread and the audio callback,
/// with in-band markers for track boundaries and the end of the stream.
/// <para>
/// The consumer (<see cref="Read"/>, on the device's audio thread) only copies: no allocation, no blocking, no logging.
/// Sample indices are monotonic 64-bit counters, so "position" is just <c>read - segment.StartIndex</c>. Structural changes
/// (flush on seek, cutting off pre-decoded tracks) happen on other threads while holding a tiny guard the consumer only
/// ever <i>tries</i> to take: if it is busy, that one callback plays silence instead of waiting.
/// </para>
/// </summary>
internal sealed class PlaybackBuffer : IPcmSource
{
    public const int MaxMarkers = 64;

    private readonly float[] _ring;
    private readonly Marker?[] _markers = new Marker?[MaxMarkers];
    private readonly Func<float> _volume;
    private readonly Action _signal;

    private long _write;      // producer
    private long _read;       // consumer (or a guarded structural change)
    private int _markerHead;  // guarded
    private int _markerCount; // guarded
    private int _guard;
    private TrackSegment _current;
    private int _endReached;
    private long _underruns;
    private long _crossed;

    public PlaybackBuffer(PcmFormat format, int capacitySamples, TrackSegment first, Func<float> volume, Action signal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySamples, format.Channels);
        Format = format;
        _ring = new float[capacitySamples - (capacitySamples % format.Channels)];
        _current = first;
        _volume = volume;
        _signal = signal;
    }

    public PcmFormat Format { get; }

    public int Capacity => _ring.Length;

    /// <summary>The segment whose samples the output is playing now.</summary>
    public TrackSegment Current => Volatile.Read(ref _current);

    /// <summary>Track time of the next sample the output will play.</summary>
    public TimeSpan Position
    {
        get
        {
            var segment = Current;
            var played = Math.Max(0, Volatile.Read(ref _read) - segment.StartIndex);
            return segment.Start + Format.SamplesToTime(played);
        }
    }

    /// <summary>Samples decoded but not yet played.</summary>
    public long Buffered => Volatile.Read(ref _write) - Volatile.Read(ref _read);

    public int Free => _ring.Length - (int)Buffered;

    /// <summary>Callbacks that found no decoded audio and played silence (the decoder fell behind).</summary>
    public long Underruns => Volatile.Read(ref _underruns);

    /// <summary>Track boundaries the output has played through.</summary>
    public long Crossed => Volatile.Read(ref _crossed);

    /// <summary>The output played the last sample before the end marker.</summary>
    public bool EndReached => Volatile.Read(ref _endReached) != 0;

    // ------------------------------------------------------------------------------------------------ consumer

    /// <summary>Audio callback. Returns <paramref name="buffer"/>.Length (silence-padded on underrun), or fewer then 0 at the end.</summary>
    public int Read(Span<float> buffer)
    {
        if (!TryEnterAsConsumer())
        {
            // The holder was preempted mid-change (its critical sections are a few array writes): rather than wait on
            // it, play one block of silence and count it.
            buffer.Clear();
            Volatile.Write(ref _underruns, _underruns + 1);
            return buffer.Length;
        }

        try
        {
            if (_endReached != 0)
            {
                return 0;
            }

            var done = 0;
            while (done < buffer.Length)
            {
                var limit = Volatile.Read(ref _write) - _read;
                if (_markerCount > 0)
                {
                    var marker = _markers[_markerHead]!;
                    if (marker.Position <= _read)
                    {
                        _markers[_markerHead] = null;
                        _markerHead = (_markerHead + 1) % MaxMarkers;
                        _markerCount--;
                        if (marker.Kind == MarkerKind.End)
                        {
                            Volatile.Write(ref _endReached, 1);
                            _signal();
                            return done;
                        }

                        marker.Segment!.StartIndex = _read;
                        Volatile.Write(ref _current, marker.Segment);
                        Volatile.Write(ref _crossed, _crossed + 1);
                        _signal();
                        continue;
                    }

                    limit = Math.Min(limit, marker.Position - _read);
                }

                if (limit <= 0)
                {
                    buffer[done..].Clear();
                    Volatile.Write(ref _underruns, _underruns + 1);
                    return buffer.Length;
                }

                var count = (int)Math.Min(limit, buffer.Length - done);
                var at = (int)(_read % _ring.Length);
                var first = Math.Min(count, _ring.Length - at);
                var target = buffer.Slice(done, count);
                _ring.AsSpan(at, first).CopyTo(target);
                _ring.AsSpan(0, count - first).CopyTo(target[first..]);
                var gain = _volume();
                if (gain < 1f)
                {
                    for (var i = 0; i < target.Length; i++)
                    {
                        target[i] *= gain;
                    }
                }

                Volatile.Write(ref _read, _read + count);
                done += count;
            }

            return done;
        }
        finally
        {
            Volatile.Write(ref _guard, 0);
        }
    }

    private bool TryEnterAsConsumer()
    {
        // Bounded busy-wait (no syscalls, no blocking): structural changes hold the guard for well under a microsecond.
        for (var i = 0; i < 1000; i++)
        {
            if (Interlocked.CompareExchange(ref _guard, 1, 0) == 0)
            {
                return true;
            }

            Thread.SpinWait(8);
        }

        return false;
    }

    // ------------------------------------------------------------------------------------------------ producer

    /// <summary>Appends samples. The caller checked <see cref="Free"/>.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var at = (int)(_write % _ring.Length);
        var first = Math.Min(samples.Length, _ring.Length - at);
        samples[..first].CopyTo(_ring.AsSpan(at));
        samples[first..].CopyTo(_ring);
        Volatile.Write(ref _write, _write + samples.Length);
    }

    /// <summary>Markers the output has not reached yet (a hint: may be stale by the time it is used).</summary>
    public int PendingMarkers => Volatile.Read(ref _markerCount);

    /// <summary>Marks the current write position. False when too many markers are pending.</summary>
    public bool TryAddMarker(MarkerKind kind, TrackSegment? segment)
    {
        using var _ = Enter();
        if (_markerCount == MaxMarkers)
        {
            return false;
        }

        _markers[(_markerHead + _markerCount) % MaxMarkers] = new Marker(kind, _write, segment);
        _markerCount++;
        return true;
    }

    /// <summary>Drops everything not yet played; <paramref name="current"/> plays next from <paramref name="current"/>.Start.</summary>
    public void Flush(TrackSegment current)
    {
        using var _ = Enter();
        Volatile.Write(ref _read, _write);
        ClearMarkers();
        current.StartIndex = _read;
        Volatile.Write(ref _current, current);
        Volatile.Write(ref _endReached, 0);
    }

    /// <summary>
    /// Cuts the buffer at the first marker the output has not reached yet: everything after it (pre-decoded next tracks,
    /// the end) is dropped and writing continues from there. Returns how many boundaries were dropped.
    /// </summary>
    public int CutAtFirstMarker()
    {
        using var _ = Enter();
        if (_markerCount == 0)
        {
            return 0;
        }

        var cut = _markers[_markerHead]!.Position;
        var boundaries = 0;
        for (var i = 0; i < _markerCount; i++)
        {
            if (_markers[(_markerHead + i) % MaxMarkers]!.Kind == MarkerKind.Boundary)
            {
                boundaries++;
            }
        }

        ClearMarkers();
        Volatile.Write(ref _write, cut);
        return boundaries;
    }

    /// <summary>Removes the end marker if it is the last thing written and the output has not reached it.</summary>
    public bool TryRemoveTrailingEnd()
    {
        using var _ = Enter();
        if (_markerCount == 0)
        {
            return false;
        }

        var last = (_markerHead + _markerCount - 1) % MaxMarkers;
        if (_markers[last]!.Kind != MarkerKind.End || _markers[last]!.Position != _write)
        {
            return false;
        }

        _markers[last] = null;
        _markerCount--;
        return true;
    }

    /// <summary>
    /// Test/diagnostic helper (never used on the audio path): waits until <paramref name="samples"/> are decoded ahead, or
    /// the end of the stream is in the buffer, so a pull reads deterministic audio rather than underrun silence.
    /// </summary>
    internal bool WaitUntilReadable(int samples, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var spin = new SpinWait();
        while (Buffered < samples && !EndReached && !HasPendingEnd())
        {
            if (clock.Elapsed > timeout)
            {
                return false;
            }

            spin.SpinOnce();
        }

        return true;
    }

    private bool HasPendingEnd()
    {
        using var _ = Enter();
        for (var i = 0; i < _markerCount; i++)
        {
            if (_markers[(_markerHead + i) % MaxMarkers]!.Kind == MarkerKind.End)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearMarkers()
    {
        Array.Clear(_markers);
        _markerHead = 0;
        _markerCount = 0;
    }

    private GuardScope Enter()
    {
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref _guard, 1, 0) != 0)
        {
            spin.SpinOnce(sleep1Threshold: -1); // the consumer holds it only for one callback's copy
        }

        return new GuardScope(this);
    }

    private readonly struct GuardScope(PlaybackBuffer owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._guard, 0);
    }
}
