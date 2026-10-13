using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Playback;

/// <summary>
/// Spectrum levels for an analyzer display: 16 bands spaced logarithmically from 40 Hz to 16 kHz, each 0..1 on a dB scale
/// (0 = −60 dBFS or quieter, 1 = 0 dBFS). One Hann-windowed FFT of <see cref="FftSize"/> samples per update.
/// </summary>
public static class SpectrumAnalyzer
{
    public const int Bands = 16;
    public const double MinHz = 40;
    public const double MaxHz = 16_000;
    public const double FloorDb = -60;
    public const int FftSize = 2048;

    private static readonly double[] Window = Hann(FftSize);
    private static readonly double WindowSum = Window.Sum();

    /// <summary>The lower edge of each band, plus the upper edge of the last one (<see cref="Bands"/> + 1 values).</summary>
    public static IReadOnlyList<double> BandEdges { get; } = [.. Enumerable.Range(0, Bands + 1).Select(b => MinHz * Math.Pow(MaxHz / MinHz, (double)b / Bands))];

    /// <summary>
    /// Levels of the <see cref="FftSize"/> mono samples in <paramref name="samples"/> (the most recent last), played at
    /// <paramref name="sampleRate"/>. A full-scale sine gives about 1 in its band. Bands above the Nyquist frequency are 0.
    /// </summary>
    public static float[] Analyze(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length != FftSize)
        {
            throw new ArgumentException($"Expected {FftSize} samples, got {samples.Length}.", nameof(samples));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var re = new double[FftSize];
        var im = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            re[i] = samples[i] * Window[i];
        }

        Fft(re, im);
        var levels = new float[Bands];
        var binHz = (double)sampleRate / FftSize;
        var lastBin = (FftSize / 2) - 1;
        for (var b = 0; b < Bands; b++)
        {
            var (lo, hi) = (BandEdges[b], BandEdges[b + 1]);
            if (lo >= sampleRate / 2.0)
            {
                continue;
            }

            var first = Math.Max(1, (int)Math.Ceiling(lo / binHz));
            var last = Math.Min(lastBin, (int)Math.Ceiling(hi / binHz) - 1);
            if (first > last)
            {
                first = last = Math.Clamp((int)Math.Round(Math.Sqrt(lo * hi) / binHz), 1, lastBin); // narrower than a bin
            }

            var peak = 0.0;
            for (var k = first; k <= last; k++)
            {
                peak = Math.Max(peak, Math.Sqrt((re[k] * re[k]) + (im[k] * im[k])));
            }

            levels[b] = Level(peak * 2 / WindowSum);
        }

        return levels;
    }

    private static float Level(double amplitude)
        => amplitude <= 0 ? 0 : (float)Math.Clamp((20 * Math.Log10(amplitude) - FloorDb) / -FloorDb, 0, 1);

    private static double[] Hann(int size)
        => [.. Enumerable.Range(0, size).Select(i => 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / size)))];

    /// <summary>In-place iterative radix-2 FFT; the length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var (wRe, wIm) = (Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += len)
            {
                var (cRe, cIm) = (1.0, 0.0);
                for (var k = 0; k < len / 2; k++)
                {
                    var (a, b) = (start + k, start + k + (len / 2));
                    var tRe = (re[b] * cRe) - (im[b] * cIm);
                    var tIm = (re[b] * cIm) + (im[b] * cRe);
                    (re[b], im[b]) = (re[a] - tRe, im[a] - tIm);
                    (re[a], im[a]) = (re[a] + tRe, im[a] + tIm);
                    (cRe, cIm) = ((cRe * wRe) - (cIm * wIm), (cRe * wIm) + (cIm * wRe));
                }
            }
        }
    }
}

/// <summary>
/// Copies what the output plays into a small mono ring for <see cref="SpectrumMeter"/>: written on the audio thread
/// (before volume, so a muted player still shows the music), only while <see cref="Enabled"/>. Never changes the samples.
/// Rates above 48 kHz (hi-res PCM, converted DSD) are averaged down by a whole factor to 44.1-48 kHz, which keeps one FFT
/// fine-grained in the bass and the ring small.
/// </summary>
internal sealed class SpectrumTap
{
    private const int Capacity = 8192; // power of two, more than one FFT

