using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Equalization;

/// <summary>
/// Runs a <see cref="GraphicEqualizer"/> over one playback stream's interleaved samples, on the audio thread: no
/// allocation after construction, no locks, no waiting.
/// <list type="bullet">
/// <item>Off or flat: the samples are not touched (bit-exact), so bit-perfect playback stays bit-perfect.</item>
/// <item>Filters in 64-bit float with their own state per channel; the state runs on across gapless boundaries.</item>
/// <item>Designed for the stream's rate and redesigned when the format it is given changes.</item>
/// <item>A change (a band, a preset, on/off) crossfades from the old filters to the new over <see cref="FadeMilliseconds"/>,
/// both running meanwhile, so it never clicks; a change during a fade follows when that fade ends.</item>
/// <item>A sample that comes out not finite (a corrupt input) is replaced by silence and the filters are reset.</item>
/// </list>
/// </summary>
internal sealed class EqualizerProcessor
{
    public const int FadeMilliseconds = 20;

    private readonly GraphicEqualizer _equalizer;
    private Chain _active;
    private Chain _next;
    private EqualizerSettings? _seen;
    private PcmFormat _format;
    private int _fadeFrames;
    private int _fadeStep = -1; // -1: not fading

    public EqualizerProcessor(GraphicEqualizer equalizer, PcmFormat format)
    {
        _equalizer = equalizer ?? throw new ArgumentNullException(nameof(equalizer));
        _active = new Chain(format.Channels);
        _next = new Chain(format.Channels);
        Reformat(format);
    }

    /// <summary>The settings the output is filtering with now (after any fade).</summary>
    internal EqualizerSettings? Applied => _fadeStep < 0 ? _seen : null;

    /// <summary>Filters <paramref name="samples"/> (interleaved, in <paramref name="format"/>) in place.</summary>
    public void Process(Span<float> samples, PcmFormat format)
    {
        if (format != _format)
        {
            Reformat(format);
        }

        var settings = _equalizer.Settings;
        if (!ReferenceEquals(settings, _seen) && _fadeStep < 0)
        {
            // Start fading to it (when it filters any differently from what plays).
            _seen = settings;
            _next.Design(settings, _format.SampleRate, _active);
            if (!_next.SameAs(_active))
            {
                _fadeStep = 0;
            }
        }

        if (_fadeStep < 0 && _active.Identity)
        {
            return; // bit-exact
        }

        var channels = Math.Max(1, _format.Channels);
        var broken = false;
        for (var i = 0; i + channels <= samples.Length; i += channels)
        {
            double mix = 1;
            if (_fadeStep >= 0)
            {
                mix = (_fadeStep + 1) / (double)_fadeFrames;
            }

            for (var c = 0; c < channels; c++)
            {
                double x = samples[i + c];
                var y = _active.Run(x, c);
                if (_fadeStep >= 0)
                {
                    y += (_next.Run(x, c) - y) * mix;
                }

                if (!double.IsFinite(y))
                {
                    y = 0;
                    broken = true;
                }

                samples[i + c] = (float)y;
            }

            if (_fadeStep >= 0 && ++_fadeStep >= _fadeFrames)
            {
                (_active, _next) = (_next, _active);
                _fadeStep = -1;
            }
        }

        if (broken)
        {
            _active.Reset();
            _next.Reset();
        }
        else
        {
            _active.Flush();
            if (_fadeStep >= 0)
            {
                _next.Flush();
            }
        }
    }

    private void Reformat(PcmFormat format)
    {
        // A new format (a new stream's, or a test's): design for its rate straight away, no fade from another rate's filters.
        _format = format;
        _fadeFrames = Math.Max(1, (int)((long)Math.Max(format.SampleRate, 1) * FadeMilliseconds / 1000));
        _active = new Chain(format.Channels);
        _next = new Chain(format.Channels);
        _seen = _equalizer.Settings;
        _active.Design(_seen, format.SampleRate, null);
        _fadeStep = -1;
    }

    /// <summary>One set of filters with its per-channel state (transposed direct form II: two state values per filter and channel).</summary>
    private sealed class Chain(int channels)
    {
        private readonly Biquad[] _filters = new Biquad[EqualizerSettings.BandCount];
        private readonly double[] _state = new double[EqualizerSettings.BandCount * Math.Max(1, channels) * 2];
        private readonly int _channels = Math.Max(1, channels);
        private int _count;
        private double _gain = 1;

        /// <summary>Nothing to do: no filters and unity gain.</summary>
        public bool Identity => _count == 0 && _gain == 1;

        public void Design(EqualizerSettings settings, int sampleRate, Chain? from)
        {
            if (!settings.IsActive)
            {
                _count = 0;
                _gain = 1;
            }
            else
            {
                _count = EqualizerDesign.Design(settings, sampleRate, _filters, out _gain, out _);
            }

            // Start from the playing filters' state (the same signal has been through them), else from rest.
            if (from is not null && from._count == _count)
            {
                Array.Copy(from._state, _state, _state.Length);
            }
            else
            {
                Array.Clear(_state);
            }
        }

        public bool SameAs(Chain other)
        {
            if (_count != other._count || _gain != other._gain)
            {
                return false;
            }

            for (var i = 0; i < _count; i++)
            {
                if (_filters[i] != other._filters[i])
                {
                    return false;
                }
            }

            return true;
        }

        public double Run(double x, int channel)
        {
            var y = x * _gain;
            var s = channel * EqualizerSettings.BandCount * 2;
            for (var f = 0; f < _count; f++, s += 2)
            {
                ref readonly var q = ref _filters[f];
                var input = y;
                y = (q.B0 * input) + _state[s];
                _state[s] = (q.B1 * input) - (q.A1 * y) + _state[s + 1];
                _state[s + 1] = (q.B2 * input) - (q.A2 * y);
            }

            return y;
        }

        public void Reset() => Array.Clear(_state);

        /// <summary>Zeroes state that has decayed to nothing, so silence never runs on in slow subnormal arithmetic.</summary>
        public void Flush()
        {
            for (var i = 0; i < _state.Length; i++)
            {
                if (Math.Abs(_state[i]) < 1e-30)
                {
                    _state[i] = 0;
                }
            }
        }
    }
}
