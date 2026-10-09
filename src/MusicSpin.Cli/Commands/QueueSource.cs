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
        Description = "Library tracks matching this text (same matching as 'musicspin search'), up to 10000.",
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
    {
        var album = parseResult.GetValue(Album);
        var artist = parseResult.GetValue(Artist);
        var search = parseResult.GetValue(Search);
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
                : throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No library tracks match '{search}'. See 'musicspin search'.");
        }

        return album is not null ? library.AlbumQueue(album, artist) : library.ArtistQueue(artist!);
    }

    /// <summary>Applies --shuffle/--seed; returns the seed used (null when not shuffled).</summary>
    public int? ApplyShuffle<T>(ParseResult parseResult, ref IReadOnlyList<T> items)
    {
        if (!parseResult.GetValue(Shuffle))
        {
            return null;
        }

#pragma warning disable CA5394 // not security sensitive
        var seed = parseResult.GetValue(Seed) ?? Random.Shared.Next();
#pragma warning restore CA5394
        items = MusicLibrary.Shuffle(items, seed);
        return seed;
    }

    /// <summary>Queue items for the background player (files resolved to absolute paths: the player has another working directory).</summary>
    public (List<QueueItem> Items, int? Seed) Resolve(CommandRunner runner, ParseResult parseResult)
    {
        IReadOnlyList<QueueItem> items = LibraryTracks(runner, parseResult) is { } tracks
            ? [.. tracks.Select(ToItem)]
            : [.. (parseResult.GetValue(Files) ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => new QueueItem(Path.GetFullPath(f)))];
        var seed = ApplyShuffle(parseResult, ref items);
        return ([.. items], seed);
    }

    public static QueueItem ToItem(TrackRecord t) => new(
        t.Path, t.Title, t.Artist, t.Album, t.EffectiveAlbumArtist, t.TrackNumber, Math.Round(t.DurationSeconds, 3), t.Id);
}
