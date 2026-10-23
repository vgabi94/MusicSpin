namespace MusicSpin.Core.Library;

// LiteDB document classes. Ids are hashes (see LibraryKeys) because LiteDB index keys are limited to 1023 bytes.
// Times are stored as UTC ticks (long) to avoid LiteDB's millisecond DateTime rounding breaking mtime comparisons.

/// <summary>A folder the user added to the library.</summary>
public sealed class LibraryFolderRecord
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long AddedUtcTicks { get; set; }
    public long? LastScanUtcTicks { get; set; }
    public int TrackCount { get; set; }
}

/// <summary>One audio file.</summary>
public sealed class TrackRecord
{
    /// <summary>
    /// GUID ("D" format, UUIDv7) assigned when the file is first added. It does not depend on the path: renames/moves found
    /// by a scan and <c>library relocate</c> keep it. A file that is removed from the library and added again gets a new one.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Normalized absolute path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary><see cref="LibraryKeys.PathKey"/> of <see cref="Path"/> (unique index; used to find a file's track during scans).</summary>
    public string PathKey { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public long AddedUtcTicks { get; set; }

    /// <summary><see cref="FileFingerprint"/> of the file as last read (empty: not computed yet). Used to detect renames/moves.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    // Tags
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public int? DiscCount { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }
    public bool IsCompilation { get; set; }
    public double? ReplayGainTrackGain { get; set; }
    public double? ReplayGainTrackPeak { get; set; }
    public double? ReplayGainAlbumGain { get; set; }
    public double? ReplayGainAlbumPeak { get; set; }
    public bool HasEmbeddedCover { get; set; }

    // Analysed loudness (#139): measured on first play of an untagged file, valid for the file with the size and modification
    // time below (a changed file gets a fresh record from the scan, which clears all of it).
    public double? LoudnessLufs { get; set; }
    public double? LoudnessTruePeak { get; set; }
    public bool LoudnessAnalyzed { get; set; }

    /// <summary>The <see cref="MusicSpin.Core.Normalization.LoudnessMeter.AlgorithmVersion"/> that measured it; 0 (not stored) never matches, so such a result is measured again.</summary>
    public int LoudnessVersion { get; set; }
    public long LoudnessSizeBytes { get; set; }
    public long LoudnessModifiedUtcTicks { get; set; }

    // Audio properties
    public double DurationSeconds { get; set; }
    public string Format { get; set; } = string.Empty;
    public string? Codec { get; set; }
    public int SampleRate { get; set; }
    public int? BitsPerSample { get; set; }
    public int? BitrateKbps { get; set; }
    public int Channels { get; set; }
    public bool IsLossless { get; set; }
    public bool IsHiRes { get; set; }

    /// <summary>"taglib", "taglib+ffmpeg" or "ffmpeg": which reader produced the data.</summary>
    public string TagSource { get; set; } = string.Empty;

    // Derived by the indexer
    public string EffectiveAlbumArtist { get; set; } = string.Empty;
    public string ArtistKey { get; set; } = string.Empty;
    public string AlbumArtistKey { get; set; } = string.Empty;
    public string AlbumId { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;

    // Folded search keys (LibrarySearch.Fold), computed by the indexer. Several values in one key are separated by U+001F.

    /// <summary>All search keys below, combined (used to narrow a search inside the database).</summary>
    public string SearchText { get; set; } = string.Empty;
    public string SearchTitle { get; set; } = string.Empty;

    /// <summary>Track artist, album artist and effective album artist (e.g. "various artists").</summary>
    public string SearchArtist { get; set; } = string.Empty;
    public string SearchAlbum { get; set; } = string.Empty;
    public string SearchGenre { get; set; } = string.Empty;

    /// <summary>File name without extension.</summary>
    public string SearchFile { get; set; } = string.Empty;
}

/// <summary>Album = tracks grouped by (effective album artist, album title), case-insensitively.</summary>
public sealed class AlbumRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string AlbumArtistKey { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public int? Year { get; set; }

    /// <summary>The genre most of its tracks have (null: none tagged).</summary>
    public string? Genre { get; set; }
    public int TrackCount { get; set; }
    public int DiscCount { get; set; }
    public double DurationSeconds { get; set; }
    public long SizeBytes { get; set; }
    public List<string> Formats { get; set; } = [];
    public List<string> ArtistKeys { get; set; } = [];
    public bool IsCompilation { get; set; }
    public bool IsHiRes { get; set; }
    public int MaxSampleRate { get; set; }
    public int? MaxBitsPerSample { get; set; }
    public bool HasEmbeddedCover { get; set; }

    // Folded search keys (see TrackRecord).
    public string SearchTitle { get; set; } = string.Empty;

    /// <summary>Album artist and every track artist.</summary>
    public string SearchArtist { get; set; } = string.Empty;
    public string SearchGenre { get; set; } = string.Empty;
}

/// <summary>Artist = track artists and album artists, merged case-insensitively.</summary>
public sealed class ArtistRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public int AlbumCount { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>Folded name (see TrackRecord).</summary>
    public string SearchName { get; set; } = string.Empty;
}

/// <summary>A file or folder the scanner could not use. File issues are re-tried only when size/mtime change.</summary>
public sealed class ScanIssueRecord
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    /// <summary>unreadable | access_denied | folder_missing | symlink_loop | missing.</summary>
    public string Kind { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public long AtUtcTicks { get; set; }
}

/// <summary>A saved playlist. Entries are embedded and ordered; names are unique case-insensitively (<see cref="NameKey"/>).</summary>
public sealed class PlaylistRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="LibraryKeys.NameKey"/> of <see cref="Name"/> (unique index).</summary>
    public string NameKey { get; set; } = string.Empty;
    public long CreatedUtcTicks { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public List<PlaylistEntryRecord> Entries { get; set; } = [];
}

/// <summary>
/// One playlist entry: the library track id when the file is in the library (null otherwise) and the path, so the entry
/// can be re-resolved when the track id is unknown or gone. Title/artist/duration are hints from an imported file
/// (#EXTINF / PLS TitleN) for entries outside the library.
/// </summary>
public sealed class PlaylistEntryRecord
{
    public string? TrackId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public double? DurationSeconds { get; set; }
}

/// <summary>
/// A favorite track. Kept apart from <see cref="TrackRecord"/> (a rescan rebuilds track records) and kept when its track
/// leaves the library: it is found again by <see cref="Id"/> (the track id), else by <see cref="PathKey"/> (the same file
/// added back under a new id).
/// </summary>
public sealed class FavoriteRecord
{
    /// <summary>The favorite track's id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The track's path, kept current on moves and <c>library relocate</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary><see cref="LibraryKeys.PathKey"/> of <see cref="Path"/>.</summary>
    public string PathKey { get; set; } = string.Empty;
    public long FavoritedUtcTicks { get; set; }
}
