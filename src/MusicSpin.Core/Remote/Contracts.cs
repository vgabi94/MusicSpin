using MusicSpin.Core.Metadata;
using MusicSpin.Core.Playback;

namespace MusicSpin.Core.Remote;

/// <summary>Stable JSON shape of a decoded track (codec, sample rate, bit depth, tags). Add fields freely; never rename/remove.</summary>
public sealed record TrackDto(
    string Path,
    string Container,
    string Codec,
    string? CodecLongName,
    int SampleRate,
    int? BitsPerSample,
    int Channels,
    string? ChannelLayout,
    string? SampleFormat,
    long? BitRate,
    double DurationSeconds,
    bool Lossless,
    bool HiRes,
    string? Title,
    string? Artist,
    string? Album,
    IReadOnlyDictionary<string, string> Tags)
{
    public static TrackDto From(TrackInfo t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(
            t.Path, t.Container, t.Codec, t.CodecLongName, t.SampleRate, t.BitsPerSample, t.Channels, t.ChannelLayout,
            t.SampleFormat, t.BitRate, Math.Round(t.Duration.TotalSeconds, 3), t.IsLossless, t.IsHiRes,
            t.Title, t.Artist, t.Album, t.Tags);
    }
}

/// <summary>
/// One queue entry. Only <see cref="Path"/> is required; the rest is library metadata known before decoding
/// (the decoded <see cref="TrackDto"/> in the status is authoritative once the track is loaded).
/// </summary>
public sealed record QueueItem(
    string Path,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    int? TrackNumber = null,
    double? DurationSeconds = null,
    string? TrackId = null);

/// <summary>A queue entry that could not be played (deleted/moved file, unsupported, decode error) and was skipped.</summary>
public sealed record SkippedItem(int Index, string Path, string Code, string Message);

/// <summary>Output the player renders to.</summary>
public sealed record OutputInfo(string Backend, string? Device, bool Exclusive);

/// <summary>Snapshot returned by most player commands.</summary>
/// <param name="QueueIndex">0-based index of the current queue entry; null when the queue is empty.</param>
/// <param name="Ended">True after the last queue entry finished (play/resume restarts from the first entry).</param>
public sealed record PlayerStatus(
    PlayerState State,
    double PositionSeconds,
    double DurationSeconds,
    float Volume,
    TrackDto? Track,
    int? QueueIndex,
    int QueueLength,
    QueueItem? Item,
    OutputInfo? Output,
    bool Ended,
    string? LastError,
    IReadOnlyList<SkippedItem>? Skipped);

/// <summary>A page of the queue.</summary>
public sealed record QueueListing(int Total, int Offset, int Limit, int? CurrentIndex, IReadOnlyList<QueueEntry> Items);

public sealed record QueueEntry(int Index, bool Current, QueueItem Item);

/// <summary>Information about the background player process (<c>hello</c> / <c>music-spin player status</c>).</summary>
public sealed record PlayerProcessInfo(
    int Protocol,
    string Version,
    int Pid,
    DateTimeOffset StartedAt,
    string Endpoint,
    string DataDir,
    string? LogFile,
    double? IdleTimeoutSeconds,
    OutputInfo Output);
