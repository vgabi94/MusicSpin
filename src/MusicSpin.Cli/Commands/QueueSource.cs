using System.CommandLine;
using System.CommandLine.Parsing;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Commands;

/// <summary>"What to play" options shared by `play` and `queue add`: files, --album, --artist, --search, --shuffle, --seed.</summary>
internal sealed class QueueSource
{
    public Argument<string[]> Files { get; } = new("files")
    {
        Description = "Audio file(s) to play (or use --album / --artist / --search to play from the library).",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public Option<string?> Album { get; } = new("--album")
    {
        Description = "An album from the library (title, case-insensitive, or album id). Combine with --artist to disambiguate.",
    };

    public Option<string?> Artist { get; } = new("--artist")
    {
        Description = "All library tracks by an artist (albums by year), or narrow --album.",
    };

    public Option<string?> Search { get; } = new("--search")
    {
        Description = "Library tracks matching this text (same matching as 'music-spin search'), up to 10000.",
    };

    public Option<bool> Shuffle { get; } = new("--shuffle") { Description = "Shuffle the queue." };

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
        command.Add(Shuffle);
        command.Add(Seed);
    }

    /// <summary>Exactly one kind of source; --seed needs --shuffle.</summary>
    public void Validate(CommandResult r, bool required = true)
    {
        var hasFiles = Options.Get(r, Files) is { Length: > 0 } files && files.Any(f => !string.IsNullOrWhiteSpace(f));
        var hasLibrary = Options.Get(r, Album) is not null || Options.Get(r, Artist) is not null;
        var hasSearch = Options.Get(r, Search) is not null;
        var kinds = (hasFiles ? 1 : 0) + (hasLibrary ? 1 : 0) + (hasSearch ? 1 : 0);
        if (kinds > 1)
        {
            r.AddError("Give either file(s), --album/--artist, or --search, not several.");
        }
        else if (kinds == 0 && required)
        {
            r.AddError("Nothing to play: give a file, --album <title>, --artist <name> or --search <text>.");
        }
        else if (Options.Get(r, Seed) is not null && !Options.Get(r, Shuffle))
        {
            r.AddError("--seed only applies together with --shuffle.");
        }
    }

    public bool HasFiles(ParseResult parseResult) => parseResult.GetValue(Files) is { Length: > 0 };

    /// <summary>Library tracks for --album/--artist/--search (null for files). Releases the library before returning.</summary>
    public IReadOnlyList<TrackRecord>? LibraryTracks(CommandRunner runner, ParseResult parseResult)
        => LibraryTracks(runner, parseResult, parseResult.GetValue(Album), parseResult.GetValue(Artist), parseResult.GetValue(Search));

    public static IReadOnlyList<TrackRecord>? LibraryTracks(CommandRunner runner, ParseResult parseResult, string? album, string? artist, string? search)
    {
        if (album is null && artist is null && search is null)
        {
            return null;
        }

        using var library = runner.OpenLibraryForRead(parseResult);
        if (search is not null)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "--search needs some text.");
            }

            var tracks = library.Search(search, new Paging(0, Paging.MaxLimit)).Tracks.Items;
            return tracks.Count > 0
                ? tracks
                : throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No library tracks match '{search}'. See 'music-spin search'.");
        }

        return album is not null ? library.AlbumQueue(album, artist) : library.ArtistQueue(artist!);
    }

    /// <summary>Applies --shuffle/--seed; returns the seed used (null when not shuffled).</summary>
    public int? ApplyShuffle<T>(ParseResult parseResult, ref IReadOnlyList<T> items)
        => ApplyShuffle(parseResult.GetValue(Shuffle), parseResult.GetValue(Seed), ref items);

    public static int? ApplyShuffle<T>(bool shuffle, int? seed, ref IReadOnlyList<T> items)
    {
        if (!shuffle)
        {
            return null;
        }

#pragma warning disable CA5394 // not security sensitive
        var used = seed ?? Random.Shared.Next();
#pragma warning restore CA5394
        items = MusicLibrary.Shuffle(items, used);
        return used;
    }

    /// <summary>Queue items for the background player (files resolved to absolute paths: the player has another working directory).</summary>
    public (List<QueueItem> Items, int? Seed) Resolve(CommandRunner runner, ParseResult parseResult)
        => Resolve(runner, parseResult, parseResult.GetValue(Files), parseResult.GetValue(Album), parseResult.GetValue(Artist),
            parseResult.GetValue(Search), parseResult.GetValue(Shuffle), parseResult.GetValue(Seed));

    public static (List<QueueItem> Items, int? Seed) Resolve(CommandRunner runner, ParseResult parseResult,
        IReadOnlyList<string>? files, string? album, string? artist, string? search, bool shuffle, int? seed)
    {
        IReadOnlyList<QueueItem> items = LibraryTracks(runner, parseResult, album, artist, search) is { } tracks
            ? [.. tracks.Select(ToItem)]
            : [.. (files ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => new QueueItem(Path.GetFullPath(f)))];
        var used = ApplyShuffle(shuffle, seed, ref items);
        return ([.. items], used);
    }

    /// <summary>The same rules as <see cref="Validate"/> for callers without a command line (MCP tools). Null when valid.</summary>
    public static string? ValidateValues(IReadOnlyList<string>? files, string? album, string? artist, string? search, bool shuffle, int? seed)
    {
        var hasFiles = files is { Count: > 0 } && files.Any(f => !string.IsNullOrWhiteSpace(f));
        var kinds = (hasFiles ? 1 : 0) + (album is not null || artist is not null ? 1 : 0) + (search is not null ? 1 : 0);
        return kinds > 1 ? "Give either files, album/artist, or search, not several."
            : kinds == 0 ? "Nothing to play: give files, an album, an artist or a search text."
            : seed is not null && !shuffle ? "seed only applies together with shuffle."
            : null;
    }

    public static QueueItem ToItem(TrackRecord t) => new(
        t.Path, t.Title, t.Artist, t.Album, t.EffectiveAlbumArtist, t.TrackNumber, Math.Round(t.DurationSeconds, 3), t.Id);
}
