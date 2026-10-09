using System.CommandLine;
using System.Globalization;
using MusicSpin.Cli.Output;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Commands;

/// <summary>Library queries: artists, albums, tracks, search, filter. All paged (--limit/--offset) with --sort/--desc.</summary>
internal static class BrowseCommands
{
    private static readonly string[] TrackSorts = ["default", "relevance", "title", "artist", "album", "year", "duration", "added", "samplerate", "bitdepth", "size", "path"];

    /// <summary>Per-field "contains" filters and --search for tracks/filter.</summary>
    private sealed class TrackTextOptions
    {
        public Option<string?> Search { get; } = new("--search") { Description = "Search syntax as 'music-spin search': words (all must match), \"phrases\", artist:/album:/title:/genre:/year:." };
        public Option<string?> Title { get; } = new("--title") { Description = "Title contains these words (case/diacritic-insensitive)." };
        public Option<string?> AlbumContains { get; } = new("--album-contains") { Description = "Album title contains these words." };
        public Option<string?> ArtistContains { get; } = new("--artist-contains") { Description = "Track artist or album artist contains these words." };

        public IEnumerable<Option> All => [Search, Title, AlbumContains, ArtistContains];

        public TrackQuery Apply(TrackQuery query, ParseResult parseResult) => query with
        {
            Text = parseResult.GetValue(Search),
            Title = parseResult.GetValue(Title),
            AlbumContains = parseResult.GetValue(AlbumContains),
            ArtistContains = parseResult.GetValue(ArtistContains),
        };
    }

    public static IEnumerable<Command> Create(CommandRunner runner)
    {
        yield return Artists(runner);
        yield return Albums(runner);
        yield return Tracks(runner);
        yield return Search(runner);
        yield return Filter(runner);
    }