    private readonly float[] _ring = new float[Capacity];
    private long _written;
    private volatile bool _enabled;
    private volatile int _sampleRate;
    private PcmFormat _format;
    private int _factor = 1;
    private float _sum;
    private int _summed;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>The rate of the samples in the ring (after averaging down); 0 before anything was written.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Audio thread: mixes <paramref name="interleaved"/> to mono and appends it.</summary>
    public void Write(ReadOnlySpan<float> interleaved, PcmFormat format)
    {
        if (format != _format)
        {
            _format = format;
            _factor = Math.Max(1, format.SampleRate / 44_100);
            (_sum, _summed) = (0, 0);
            _sampleRate = format.SampleRate / _factor;
            Volatile.Write(ref _written, 0);
        }

        var channels = Math.Max(1, format.Channels);
        var written = _written;
        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            var frame = 0f;
            for (var c = 0; c < channels; c++)
            {
                frame += interleaved[i + c];
            }

            _sum += frame / channels;
            if (++_summed == _factor)
            {
                _ring[written & (Capacity - 1)] = _sum / _factor;
                written++;
                (_sum, _summed) = (0, 0);
            }
        }

        Volatile.Write(ref _written, written);
    }

    /// <summary>Copies the most recent samples into <paramref name="target"/> (oldest first; zeros where there are none yet).</summary>
    public void CopyLatest(Span<float> target)
    {
        var written = Volatile.Read(ref _written);
        var count = (int)Math.Min(written, Math.Min(target.Length, Capacity));
        target.Clear();
        var offset = target.Length - count;
        for (var k = 0; k < count; k++)
        {
            target[offset + k] = _ring[(written - count + k) & (Capacity - 1)];
        }
    }
}

/// <summary>
/// Spectrum levels of what a <see cref="Player"/> plays, about 30 times a second, for the subscribers only: nothing is
/// copied or computed while nobody listens. While the player is paused or stopped, one update of zeros is sent (so the
/// bars fall) and then nothing until it plays again. Handlers run on a timer thread and must not block.
/// </summary>
public sealed class SpectrumMeter : IDisposable
{
    /// <summary>Time between updates (about 30 per second).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(33);

    private readonly Player _player;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<Action<float[]>> _handlers = [];
    private readonly float[] _samples = new float[SpectrumAnalyzer.FftSize];
    private ITimer? _timer;
    private int _ticking;
    private bool _zerosSent;
    private bool _disposed;

    public SpectrumMeter(Player player, TimeProvider? time = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True while someone is subscribed (the audio is being copied and analysed).</summary>
    public bool Active
    {
        get { lock (_gate) { return _handlers.Count > 0; } }
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with <see cref="SpectrumAnalyzer.Bands"/> levels per update until the returned handle
    /// is disposed. Every subscriber gets the same array: don't change it.
    /// </summary>
    public IDisposable Subscribe(Action<float[]> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _handlers.Add(handler);
            if (_handlers.Count == 1)
            {
                _player.SpectrumTap.Enabled = true;
                _zerosSent = false;
                _timer = _time.CreateTimer(static s => ((SpectrumMeter)s!).Tick(), this, Interval, Interval);
            }
        }

        return new Unsubscribe(this, handler);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handlers.Clear();
            Stop();
        }
    }

    private void Remove(Action<float[]> handler)
    {
        lock (_gate)
        {
            if (_handlers.Remove(handler) && _handlers.Count == 0)
            {
                Stop();
            }
        }
    }

    private void Stop()
    {
        _player.SpectrumTap.Enabled = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return; // the previous update is still running
        }

        try
        {
            Action<float[]>[] handlers;
            lock (_gate)
            {
                if (_handlers.Count == 0)
                {
                    return;
                }

                handlers = [.. _handlers];
            }

            var tap = _player.SpectrumTap;
            float[] levels;
            if (_player.State == PlayerState.Playing && tap.SampleRate > 0)
            {
                tap.CopyLatest(_samples);
                levels = SpectrumAnalyzer.Analyze(_samples, tap.SampleRate);
                _zerosSent = false;
            }
            else if (!_zerosSent)
            {
                levels = new float[SpectrumAnalyzer.Bands];
                _zerosSent = true;
            }
            else
            {
                return;
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(levels);
                }
                catch (Exception)
                {
                    // a broken subscriber must never break the others or playback
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private sealed class Unsubscribe(SpectrumMeter owner, Action<float[]> handler) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Remove(handler);
            }
        }
    }
}
