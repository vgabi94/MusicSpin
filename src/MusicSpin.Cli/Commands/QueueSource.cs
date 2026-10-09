using System.CommandLine;
using System.CommandLine.Parsing;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Commands;

/// <summary>Queue items built from a source, the shuffle seed used (null when not shuffled) and warnings (e.g. missing playlist entries).</summary>
internal sealed record ResolvedQueue(List<QueueItem> Items, int? Seed, List<string> Warnings);

/// <summary>
/// Library tracks to play or add: an exact album/artist (<see cref="IsExact"/>), or a query (<see cref="IsQuery"/>: search syntax
/// and/or per-field "contains" filters, combined with AND, in library order).
/// </summary>
internal sealed record LibrarySelection(string? Album = null, string? Artist = null, string? Search = null, string? Title = null,
    string? AlbumContains = null, string? ArtistContains = null)
{
    public bool IsExact => Album is not null || Artist is not null;

    public bool IsQuery => Search is not null || Title is not null || AlbumContains is not null || ArtistContains is not null;

    public int Kinds => (IsExact ? 1 : 0) + (IsQuery ? 1 : 0);

    public string Describe() => string.Join(", ", new[]
    {
        Search is null ? null : $"'{Search}'", Title is null ? null : $"title '{Title}'",
        AlbumContains is null ? null : $"album '{AlbumContains}'", ArtistContains is null ? null : $"artist '{ArtistContains}'",
    }.Where(s => s is not null));
}

