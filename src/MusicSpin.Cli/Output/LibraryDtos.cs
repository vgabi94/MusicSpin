using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Output;

public sealed record ReplayGainDto(double? TrackGain, double? TrackPeak, double? AlbumGain, double? AlbumPeak)
{
    public static ReplayGainDto? From(TrackRecord t)
        => t.ReplayGainTrackGain is null && t.ReplayGainTrackPeak is null && t.ReplayGainAlbumGain is null && t.ReplayGainAlbumPeak is null
            ? null
            : new ReplayGainDto(t.ReplayGainTrackGain, t.ReplayGainTrackPeak, t.ReplayGainAlbumGain, t.ReplayGainAlbumPeak);
}

public sealed record LibraryTrackDto(
    string Id,
    string Path,
    string? Title,
    string? Artist,
    string AlbumArtist,
    string? Album,
    string AlbumId,
    int? TrackNumber,
    int? TrackCount,
    int? DiscNumber,
    int? DiscCount,
    int? Year,
    string? Genre,
    double DurationSeconds,
    string Format,
    string? Codec,
    int SampleRate,
    int? BitsPerSample,
    int? BitrateKbps,
    int Channels,
    bool Lossless,
    bool HiRes,
    ReplayGainDto? ReplayGain,
    bool HasEmbeddedCover,
    long SizeBytes,
    DateTimeOffset Modified,
    string TagSource)
{
    public static LibraryTrackDto From(TrackRecord t) => new(
        t.Id, t.Path, t.Title, t.Artist, t.EffectiveAlbumArtist, t.Album, t.AlbumId, t.TrackNumber, t.TrackCount, t.DiscNumber, t.DiscCount,
        t.Year, t.Genre, Math.Round(t.DurationSeconds, 3), t.Format, t.Codec, t.SampleRate, t.BitsPerSample, t.BitrateKbps, t.Channels,
        t.IsLossless, t.IsHiRes, ReplayGainDto.From(t), t.HasEmbeddedCover, t.SizeBytes, new DateTimeOffset(t.ModifiedUtcTicks, TimeSpan.Zero),
        t.TagSource);
}

public sealed record AlbumDto(
    string Id,
    string Title,
    string AlbumArtist,
    int? Year,
    int TrackCount,
    int DiscCount,
    double DurationSeconds,
    long SizeBytes,
    IReadOnlyList<string> Formats,
    bool Compilation,
    bool HiRes,
    int MaxSampleRate,
    int? MaxBitsPerSample,
    bool HasEmbeddedCover)
{
    public static AlbumDto From(AlbumRecord a) => new(
        a.Id, a.Title, a.AlbumArtist, a.Year, a.TrackCount, a.DiscCount, Math.Round(a.DurationSeconds, 3), a.SizeBytes, a.Formats,
        a.IsCompilation, a.IsHiRes, a.MaxSampleRate, a.MaxBitsPerSample, a.HasEmbeddedCover);
}

public sealed record ArtistDto(string Id, string Name, int TrackCount, int AlbumCount, double DurationSeconds)
{
    public static ArtistDto From(ArtistRecord a) => new(a.Id, a.Name, a.TrackCount, a.AlbumCount, Math.Round(a.DurationSeconds, 3));
}

public sealed record PageDto<T>(int Total, int Offset, int Limit, int Count, IReadOnlyList<T> Items);

public static class PageDto
{
    public static PageDto<T> From<TSource, T>(Page<TSource> page, Func<TSource, T> map)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PageDto<T>(page.Total, page.Offset, page.Limit, page.Items.Count, page.Items.Select(map).ToList());
    }
}

public sealed record SearchDto(string Query, IReadOnlyList<ArtistDto> Artists, IReadOnlyList<AlbumDto> Albums, PageDto<LibraryTrackDto> Tracks);

