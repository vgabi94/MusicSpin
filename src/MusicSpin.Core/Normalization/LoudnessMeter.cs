namespace MusicSpin.Core.Normalization;

/// <summary>The loudness of a track.</summary>
/// <param name="IntegratedLufs">EBU R128 / ITU-R BS.1770-4 integrated loudness in LUFS; null for silence (nothing above the -70 LUFS gate).</param>
/// <param name="TruePeak">Largest inter-sample (4x oversampled) peak of any channel, as a linear full-scale ratio (1.0 = 0 dBTP).</param>
/// <param name="DurationSeconds">How much audio was measured.</param>
public readonly record struct LoudnessResult(double? IntegratedLufs, double TruePeak, double DurationSeconds);

/// <summary>
/// Streaming EBU R128 / ITU-R BS.1770-4 meter: K-weighted, gated (-70 LUFS absolute, -10 LU relative) integrated loudness over
/// 400 ms blocks with 75% overlap, plus the true peak. Feed interleaved float samples with <see cref="Add"/>, then <see cref="Finish"/>.
/// Not thread-safe; allocation-free once running (it keeps one double per 100 ms of audio).
/// </summary>
public sealed class LoudnessMeter
{
    private const double BlockSeconds = 0.4;
    private const double StepSeconds = 0.1;
    private const double AbsoluteGateLufs = -70;
    private const double RelativeGateLu = -10;
    private const double Offset = -0.691;

    private readonly int _channels;
    private readonly double[] _weights;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly TruePeakFilter _peak;
    private readonly int _subBlockFrames;
    private readonly double[] _recent = new double[4];
    private readonly List<double> _blocks = [];
    private readonly int _sampleRate;
    private double _subSum;
    private int _subFrames;
    private int _recentCount;
    private int _recentNext;
    private long _frames;
    private double _allSum;
    private bool _finished;

    public LoudnessMeter(int sampleRate, int channels)
    {
        if (sampleRate < 8000 || sampleRate > 768_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be 8000..768000 Hz.");
        }

        if (channels is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1..32.");
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _weights = ChannelWeights(channels);
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];
        var (shelf, highPass) = KWeighting(sampleRate);
        for (var c = 0; c < channels; c++)
        {
            _shelf[c] = shelf;
            _highPass[c] = highPass;
        }

        _peak = new TruePeakFilter(sampleRate, channels);
        _subBlockFrames = Math.Max(1, (int)Math.Round(sampleRate * StepSeconds));
    }

    /// <summary>BS.1770 channel weights for the usual layouts: surround channels +1.5 dB, LFE left out, the rest 1.</summary>
    internal static double[] ChannelWeights(int channels)
    {
        var w = new double[channels];
        Array.Fill(w, 1.0);
        const double Surround = 1.41253754462275; // +1.5 dB
        switch (channels)
        {
            case 4: // quad: L R Ls Rs
                w[2] = w[3] = Surround;
                break;
            case 5: // 5.0: L R C Ls Rs
                w[3] = w[4] = Surround;
                break;
            case 6: // 5.1: L R C LFE Ls Rs
                w[3] = 0;
                w[4] = w[5] = Surround;
                break;
            case 7: // 6.1: L R C LFE Cs Ls Rs
                w[3] = 0;
                w[4] = w[5] = w[6] = Surround;
                break;
            case 8: // 7.1: L R C LFE Lb Rb Ls Rs
                w[3] = 0;
                w[4] = w[5] = w[6] = w[7] = Surround;
                break;
        }

        return w;
    }

    /// <summary>The two K-weighting biquads (pre-filter high shelf, RLB high-pass) for a sample rate.</summary>
    internal static (Biquad Shelf, Biquad HighPass) KWeighting(int sampleRate)
    {
        // Analogue prototypes of BS.1770 mapped with the bilinear transform (the same derivation libebur128 uses),
        // so any sample rate gets the right filter, not just 48 kHz.
        const double F0 = 1681.974450955533;
        const double G = 3.999843853973347;
        const double Q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * F0 / sampleRate);
        var vh = Math.Pow(10.0, G / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1.0 + k / Q + k * k;
        var shelf = new Biquad(
            (vh + vb * k / Q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / Q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / Q + k * k) / a0);

        const double F1 = 38.13547087602444;
        const double Q1 = 0.5003270373238773;
        var k1 = Math.Tan(Math.PI * F1 / sampleRate);
        var d = 1.0 + k1 / Q1 + k1 * k1;
        var highPass = new Biquad(1.0, -2.0, 1.0, 2.0 * (k1 * k1 - 1.0) / d, (1.0 - k1 / Q1 + k1 * k1) / d);
        return (shelf, highPass);
    }