/// <summary>"What to play" options shared by `play` and `queue add`: files, --album, --artist, --search, --playlist, --shuffle, --seed.</summary>
internal sealed class QueueSource
{
    public Argument<string[]> Files { get; } = new("files")
    {
        Description = "Audio file(s) to play (or use --album / --artist / --search to play from the library).",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public Option<string?> Album { get; } = new("--album")
    {
        Description = "An album from the library (title, case-insensitive, or album id GUID). Combine with --artist to disambiguate.",
    };

    public Option<string?> Artist { get; } = new("--artist")
    {
        Description = "All library tracks by an artist (albums by year), or narrow --album.",
    };

    public Option<string?> Search { get; } = new("--search")
    {
        Description = "Library tracks matching this text (same matching as 'music-spin search'), up to 10000.",
    };

    public Option<string?> Title { get; } = new("--title")
    {
        Description = "Library tracks whose title contains these words (case/diacritic-insensitive). Combines with --search/--album-contains/--artist-contains.",
    };

    public Option<string?> AlbumContains { get; } = new("--album-contains")
    {
        Description = "Library tracks whose album title contains these words.",
    };

    public Option<string?> ArtistContains { get; } = new("--artist-contains")
    {
        Description = "Library tracks whose artist or album artist contains these words.",
    };

    public Option<string?> Playlist { get; } = new("--playlist")
    {
        Description = "A saved playlist (name, case-insensitive). Missing entries are left out with a warning.",
    };

    public Option<bool> Shuffle { get; } = new("--shuffle")
    {
        Description = "Shuffle. With play: turns the player's shuffle mode on (turn it off later to get the original order). With queue add: shuffles the added tracks.",
    };

    public Option<int?> Seed { get; } = new("--seed")
    {
        Description = "Shuffle seed for a reproducible order (default: random, reported in the JSON result).",
    };

    public void AddTo(Command command)
    {
        command.Add(Files);
        command.Add(Album);
        command.Add(Artist);
        command.Add(Search);
        command.Add(Title);
        command.Add(AlbumContains);
        command.Add(ArtistContains);
        command.Add(Playlist);
        command.Add(Shuffle);
        command.Add(Seed);
    }

    /// <summary>Exactly one kind of source; --seed needs --shuffle.</summary>
    public void Validate(CommandResult r, bool required = true)
    {
        var hasFiles = Options.Get(r, Files) is { Length: > 0 } files && files.Any(f => !string.IsNullOrWhiteSpace(f));
        var selection = new LibrarySelection(Options.Get(r, Album), Options.Get(r, Artist), Options.Get(r, Search), Options.Get(r, Title),
            Options.Get(r, AlbumContains), Options.Get(r, ArtistContains));
        var hasPlaylist = Options.Get(r, Playlist) is not null;
        var kinds = (hasFiles ? 1 : 0) + selection.Kinds + (hasPlaylist ? 1 : 0);
        if (kinds > 1)
        {
            r.AddError(SourceConflict);
        }
        else if (kinds == 0 && required)
        {
            r.AddError("Nothing to play: give a file, --album <title>, --artist <name>, --search <text>, --title/--album-contains/--artist-contains <words> or --playlist <name>.");
        }
        else if (Options.Get(r, Seed) is not null && !Options.Get(r, Shuffle))
        {
            r.AddError("--seed only applies together with --shuffle.");
        }
    }

    public bool HasFiles(ParseResult parseResult) => parseResult.GetValue(Files) is { Length: > 0 };

    public const string SourceConflict = "Give either file(s), --album/--artist, --search/--title/--album-contains/--artist-contains, or --playlist, not several.";

    public LibrarySelection Selection(ParseResult parseResult) => new(parseResult.GetValue(Album), parseResult.GetValue(Artist), parseResult.GetValue(Search),
        parseResult.GetValue(Title), parseResult.GetValue(AlbumContains), parseResult.GetValue(ArtistContains));

    /// <summary>Library tracks for --album/--artist, or for --search and the contains filters (null for files). Releases the library before returning.</summary>
    public IReadOnlyList<TrackRecord>? LibraryTracks(CommandRunner runner, ParseResult parseResult)
        => LibraryTracks(runner, parseResult, Selection(parseResult));

    /// <exception cref="MusicSpinException">NotFound (unknown album/artist, nothing matches); InvalidArgument (unusable search text, ambiguous album).</exception>
    public static IReadOnlyList<TrackRecord>? LibraryTracks(CommandRunner runner, ParseResult parseResult, LibrarySelection selection)
    {
        if (selection.Kinds == 0)
        {
            return null;
        }

        using var library = runner.OpenLibraryForRead(parseResult);
        if (selection.IsQuery)
        {
            var tracks = library.Tracks(new TrackQuery
            {
                Text = selection.Search,
                Title = selection.Title,
                AlbumContains = selection.AlbumContains,
                ArtistContains = selection.ArtistContains,
                Paging = new Paging(0, Paging.MaxLimit),
            }).Items;
            return tracks.Count > 0
                ? tracks
                : throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No library tracks match {selection.Describe()}. See 'music-spin search'.");
        }

        return selection.Album is not null ? library.AlbumQueue(selection.Album, selection.Artist) : library.ArtistQueue(selection.Artist!);
    }

    /// <summary>Applies --shuffle/--seed; returns the seed used (null when not shuffled).</summary>
    public int? ApplyShuffle<T>(ParseResult parseResult, ref IReadOnlyList<T> items)
        => ApplyShuffle(parseResult.GetValue(Shuffle), parseResult.GetValue(Seed), ref items);

    public static int? ApplyShuffle<T>(bool shuffle, int? seed, ref IReadOnlyList<T> items)
    {
        if (PickSeed(shuffle, seed) is not { } used)
        {
            return null;
        }

        items = MusicLibrary.Shuffle(items, used);
        return used;
    }

    /// <summary>The seed to use for --shuffle (given or random); null without --shuffle.</summary>
    public static int? PickSeed(bool shuffle, int? seed)
    {
#pragma warning disable CA5394 // not security sensitive
        return shuffle ? seed ?? Random.Shared.Next() : null;
#pragma warning restore CA5394
    }

    /// <summary>Queue items for the background player (files resolved to absolute paths: the player has another working directory).</summary>
    public ResolvedQueue Resolve(CommandRunner runner, ParseResult parseResult, bool? shuffle = null)
        => Resolve(runner, parseResult, parseResult.GetValue(Files), Selection(parseResult),
            parseResult.GetValue(Playlist), shuffle ?? parseResult.GetValue(Shuffle), shuffle is false ? null : parseResult.GetValue(Seed));

    public static ResolvedQueue Resolve(CommandRunner runner, ParseResult parseResult,
        IReadOnlyList<string>? files, LibrarySelection selection, string? playlist, bool shuffle, int? seed)
    {
        var warnings = new List<string>();
        IReadOnlyList<QueueItem> items = playlist is not null
            ? PlaylistItems(runner, parseResult, playlist, warnings)
            : LibraryTracks(runner, parseResult, selection) is { } tracks
                ? [.. tracks.Select(ToItem)]
                : [.. (files ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => new QueueItem(Path.GetFullPath(f)))];
        var used = ApplyShuffle(shuffle, seed, ref items);
        return new ResolvedQueue([.. items], used, warnings);
    }

    /// <summary>The playable entries of a saved playlist. Missing entries are left out (one warning).</summary>
    /// <exception cref="MusicSpinException">NotFound (unknown playlist, or nothing playable in it).</exception>
    public static List<QueueItem> PlaylistItems(CommandRunner runner, ParseResult parseResult, string playlist, List<string> warnings)
    {
        using var library = runner.OpenLibraryForRead(parseResult);
        var (playable, missing) = library.PlaylistQueue(playlist);
        if (playable.Count == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.NotFound, missing == 0
                ? $"Playlist '{playlist.Trim()}' is empty. Add tracks with 'music-spin playlist add'."
                : $"None of the {missing} entries of playlist '{playlist.Trim()}' can be found (see 'music-spin playlist show').");
        }

        if (missing > 0)
        {
            warnings.Add($"{missing} missing entr{(missing == 1 ? "y" : "ies")} of playlist '{playlist.Trim()}' left out (see 'music-spin playlist show').");
        }

        return [.. playable.Select(e => new QueueItem(e.Path, e.Title, e.Artist, e.Album, e.AlbumArtist, e.TrackNumber, e.DurationSeconds, e.TrackId))];
    }

    /// <summary>The same rules as <see cref="Validate"/> for callers without a command line (MCP tools). Null when valid.</summary>
    public static string? ValidateValues(IReadOnlyList<string>? files, LibrarySelection selection, string? playlist, bool shuffle, int? seed)
    {
        var hasFiles = files is { Count: > 0 } && files.Any(f => !string.IsNullOrWhiteSpace(f));
        var kinds = (hasFiles ? 1 : 0) + selection.Kinds + (playlist is not null ? 1 : 0);
        return kinds > 1 ? "Give either files, album/artist, search/title/albumContains/artistContains, or playlist, not several."
            : kinds == 0 ? "Nothing to play: give files, an album, an artist, a search text, title/albumContains/artistContains words or a playlist."
            : seed is not null && !shuffle ? "seed only applies together with shuffle."
            : null;
    }

    public static QueueItem ToItem(TrackRecord t) => new(
        t.Path, t.Title, t.Artist, t.Album, t.EffectiveAlbumArtist, t.TrackNumber, Math.Round(t.DurationSeconds, 3), t.Id);
}