public sealed record DataDirDto(string Path, string Source, bool Exists, bool Writable, string? Error, string SettingsFile, bool SettingsExists,
    string? SettingsWarning, string LibraryFile, bool LibraryExists, long? LibrarySizeBytes)
{
    public static DataDirDto From(DataDirectory dir, bool writable, string? error, string? settingsWarning)
    {
        var db = new FileInfo(dir.LibraryDatabaseFile);
        return new DataDirDto(dir.Path, dir.Source.ToString().ToLowerInvariant(), Directory.Exists(dir.Path), writable, error, dir.SettingsFile,
            File.Exists(dir.SettingsFile), settingsWarning, dir.LibraryDatabaseFile, db.Exists, db.Exists ? db.Length : null);
    }
}

public sealed record ScanIssueDto(string Path, string Kind, string Message)
{
    public static ScanIssueDto From(ScanIssue i) => new(i.Path, i.Kind, i.Message);
}

public sealed record ScanResultDto(
    int Folders,
    int FoldersMissing,
    int FilesFound,
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    int Failed,
    int SkippedUnreadable,
    int Moved,
    int AmbiguousMoves,
    int Fingerprinted,
    int PlaylistEntriesUpdated,
    IReadOnlyList<TrackMove> Moves,
    int Tracks,
    int Albums,
    int Artists,
    bool Full,
    bool Cancelled,
    double ElapsedSeconds,
    int IssueCount,
    IReadOnlyList<ScanIssueDto> Issues)
{
    public const int MaxIssues = 200;

    public static ScanResultDto From(ScanResult r) => new(
        r.Folders, r.FoldersMissing, r.FilesFound, r.Added, r.Updated, r.Unchanged, r.Removed, r.Failed, r.SkippedUnreadable,
        r.Moved, r.AmbiguousMoves, r.Fingerprinted, r.PlaylistEntriesUpdated, r.Moves, r.Tracks, r.Albums, r.Artists, r.Full, r.Cancelled, Math.Round(r.Elapsed.TotalSeconds, 3), r.Issues.Count,
        r.Issues.Take(MaxIssues).Select(ScanIssueDto.From).ToList());
}

public sealed record ScanProgressDto(string Event, string Phase, int FilesFound, int FilesToRead, int FilesRead, int Failed, string? Path);

public sealed record FolderDto(string Path, bool Exists, int TrackCount, DateTimeOffset Added, DateTimeOffset? LastScan)
{
    public static FolderDto From(FolderInfo f) => new(f.Path, f.Exists, f.TrackCount, f.Added, f.LastScan);
}

public sealed record FoldersDto(string DataDir, IReadOnlyList<FolderDto> Folders);

public sealed record CheckDto(
    bool Healthy,
    int MissingFileCount,
    int MissingFolderCount,
    int DuplicateGroupCount,
    int MissingTagsCount,
    int UnreadableCount,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> MissingFolders,
    IReadOnlyList<DuplicateGroup> Duplicates,
    IReadOnlyList<MissingTagsEntry> MissingTags,
    IReadOnlyList<ScanIssueDto> Unreadable)
{
    public static CheckDto From(LibraryCheck c) => new(
        c.Healthy, c.MissingFiles.Count, c.MissingFolders.Count, c.Duplicates.Count, c.MissingTags.Count, c.Unreadable.Count,
        c.MissingFiles, c.MissingFolders, c.Duplicates, c.MissingTags, c.Unreadable.Select(ScanIssueDto.From).ToList());
}

public sealed record QueueEntryDto(LibraryTrackDto Track, double PlayedSeconds, string StopReason);

public sealed record QueueSkipDto(string Path, string Code, string Message);

public sealed record QueuePlayResultDto(
    string Mode,
    string? Album,
    string? Artist,
    bool Shuffle,
    int? Seed,
    int QueueLength,
    IReadOnlyList<QueueEntryDto> Played,
    IReadOnlyList<QueueSkipDto> Skipped,
    string StopReason);
