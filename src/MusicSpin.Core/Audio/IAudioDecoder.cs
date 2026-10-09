using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Audio;

/// <summary>An opened, seekable decoded audio stream.</summary>
public interface IAudioDecoder : IPcmSource, IDisposable
{
    TrackInfo Track { get; }

    /// <summary>Current decode position (time of the next sample returned by <see cref="IPcmSource.Read"/>).</summary>
    TimeSpan Position { get; }

    bool CanSeek { get; }

    /// <summary>Seeks to <paramref name="position"/>; implementations clamp to [0, duration].</summary>
    void Seek(TimeSpan position);
}

/// <summary>Opens files for decoding and probes metadata.</summary>
public interface IAudioDecoderFactory
{
    /// <summary>Human-readable backend name, e.g. "FFmpeg 9.0.2".</summary>
    string Name { get; }

    /// <summary>True when the decoding backend (e.g. FFmpeg shared libraries) could be loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads metadata and stream properties without decoding audio.</summary>
    /// <exception cref="MusicSpinException">FileNotFound, UnsupportedFormat or DecoderUnavailable.</exception>
    TrackInfo Probe(string path);

    /// <exception cref="MusicSpinException">FileNotFound, UnsupportedFormat or DecoderUnavailable.</exception>
    IAudioDecoder Open(string path);

    /// <summary>Formats this backend can decode.</summary>
    IReadOnlyList<AudioFormatSupport> GetSupportedFormats();
}
