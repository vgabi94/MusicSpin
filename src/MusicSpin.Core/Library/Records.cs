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
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public long AddedUtcTicks { get; set; }

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
    public string SearchText { get; set; } = string.Empty;
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
