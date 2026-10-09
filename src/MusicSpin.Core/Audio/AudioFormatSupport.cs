namespace MusicSpin.Core.Audio;

/// <summary>Whether a given audio format/codec can be decoded by the active backend.</summary>
/// <param name="Id">Stable identifier, e.g. "flac", "alac", "dsd".</param>
/// <param name="Name">Display name, e.g. "FLAC".</param>
/// <param name="Extensions">Typical file extensions (without dot).</param>
/// <param name="Lossless">True for lossless/uncompressed formats.</param>
/// <param name="HiResCapable">True if the format can carry more than 16-bit/48 kHz.</param>
/// <param name="Supported">True if the backend can decode it right now.</param>
/// <param name="Decoders">Backend decoder names used for this format (e.g. FFmpeg codec names).</param>
public sealed record AudioFormatSupport(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    bool Lossless,
    bool HiResCapable,
    bool Supported,
    IReadOnlyList<string> Decoders);