    private static Command Artists(CommandRunner runner)
    {
        var (limit, offset, desc) = (Options.Limit(), Options.Offset(), Options.Descending());
        var sort = Options.Sort("name", "name", "tracks", "albums");
        var name = new Option<string?>("--name") { Description = "Only artists whose name contains these words (case/diacritic-insensitive, every word must match)." };
        var command = new Command("artists", "List artists (track artists and album artists).") { name, sort, desc, limit, offset };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var page = library.Artists(new ArtistQuery
            {
                Name = parseResult.GetValue(name),
                Sort = Enum.Parse<ArtistSort>(parseResult.GetValue(sort)!, ignoreCase: true),
                Descending = parseResult.GetValue(desc),
                Paging = new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)),
            });
            return Task.FromResult(Write(runner, json, "artists", PageDto.From(page, ArtistDto.From),
                a => $"{a.Name}  ({a.AlbumCount} album(s), {a.TrackCount} track(s))"));
        });
        return command;
    }

    private static Command Albums(CommandRunner runner)
    {
        var (limit, offset, desc) = (Options.Limit(), Options.Offset(), Options.Descending());
        var sort = Options.Sort("artist", "artist", "title", "year", "tracks", "duration");
        var artist = new Option<string?>("--artist") { Description = "Only albums by this artist (exact name: album artist or any track artist, case-insensitive). Unknown artist: exit 12." };
        var title = new Option<string?>("--title") { Description = "Only albums whose title contains these words (case/diacritic-insensitive)." };
        var artistContains = new Option<string?>("--artist-contains") { Description = "Only albums whose album artist or a track artist contains these words." };
        var hires = new Option<bool>("--hires") { Description = "Only albums with hi-res tracks." };
        var command = new Command("albums", "List albums (grouped by album artist + title; compilations under 'Various Artists').")
        {
            artist, title, artistContains, hires, sort, desc, limit, offset,
        };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var page = library.Albums(new AlbumQuery
            {
                Artist = parseResult.GetValue(artist),
                Title = parseResult.GetValue(title),
                ArtistContains = parseResult.GetValue(artistContains),
                HiRes = parseResult.GetValue(hires) ? true : null,
                Sort = Enum.Parse<AlbumSort>(parseResult.GetValue(sort)!, ignoreCase: true),
                Descending = parseResult.GetValue(desc),
                Paging = new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)),
            });
            return Task.FromResult(Write(runner, json, "albums", PageDto.From(page, AlbumDto.From),
                a => $"{a.AlbumArtist} - {a.Title}{(a.Year is { } y ? $" ({y})" : string.Empty)}  {a.TrackCount} track(s), {string.Join("/", a.Formats)}{(a.HiRes ? ", hi-res" : string.Empty)}  [{a.Id}]"));
        });
        return command;
    }

    private static Command Tracks(CommandRunner runner)
    {
        var (limit, offset, desc) = (Options.Limit(), Options.Offset(), Options.Descending());
        var sort = Options.Sort("default", TrackSorts);
        var artist = new Option<string?>("--artist") { Description = "Track artist or album artist (exact name, case-insensitive). Unknown: exit 12." };
        var album = new Option<string?>("--album") { Description = "Album title (exact, case-insensitive) or album id. Unknown: exit 12." };
        var text = new TrackTextOptions();
        var command = new Command("tracks", "List tracks (default order: album artist, album, disc, track).") { album, artist, sort, desc, limit, offset };
        foreach (var option in text.All)
        {
            command.Add(option);
        }

        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var page = library.Tracks(text.Apply(new TrackQuery(), parseResult) with
            {
                Artist = parseResult.GetValue(artist),
                Album = parseResult.GetValue(album),
                Sort = ParseTrackSort(parseResult.GetValue(sort)!),
                Descending = parseResult.GetValue(desc),
                Paging = new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)),
            });
            return Task.FromResult(Write(runner, json, "tracks", PageDto.From(page, LibraryTrackDto.From), TrackLine));
        });
        return command;
    }

    private static Command Search(CommandRunner runner)
    {
        var (limit, offset) = (Options.Limit(), Options.Offset());
        var text = new Argument<string>("text")
        {
            Description = "Words to find in titles, artists, albums, genres and file names (case/diacritic-insensitive; every word must match). " +
                          "\"Quoted phrases\" match as a whole; artist:, album:, title:, genre: restrict a word or \"phrase\" to one field; year:1959 or year:1950-1959.",
        };
        text.Validators.Add(r =>
        {
            if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>()))
            {
                r.AddError("Search text must not be empty.");
            }
        });
        var command = new Command("search", "Search artists, albums and tracks, best matches first (exact > prefix > word start > anywhere). Tracks are paged with --limit/--offset.") { text, limit, offset };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var query = parseResult.GetValue(text)!;
            var result = library.Search(query, new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)));
            var dto = new SearchDto(query, result.Artists.Select(ArtistDto.From).ToList(), result.Albums.Select(AlbumDto.From).ToList(),
                PageDto.From(result.Tracks, LibraryTrackDto.From));
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "search", dto);
                return Task.FromResult(ExitCodes.Success);
            }

            var o = runner.Env.Out;
            o.WriteLine($"Artists ({dto.Artists.Count}):");
            foreach (var a in dto.Artists)
            {
                o.WriteLine($"  {a.Name}");
            }

            o.WriteLine($"Albums ({dto.Albums.Count}):");
            foreach (var a in dto.Albums)
            {
                o.WriteLine($"  {a.AlbumArtist} - {a.Title}");
            }

            o.WriteLine($"Tracks ({dto.Tracks.Total}, showing {dto.Tracks.Offset + 1}-{dto.Tracks.Offset + dto.Tracks.Count}):");
            foreach (var t in dto.Tracks.Items)
            {
                o.WriteLine($"  {TrackLine(t)}");
            }

            return Task.FromResult(ExitCodes.Success);
        });
        return command;
    }

    private static Command Filter(CommandRunner runner)
    {
        var (limit, offset, desc) = (Options.Limit(), Options.Offset(), Options.Descending());
        var sort = Options.Sort("default", TrackSorts);
        var minBits = Options.Validated(new Option<int?>("--min-bit-depth") { Description = "Minimum bits per sample (e.g. 24)." },
            v => v is < 1 or > 64 ? "--min-bit-depth must be between 1 and 64." : null);
        var minRate = Options.Validated(new Option<int?>("--min-sample-rate") { Description = "Minimum sample rate in Hz (e.g. 96000)." },
            v => v is < 1 or > 50_000_000 ? "--min-sample-rate must be between 1 and 50000000 Hz." : null);
        var format = new Option<string[]>("--format")
        {
            Description = "Format id(s): flac, alac, dsd, pcm, wavpack, ape, mp3, aac, opus, vorbis, ... Repeat or comma-separate.",
            AllowMultipleArgumentsPerToken = true,
        };
        var yearFrom = Options.Validated(new Option<int?>("--year-from") { Description = "First year (inclusive)." },
            v => v is < 0 or > 9999 ? "--year-from must be a year between 0 and 9999." : null);
        var yearTo = Options.Validated(new Option<int?>("--year-to") { Description = "Last year (inclusive)." },
            v => v is < 0 or > 9999 ? "--year-to must be a year between 0 and 9999." : null);
        var hires = new Option<bool>("--hires") { Description = "Only hi-res tracks (lossless > 16-bit or > 48 kHz, or DSD)." };
        var artist = new Option<string?>("--artist") { Description = "Track artist or album artist (exact name). Unknown: exit 12." };
        var album = new Option<string?>("--album") { Description = "Album title (exact) or id. Unknown: exit 12." };
        var text = new TrackTextOptions();
        var command = new Command("filter", "Filter tracks by audio quality, format, year and text (all criteria must match).")
        {
            minBits, minRate, format, yearFrom, yearTo, hires, artist, album, sort, desc, limit, offset,
        };
        foreach (var option in text.All)
        {
            command.Add(option);
        }

        command.Validators.Add(r =>
        {
            if (Options.Get(r, yearFrom) is { } from && Options.Get(r, yearTo) is { } to && from > to)
            {
                r.AddError("--year-from must not be after --year-to.");
            }
        });
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var formats = (parseResult.GetValue(format) ?? [])
                .SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
            var page = library.Tracks(text.Apply(new TrackQuery(), parseResult) with
            {
                MinBitDepth = parseResult.GetValue(minBits),
                MinSampleRate = parseResult.GetValue(minRate),
                Formats = formats.Count > 0 ? formats : null,
                YearFrom = parseResult.GetValue(yearFrom),
                YearTo = parseResult.GetValue(yearTo),
                HiRes = parseResult.GetValue(hires) ? true : null,
                Artist = parseResult.GetValue(artist),
                Album = parseResult.GetValue(album),
                Sort = ParseTrackSort(parseResult.GetValue(sort)!),
                Descending = parseResult.GetValue(desc),
                Paging = new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)),
            });
            return Task.FromResult(Write(runner, json, "filter", PageDto.From(page, LibraryTrackDto.From), TrackLine));
        });
        return command;
    }

    private static TrackSort ParseTrackSort(string value) => value switch
    {
        "samplerate" => TrackSort.SampleRate,
        "bitdepth" => TrackSort.BitDepth,
        _ => Enum.Parse<TrackSort>(value, ignoreCase: true),
    };

    private static string TrackLine(LibraryTrackDto t)
    {
        var quality = t.Format == "dsd"
            ? $"DSD {t.SampleRate / 1_000_000.0:0.###} MHz"
            : $"{(t.BitsPerSample is { } b ? $"{b}-bit/" : string.Empty)}{(t.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} kHz";
        var number = t.TrackNumber is { } n ? $"{(t.DiscNumber is > 1 ? $"{t.DiscNumber}-" : string.Empty)}{n:00}. " : string.Empty;
        return $"{t.AlbumArtist} - {t.Album ?? "?"} - {number}{t.Title ?? Path.GetFileName(t.Path)}  [{t.Format} {quality}{(t.HiRes ? " hi-res" : string.Empty)}]";
    }

    private static int Write<T>(CommandRunner runner, bool json, string command, PageDto<T> page, Func<T, string> line)
    {
        var o = runner.Env.Out;
        if (json)
        {
            JsonOutput.WriteSuccess(o, command, page);
            return ExitCodes.Success;
        }

        if (page.Total == 0)
        {
            o.WriteLine("Nothing found. (Empty library? Run 'music-spin library add <folder>' and 'music-spin library scan'.)");
            return ExitCodes.Success;
        }

        foreach (var item in page.Items)
        {
            o.WriteLine(line(item));
        }

        o.WriteLine(page.Count == 0
            ? $"(offset {page.Offset} is past the end; {page.Total} total)"
            : $"({page.Offset + 1}-{page.Offset + page.Count} of {page.Total})");
        return ExitCodes.Success;
    }
}
