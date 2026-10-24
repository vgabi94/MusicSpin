using MusicSpin.Core.Audio;

namespace MusicSpin.Tests.Shared;

/// <summary>
/// A deterministic converter for integer rate ratios: downsampling keeps every k-th frame, upsampling repeats each frame
/// k times; output channel c takes input channel min(c, last). Keeps its phase across calls, like a real filter keeps
/// history, so a missed <see cref="Reset"/> shows. Counts what was asked of it.
/// </summary>
internal sealed class StepConverter : IPcmConverter
{
    private readonly int _down;
    private readonly int _up;
    private int _phase;

    public StepConverter(PcmFormat from, PcmFormat to)
    {
        From = from;
        To = to;
        if (from.SampleRate >= to.SampleRate && from.SampleRate % to.SampleRate == 0)
        {
            _down = from.SampleRate / to.SampleRate;
            _up = 1;
        }
        else if (to.SampleRate % from.SampleRate == 0)
        {
            _down = 1;
            _up = to.SampleRate / from.SampleRate;
        }
        else
        {
            throw new ArgumentException("integer ratios only");
        }
    }

    public PcmFormat From { get; }

    public PcmFormat To { get; }

    public int Resets { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>What a whole track converts to, from its first frame.</summary>
    public static float[] Expected(RampSpec spec, PcmFormat to)
    {
        var converter = new StepConverter(spec.Format, to);
        var input = spec.Pcm();
        var output = new float[(input.Length / spec.Channels * Math.Max(1, to.SampleRate / spec.SampleRate) + 1) * to.Channels];
        var written = converter.Convert(input, output, out _);
        return output[..written];
    }

    public int Convert(ReadOnlySpan<float> input, Span<float> output, out int consumed)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        int inCh = From.Channels, outCh = To.Channels;
        var inFrames = input.Length / inCh;
        var outFrames = output.Length / outCh;
        var i = 0;
        var o = 0;
        while (i < inFrames)
        {
            if (_down > 1)
            {
                if (_phase == 0)
                {
                    if (o >= outFrames)
                    {
                        break;
                    }

                    Copy(input, i, output, o++);
                }

                _phase = (_phase + 1) % _down;
                i++;
            }
            else
            {
                if (o >= outFrames)
                {
                    break;
                }

                Copy(input, i, output, o++);
                _phase++;
                if (_phase == _up)
                {
                    _phase = 0;
                    i++;
                }
            }
        }

        consumed = i * inCh;
        return o * outCh;

        void Copy(ReadOnlySpan<float> src, int frame, Span<float> dst, int at)
        {
            for (var c = 0; c < outCh; c++)
            {
                dst[(at * outCh) + c] = src[(frame * inCh) + Math.Min(c, inCh - 1)];
            }
        }
    }

    public void Reset()
    {
        _phase = 0;
        Resets++;
    }

    public void Dispose() => Disposed = true;
}
