using MusicSpin.Core.Library;

namespace MusicSpin.Core.Metadata;

/// <summary>Stream properties and tags of an audio file.</summary>
public sealed record TrackInfo
{
    public required string Path { get; init; }

    /// <summary>Container/demuxer name, e.g. "flac", "mov,mp4,m4a,3gp,3g2,mj2", "wav".</summary>
    public required string Container { get; init; }

    /// <summary>Short codec name, e.g. "flac", "alac", "pcm_s24le", "dsd_lsbf_planar".</summary>
    public required string Codec { get; init; }

    public string? CodecLongName { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public string? ChannelLayout { get; init; }

    /// <summary>Bits per sample of the source (e.g. 16, 24, 32; 1 for DSD). Null when unknown/not meaningful (lossy).</summary>
    public int? BitsPerSample { get; init; }

    /// <summary>Decoder output sample format, e.g. "s32", "fltp".</summary>
    public string? SampleFormat { get; init; }

    /// <summary>Average bit rate in bits/second, when known.</summary>
    public long? BitRate { get; init; }

    public TimeSpan Duration { get; init; }

    public bool IsLossless { get; init; }

    /// <summary>
    /// Hi-res by the library's rule (<see cref="AudioFileTypes.IsHiRes"/>), so <c>info</c>, the player and the library
    /// always label a file the same way: lossless and either more than 16 bits per sample or a sample rate above 48 kHz;
    /// DSD always counts.
    /// </summary>
    public bool IsHiRes
    {
        get
        {
            var format = AudioFileTypes.FormatId(Codec, System.IO.Path.GetExtension(Path));
            return AudioFileTypes.IsHiRes(format, IsLossless || AudioFileTypes.IsLosslessFormat(format), BitsPerSample, SampleRate);
        }
    }

    public string? Title => Tag("title");
    public string? Artist => Tag("artist");
    public string? Album => Tag("album");
    public string? AlbumArtist => Tag("album_artist");
    public string? Genre => Tag("genre");
    public string? Date => Tag("date");
    public string? TrackNumber => Tag("track");

    /// <summary>All tags, keys lower-cased (container-level first, stream-level for missing keys).</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private string? Tag(string key) => Tags.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
