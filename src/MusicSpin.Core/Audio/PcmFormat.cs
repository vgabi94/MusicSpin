namespace MusicSpin.Core.Audio;

/// <summary>
/// Format of the PCM stream flowing from a decoder to an output: interleaved 32-bit float samples
/// at the source's native sample rate and channel count (no resampling, no remixing).
/// </summary>
/// <param name="SampleRate">Frames per second, e.g. 44100, 96000, 192000.</param>
/// <param name="Channels">Interleaved channel count.</param>
/// <param name="SourceBitsPerSample">
/// Bit depth of the original source (16, 24, 32, 1 for DSD...). Outputs use it to pick a device sample
/// format that preserves the original samples exactly (24-bit integers are exact in float32).
/// </param>
public readonly record struct PcmFormat(int SampleRate, int Channels, int SourceBitsPerSample = 32)
{
    /// <summary>Number of interleaved samples per second (SampleRate * Channels).</summary>
    public int SamplesPerSecond => SampleRate * Channels;

    public TimeSpan SamplesToTime(long interleavedSamples)
        => Channels <= 0 || SampleRate <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)interleavedSamples / SamplesPerSecond);

    public long TimeToSamples(TimeSpan time)
        => (long)Math.Round(time.TotalSeconds * SampleRate) * Channels;
}
