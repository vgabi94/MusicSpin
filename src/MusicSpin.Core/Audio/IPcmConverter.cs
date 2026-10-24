namespace MusicSpin.Core.Audio;

/// <summary>
/// Converts interleaved float PCM from one sample rate and channel count to another, keeping filter state between
/// calls. Not thread-safe: one reader at a time.
/// </summary>
public interface IPcmConverter : IDisposable
{
    /// <summary>The input rate and channels.</summary>
    PcmFormat From { get; }

    /// <summary>The output rate and channels.</summary>
    PcmFormat To { get; }

    /// <summary>
    /// Converts what it can of <paramref name="input"/> into <paramref name="output"/> (both whole frames). Returns the
    /// output samples written; <paramref name="consumed"/> says how many input samples were used (the rest is passed
    /// again). Writing nothing while consuming something is normal (the filter is filling up).
    /// </summary>
    int Convert(ReadOnlySpan<float> input, Span<float> output, out int consumed);

    /// <summary>
    /// After the last input: writes what the filter still holds (its last few milliseconds) into
    /// <paramref name="output"/>. Returns the samples written; 0 once drained. Further input needs a
    /// <see cref="Reset"/> first.
    /// </summary>
    int Drain(Span<float> output) => 0;

    /// <summary>Forgets the filter history, for a seek.</summary>
    void Reset();
}
