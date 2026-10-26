using MusicSpin.Core.Audio;
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
/// <param name="Exclusive">In <c>status.output</c>, once a device opened: whether it really is exclusive; before, what was asked.</param>
/// <param name="Format">What the device opened at (rate, sample format); null until a track opened it.</param>
/// <param name="Notice">Why the output is not what was asked (exclusive refused, resampled); null when it is.</param>
public sealed record OutputInfo(string Backend, string? Device, bool Exclusive, OutputFormat? Format = null, string? Notice = null)
{
    /// <summary>The requested output updated with what the device really opened at.</summary>
    public OutputInfo With(OpenedOutput opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return this with
        {
            Device = opened.Device ?? Device,
            Exclusive = opened.Exclusive,
            Format = new OutputFormat(opened.SampleRate, opened.Channels, opened.SampleFormat, opened.BitsPerSample, opened.Verified),
            Notice = opened.Notice,
        };
    }

    /// <summary>"exclusive, 44.1 kHz 24-bit" (plus the format when known).</summary>
    public string Describe()
        => Format is { } f
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{(Exclusive ? "exclusive" : "shared")}, {f.SampleRate / 1000.0:0.###} kHz {f.BitsPerSample}-bit{(f.SampleFormat == "f32" ? " float" : string.Empty)}")
            : Exclusive ? "exclusive" : "shared";
}

/// <summary>The format a device opened at.</summary>
/// <param name="SampleFormat">s16 | s24 | s24in32 | s32 | f32.</param>
/// <param name="Verified">Read back from the device (WASAPI exclusive) rather than what was asked for.</param>
public sealed record OutputFormat(int SampleRate, int Channels, string SampleFormat, int BitsPerSample, bool Verified);

/// <summary>Snapshot returned by most player commands.</summary>
/// <param name="QueueIndex">0-based index of the current queue entry (in play order); null when the queue is empty.</param>
/// <param name="Ended">True after the last queue entry finished (play/resume restarts from the first entry).</param>
/// <param name="Repeat">off | one | all.</param>
/// <param name="Shuffle">True when the queue is in shuffled play order (turning it off restores the original order).</param>
/// <param name="ShuffleSeed">Seed of the latest full shuffle (reproduces it); null when shuffle is off.</param>
/// <param name="Gapless">Whether same-format tracks follow each other without a gap (settings.json "gapless").</param>
/// <param name="Underruns">Times the output ran dry and played silence because decoding fell behind (since the player started).</param>
/// <param name="UpNext">How many entries after the current one are up next (added to play next; see <see cref="QueueEntry.UpNext"/>).</param>
/// <param name="Volume">The volume level, 0..1; while <paramref name="Muted"/>, the level unmuting goes back to.</param>
/// <param name="Muted">Muted: the player plays at 0 and keeps <paramref name="Volume"/> (the <c>mute</c> command; a volume unmutes).</param>
/// <param name="Normalization">Volume levelling (ReplayGain / R128); null when the host has none.</param>
/// <param name="Equalizer">The 10-band graphic equalizer; null when the host has none.</param>
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
    IReadOnlyList<SkippedItem>? Skipped,
    RepeatMode Repeat = RepeatMode.Off,
    bool Shuffle = false,
    int? ShuffleSeed = null,
    bool? Gapless = null,
    long? Underruns = null,
    RadioInfo? Radio = null,
    int UpNext = 0,
    bool Muted = false,
    NormalizationStatus? Normalization = null,
    EqualizerStatus? Equalizer = null)
{
    /// <summary>
    /// "radio" while a radio station plays (see <see cref="Radio"/>), else "queue". (Named so it does not clash with the
    /// "mode": "background"|"foreground" that <c>music-spin play --json</c> adds.)
    /// </summary>
    public string QueueMode => Radio is null ? "queue" : "radio";
}

/// <summary>Volume levelling in a <see cref="PlayerStatus"/>: the setting and what it does to the current track.</summary>
/// <param name="Mode">"off", "track", "album" or "auto".</param>
/// <param name="TargetLufs">The loudness tracks are brought to.</param>
/// <param name="PreampDb">Added to every gain.</param>
/// <param name="GainDb">The current track's gain in dB; null when nothing is loaded.</param>
/// <param name="Source">Where it came from: "track-tag", "album-tag", "analysis", "unknown" (not known yet: plays unchanged) or "off".</param>
/// <param name="Limited">The gain was lowered so the track's peak stays below full scale.</param>
public sealed record NormalizationStatus(string Mode, double TargetLufs, double PreampDb, double? GainDb = null, string? Source = null, bool Limited = false);

/// <summary>The radio station in a <see cref="PlayerStatus"/>: its filters, a description and how many tracks matched at the last top-up.</summary>
public sealed record RadioInfo(MusicSpin.Core.Library.RadioFilter Filter, string Description, int PoolSize);

/// <summary>Radio mode as saved in queue.json: the filters and the recent picks (so no-repeat and spacing survive a restart).</summary>
public sealed record RadioState(MusicSpin.Core.Library.RadioFilter Filter, IReadOnlyList<string>? Recent, string? LastArtist, string? LastAlbum);

/// <summary>A page of the queue.</summary>
public sealed record QueueListing(int Total, int Offset, int Limit, int? CurrentIndex, IReadOnlyList<QueueEntry> Items);

/// <param name="UpNext">Added by the user to play after the current entry (see <see cref="Playback.QueuePosition"/>), before the rest of the queue or the radio station.</param>
public sealed record QueueEntry(int Index, bool Current, QueueItem Item, bool UpNext = false);

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

/// <summary>
/// The persisted state of a queue (<c>.usrdata/queue.json</c>): entries in play order, their original-order keys (so
/// shuffle off can restore the original order after a restart), the current index and position, the modes, and the
/// indexes of the up-next entries (null: none).
/// </summary>
public sealed record QueueSnapshot(
    int Version,
    IReadOnlyList<QueueItem> Items,
    IReadOnlyList<long> Order,
    int? Index,
    double PositionSeconds,
    RepeatMode Repeat,
    bool Shuffle,
    int? Seed,
    DateTimeOffset SavedAt,
    RadioState? Radio = null,
    IReadOnlyList<int>? UpNext = null)
{
    /// <summary>The only format read and written. No migrations before the first release: another version is treated like a corrupt file.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>The graphic equalizer (#141) in a <see cref="PlayerStatus"/>.</summary>
/// <param name="Enabled">Whether it filters playback (off, or on and flat, is bit-exact pass-through).</param>
/// <param name="Preset">The preset the gains came from; null when a band was changed by hand.</param>
/// <param name="PreampDb">The user's pre-amp in dB.</param>
/// <param name="HeadroomDb">How far the pre-amp is lowered automatically so boosts cannot clip (0 when nothing is boosted), at the current track's rate.</param>
/// <param name="Bands">The band centres in Hz (31.25 .. 16000).</param>
/// <param name="Gains">The gain of each band in dB (-12..+12).</param>
/// <param name="Presets">Every preset's name: the built-in ones, then the user's.</param>
/// <param name="UserPresets">The user's saved presets: name to band gains.</param>
public sealed record EqualizerStatus(
    bool Enabled,
    string? Preset,
    double PreampDb,
    double HeadroomDb,
    IReadOnlyList<double> Bands,
    IReadOnlyList<double> Gains,
    IReadOnlyList<string> Presets,
    IReadOnlyDictionary<string, IReadOnlyList<double>> UserPresets);
