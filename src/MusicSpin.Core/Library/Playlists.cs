using System.Globalization;

namespace MusicSpin.Core.Library;

/// <summary>How a playlist entry resolves right now.</summary>
public enum PlaylistEntryStatus
{
    /// <summary>A library track (tags from the library; follows the track when it moves).</summary>
    Library,

    /// <summary>Not in the library, but the file exists (playable).</summary>
    File,

    /// <summary>Neither in the library nor on disk (kept, flagged, skipped when playing).</summary>
    Missing,
}

public sealed record PlaylistSummary(string Id, string Name, int EntryCount, int MissingCount, double DurationSeconds, DateTimeOffset Created, DateTimeOffset Modified);

/// <summary>A resolved entry. <see cref="Position"/> is 1-based.</summary>
public sealed record PlaylistEntry(
    int Position,
    PlaylistEntryStatus Status,
    string Path,
    string? TrackId,
    string? Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    int? TrackNumber,
    double? DurationSeconds);

public sealed record PlaylistDetails(PlaylistSummary Playlist, Page<PlaylistEntry> Entries);

/// <summary>An entry to add. <paramref name="TrackId"/> null means "resolve <paramref name="Path"/> against the library".</summary>
public sealed record PlaylistInput(string Path, string? TrackId = null, string? Title = null, string? Artist = null, double? DurationSeconds = null)
{
    public static PlaylistInput From(TrackRecord track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new PlaylistInput(track.Path, track.Id);
    }
}

public sealed record PlaylistChange(PlaylistSummary Playlist, int Added = 0, int Removed = 0, bool Replaced = false);

public sealed record PlaylistImportMissing(int Line, string Entry);

public sealed record PlaylistImportResult(
    PlaylistSummary Playlist,
    string File,
    string Format,
    string Encoding,
    int Entries,
    int InLibrary,
    int OutsideLibrary,
    int MissingCount,
    IReadOnlyList<PlaylistImportMissing> Missing,
    IReadOnlyList<string> SkippedStreams,
    IReadOnlyList<string> Warnings,
    bool Replaced);

public sealed record PlaylistExportResult(string Name, string File, string Format, int Entries, int MissingCount, bool Relative, IReadOnlyList<string> Warnings);

/// <summary>Parses 1-based positions and ranges: <c>3</c>, <c>2-5</c>, <c>1,4,6-8</c>.</summary>
public static class PlaylistPositions
{
    /// <summary>Zero-based, distinct, ascending indexes.</summary>
    /// <exception cref="MusicSpinException">InvalidArgument for syntax errors, 0, reversed ranges or positions past <paramref name="count"/>.</exception>
    public static IReadOnlyList<int> Parse(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid("Give a position (1-based), a range like 2-5, or a list like 1,3,5-7.");
        }

        var result = new SortedSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            int from, to;
            if (dash > 0)
            {
                from = Number(part[..dash].Trim(), text);
                to = Number(part[(dash + 1)..].Trim(), text);
                if (to < from)
                {
                    throw Invalid($"Range '{part}' is reversed; write it as {to}-{from}.");
                }
            }
            else
            {
                from = to = Number(part, text);
            }

            if (to > count)
            {
                throw Invalid(count == 0
                    ? "The playlist is empty."
                    : $"Position {to} is out of range: the playlist has {count} entr{(count == 1 ? "y" : "ies")}.");
            }

            for (var i = from; i <= to; i++)
            {
                result.Add(i - 1);
            }
        }

        return [.. result];
    }

    /// <summary>A single 1-based position within 1..<paramref name="count"/>, as a zero-based index.</summary>
    public static int One(int position, int count, string what)
        => position < 1 || position > count
            ? throw Invalid(count == 0 ? "The playlist is empty." : $"{what} {position} is out of range: use 1-{count}.")
            : position - 1;

    private static int Number(string value, string text)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1
            ? n
            : throw Invalid($"'{text}' is not a valid position or range. Positions start at 1; use e.g. 3, 2-5 or 1,3,5-7.");

    private static MusicSpinException Invalid(string message) => new(MusicSpinErrorCode.InvalidArgument, message);
}
