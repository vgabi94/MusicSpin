namespace MusicSpin.Core.Library;

public enum TrackSort
{
    /// <summary>Album artist, album, disc, track, file name.</summary>
    Default,
    Title,
    Artist,
    Album,
    Year,
    Duration,
    Added,
    SampleRate,
    BitDepth,
    Size,
    Path,

    /// <summary>Best match first (exact field > prefix > word start > anywhere), then the default order. Only with a search text.</summary>
    Relevance,

    /// <summary>When the track was favorited (<see cref="TrackQuery.Descending"/>: newest first). Only with <see cref="TrackQuery.Favorite"/> = true.</summary>
    Favorited,
}

public enum AlbumSort
{
    Artist,
    Title,
    Year,
    Tracks,
    Duration,
}

public enum ArtistSort
{
    Name,
    Tracks,
    Albums,
}

/// <summary>Paging window. Limit 1..10000, offset ≥ 0.</summary>
public sealed record Paging(int Offset = 0, int Limit = Paging.DefaultLimit)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 10_000;

    public static string? Validate(int offset, int limit)
        => offset < 0 ? "--offset must be 0 or greater."
            : limit is < 1 or > MaxLimit ? $"--limit must be between 1 and {MaxLimit}."
            : null;

    public void EnsureValid()
    {
        if (Validate(Offset, Limit) is { } error)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), error);
        }
    }
}

public sealed record Page<T>(int Total, int Offset, int Limit, IReadOnlyList<T> Items);

/// <summary>Track filter; all set criteria must match.</summary>
public sealed record TrackQuery
{
    /// <summary>Track artist or album artist: the exact name (case-insensitive; diacritics ignored when nothing matches exactly). Unknown: NotFound.</summary>
    public string? Artist { get; init; }

    /// <summary>Album title (case-insensitive exact; diacritics ignored when nothing matches exactly) or album id. Unknown: NotFound.</summary>
    public string? Album { get; init; }

    /// <summary>Search syntax (see <see cref="LibrarySearch.Parse"/>): words, "phrases", artist:/album:/title:/genre:/year:.</summary>
    public string? Text { get; init; }

    /// <summary>Every word must appear in the track title (folded, see <see cref="LibrarySearch.Fold"/>).</summary>
    public string? Title { get; init; }

    /// <summary>Every word must appear in the album title.</summary>
    public string? AlbumContains { get; init; }

    /// <summary>Every word must appear in the track artist or album artist.</summary>
    public string? ArtistContains { get; init; }

    public int? MinBitDepth { get; init; }
    public int? MinSampleRate { get; init; }

    /// <summary>Format ids (flac, alac, dsd, pcm, mp3, ...).</summary>
    public IReadOnlyList<string>? Formats { get; init; }

    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public bool? HiRes { get; init; }

    /// <summary>True: only favorites (the Favorites view); false: only the other tracks.</summary>
    public bool? Favorite { get; init; }
    public TrackSort Sort { get; init; }
    public bool Descending { get; init; }
    public Paging Paging { get; init; } = new();
}

public sealed record AlbumQuery
{
    /// <summary>Album artist or any track artist (exact name, as <see cref="TrackQuery.Artist"/>). Unknown: NotFound.</summary>
    public string? Artist { get; init; }

    /// <summary>Every word must appear in the album title.</summary>
    public string? Title { get; init; }

    /// <summary>Every word must appear in the album artist or a track artist.</summary>
    public string? ArtistContains { get; init; }

    public bool? HiRes { get; init; }
    public AlbumSort Sort { get; init; }
    public bool Descending { get; init; }
    public Paging Paging { get; init; } = new();
}

public sealed record ArtistQuery
{
    /// <summary>Every word must appear in the name (folded).</summary>
    public string? Name { get; init; }
    public ArtistSort Sort { get; init; }
    public bool Descending { get; init; }
    public Paging Paging { get; init; } = new();
}

public sealed record FolderInfo(string Path, bool Exists, int TrackCount, DateTimeOffset Added, DateTimeOffset? LastScan);

/// <summary>added | exists | covered (already inside another library folder).</summary>
public sealed record FolderAddResult(string Status, string Path, string? CoveredBy, IReadOnlyList<string> Absorbed);

public sealed record FolderRemoveResult(string Path, int TracksRemoved);

public sealed record FormatStats(string Format, int Tracks, long SizeBytes, double DurationSeconds);

public sealed record ResolutionStats(string Resolution, int Tracks);

public sealed record LibraryStats(
    int Folders,
    int Tracks,
    int Albums,
    int Artists,
    long TotalSizeBytes,
    double TotalDurationSeconds,
    int HiResTracks,
    int HiResAlbums,
    int LosslessTracks,
    IReadOnlyList<FormatStats> ByFormat,
    IReadOnlyList<ResolutionStats> ByResolution,
    DateTimeOffset? LastScan);

public sealed record MissingTagsEntry(string Path, IReadOnlyList<string> Missing);

public sealed record DuplicateGroup(string Artist, string Title, double DurationSeconds, IReadOnlyList<string> Paths);

public sealed record LibraryCheck(
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> MissingFolders,
    IReadOnlyList<DuplicateGroup> Duplicates,
    IReadOnlyList<MissingTagsEntry> MissingTags,
    IReadOnlyList<ScanIssue> Unreadable)
{
    public bool Healthy => MissingFiles.Count == 0 && MissingFolders.Count == 0 && Duplicates.Count == 0 && MissingTags.Count == 0 && Unreadable.Count == 0;
}

public sealed record SearchResult(IReadOnlyList<ArtistRecord> Artists, IReadOnlyList<AlbumRecord> Albums, Page<TrackRecord> Tracks);
