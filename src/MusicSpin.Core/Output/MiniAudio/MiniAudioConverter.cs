using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>miniaudio's data converter (linear resampler behind its steepest low-pass, channel mixing), float32 in and out.</summary>
internal sealed unsafe class MiniAudioConverter : IPcmConverter
{
    private nint _handle;

    /// <exception cref="MusicSpinException">AudioDeviceError when miniaudio refuses the formats.</exception>
    public MiniAudioConverter(PcmFormat from, PcmFormat to)
    {
        var result = MsAudioNative.ConverterInit(from.Channels, from.SampleRate, to.Channels, to.SampleRate, out _handle);
        if (result != 0 || _handle == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError,
                $"Cannot convert {from.SampleRate} Hz/{from.Channels} ch to {to.SampleRate} Hz/{to.Channels} ch: {MsAudioNative.Describe(result)}");
        }

        From = from;
        To = to;
    }

    public PcmFormat From { get; }

    public PcmFormat To { get; }

    public int Convert(ReadOnlySpan<float> input, Span<float> output, out int consumed)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        ulong inFrames = (ulong)(input.Length / From.Channels);
        ulong outFrames = (ulong)(output.Length / To.Channels);
        int result;
        fixed (float* i = input)
        fixed (float* o = output)
        {
            result = MsAudioNative.ConverterProcess(_handle, i, ref inFrames, o, ref outFrames);
        }

        if (result != 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"Sample-rate conversion failed: {MsAudioNative.Describe(result)}");
        }

        consumed = (int)inFrames * From.Channels;
        return (int)outFrames * To.Channels;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var result = MsAudioNative.ConverterReset(_handle); // on failure the old (unreset) converter is kept
        if (result != 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"Sample-rate converter reset failed: {MsAudioNative.Describe(result)}");
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = 0;
        if (handle != 0)
        {
            MsAudioNative.ConverterUninit(handle);
        }

        GC.SuppressFinalize(this);
    }

    ~MiniAudioConverter()
    {
        if (_handle != 0)
        {
            MsAudioNative.ConverterUninit(_handle);
        }
    }
}