    /// <summary>Measures interleaved samples (a whole number of frames).</summary>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var frames = interleaved.Length / _channels;
        for (var f = 0; f < frames; f++)
        {
            var sum = 0.0;
            for (var c = 0; c < _channels; c++)
            {
                var x = interleaved[f * _channels + c];
                if (!float.IsFinite(x))
                {
                    x = 0; // a broken decoder's NaN must not poison the whole measurement
                }

                _peak.Push(c, x);
                if (_weights[c] == 0)
                {
                    continue;
                }

                var y = _highPass[c].Process(_shelf[c].Process(x));
                sum += _weights[c] * y * y;
            }

            _subSum += sum;
            _allSum += sum;
            _frames++;
            if (++_subFrames == _subBlockFrames)
            {
                EndSubBlock();
            }
        }
    }

    private void EndSubBlock()
    {
        _recent[_recentNext] = _subSum / _subFrames;
        _recentNext = (_recentNext + 1) % _recent.Length;
        _recentCount = Math.Min(_recentCount + 1, _recent.Length);
        _subSum = 0;
        _subFrames = 0;
        if (_recentCount == _recent.Length)
        {
            _blocks.Add((_recent[0] + _recent[1] + _recent[2] + _recent[3]) / _recent.Length);
        }
    }

    /// <summary>The loudness and true peak of everything added. The meter cannot be used afterwards.</summary>
    public LoudnessResult Finish()
    {
        _finished = true;
        var duration = (double)_frames / _sampleRate;
        var peak = _peak.Peak;
        if (_blocks.Count == 0)
        {
            // Shorter than one 400 ms block: gate the whole clip as a single block.
            if (_frames == 0)
            {
                return new LoudnessResult(null, peak, 0);
            }

            var power = _allSum / _frames;
            var lufs = ToLufs(power);
            return new LoudnessResult(lufs > AbsoluteGateLufs ? lufs : null, peak, duration);
        }

        double absSum = 0;
        var absCount = 0;
        foreach (var p in _blocks)
        {
            if (ToLufs(p) > AbsoluteGateLufs)
            {
                absSum += p;
                absCount++;
            }
        }

        if (absCount == 0)
        {
            return new LoudnessResult(null, peak, duration);
        }

        var relative = ToLufs(absSum / absCount) + RelativeGateLu;
        double sum = 0;
        var count = 0;
        foreach (var p in _blocks)
        {
            var l = ToLufs(p);
            if (l > AbsoluteGateLufs && l > relative)
            {
                sum += p;
                count++;
            }
        }

        return new LoudnessResult(count == 0 ? null : ToLufs(sum / count), peak, duration);
    }

    private static double ToLufs(double meanSquare) => meanSquare <= 0 ? double.NegativeInfinity : Offset + 10.0 * Math.Log10(meanSquare);

    internal struct Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1;
        private double _z2;

        public double Process(double x)
        {
            // Transposed direct form II.
            var y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }

    /// <summary>
    /// True-peak detector: ITU-R BS.1770 Annex 2 — oversample 4x (2x from 96 kHz, none from 192 kHz) with a windowed-sinc
    /// polyphase FIR and keep the largest absolute value.
    /// </summary>
    private sealed class TruePeakFilter
    {
        private const int TapsPerPhase = 12;
        private readonly int _factor;
        private readonly double[][] _phases;
        private readonly float[][] _history;
        private readonly int[] _pos;
        private float _peak;

        public TruePeakFilter(int sampleRate, int channels)
        {
            _factor = sampleRate < 96_000 ? 4 : sampleRate < 192_000 ? 2 : 1;
            _phases = Coefficients(_factor);
            _history = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                _history[c] = new float[TapsPerPhase * 2];
            }

            _pos = new int[channels];
        }

        public double Peak => _peak;

        public void Push(int channel, float x)
        {
            var abs = Math.Abs(x);
            if (abs > _peak)
            {
                _peak = abs;
            }

            if (_factor == 1)
            {
                return;
            }

            var h = _history[channel];
            var pos = _pos[channel];
            h[pos] = x;
            h[pos + TapsPerPhase] = x;
            pos = (pos + 1) % TapsPerPhase;
            _pos[channel] = pos;
            // h[pos .. pos+T-1] runs oldest to newest.
            for (var p = 0; p < _factor; p++) // the input sample itself was counted above
            {
                var coef = _phases[p];
                double acc = 0;
                for (var k = 0; k < TapsPerPhase; k++)
                {
                    acc += coef[k] * h[pos + TapsPerPhase - 1 - k];
                }

                var a = (float)Math.Abs(acc);
                if (a > _peak)
                {
                    _peak = a;
                }
            }
        }

        private static double[][] Coefficients(int factor)
        {
            var phases = new double[factor][];
            for (var p = 0; p < factor; p++)
            {
                phases[p] = new double[TapsPerPhase];
            }

            if (factor == 1)
            {
                return phases;
            }

            var n = factor * TapsPerPhase;
            var h = new double[n];
            const double Beta = 9.0;
            var centre = (n - 1) / 2.0;
            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var t = (i - centre) / factor;
                var sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                var r = (i - centre) / centre; // -1..1 across the window
                var window = BesselI0(Beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / BesselI0(Beta);
                h[i] = sinc * window;
                sum += h[i];
            }

            var scale = factor / sum; // unity gain per phase
            for (var i = 0; i < n; i++)
            {
                phases[i % factor][i / factor] = h[i] * scale;
            }

            return phases;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1;
            for (var k = 1; k < 60; k++)
            {
                term *= x / (2 * k) * (x / (2 * k));
                sum += term;
                if (term < sum * 1e-16)
                {
                    break;
                }
            }

            return sum;
        }
    }
}
