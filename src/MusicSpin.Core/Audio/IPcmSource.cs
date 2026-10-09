namespace MusicSpin.Core.Audio;

/// <summary>Pull-based source of interleaved float PCM. Called from the audio thread.</summary>
public interface IPcmSource
{
    PcmFormat Format { get; }

    /// <summary>
    /// Fills <paramref name="buffer"/> with interleaved samples. Returns the number of samples written;
    /// 0 means end of stream. Implementations must be fast and must not block for long.
    /// </summary>
    int Read(Span<float> buffer);
}
