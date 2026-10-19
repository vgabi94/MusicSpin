using System.CommandLine;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;

namespace MusicSpin.Cli.Commands;

/// <summary><c>music-spin radio</c>: endless random playback of the library (or the part the filters select) in the background player.</summary>
internal static class RadioCommand
{
    public static Command Create(CommandRunner runner)
    {
        var artist = Many("--artist", "Track or album artist (exact name, case/diacritics-insensitive). Repeat for several (any of them). Unknown: exit 12.");
        var album = Many("--album", "Album title (exact) or id. Repeat for several. Unknown: exit 12.");
        var genre = Many("--genre", "Genre (exact name, case/diacritics-insensitive; multi-genre tags split on ; / , |). Repeat for several.");
        var year = new Option<int[]>("--year") { Description = "Release year. Repeat for several.", AllowMultipleArgumentsPerToken = false };
        Options.Validated(year, v => (v ?? []).Where(y => y is < YearRange.MinYear or > YearRange.MaxYear).Select(y => (int?)y).FirstOrDefault() is { } bad
            ? $"--year must be between {YearRange.MinYear} and {YearRange.MaxYear} (got {bad})." : null);
        var years = Many("--years", "Inclusive year range A-B (e.g. 1990-1999). Repeat for several; ORed with --year.");
        var format = new Option<string[]>("--format")
        {
            Description = "Format id(s): flac, alac, dsd, pcm, mp3, ... Repeat or comma-separate.",
            AllowMultipleArgumentsPerToken = true,
        };
        var search = Many("--search", "Search text, same syntax as 'music-spin search' (words, \"phrases\", artist:/album:/title:/genre:/year:, is:favorite). Repeat for several (any).");
        var hires = new Option<bool>("--hires") { Description = "Only hi-res tracks." };
        var favorites = new Option<bool>("--favorites") { Description = "Only favorite tracks (see 'music-spin favorite add'); narrows the other filters." };
        var preferFavorites = new Option<bool>("--prefer-favorites")
        {
            Description = $"Play favorites more often ({RadioPicker.FavoriteWeight}x as likely as other tracks) without limiting the station to them. Saved with the station.",
        };
        const string Ex = " Excluded even when an include filter matches; unknown names are ignored.";
        var xArtist = Many("--exclude-artist", "Leave out this track or album artist (exact name). Repeatable." + Ex);
        var xAlbum = Many("--exclude-album", "Leave out this album (exact title or id). Repeatable." + Ex);
        var xGenre = Many("--exclude-genre", "Leave out this genre (same matching as --genre). Repeatable." + Ex);
        var xYear = new Option<int[]>("--exclude-year") { Description = "Leave out this release year. Repeatable." + Ex, AllowMultipleArgumentsPerToken = false };
        Options.Validated(xYear, v => (v ?? []).Where(y => y is < YearRange.MinYear or > YearRange.MaxYear).Select(y => (int?)y).FirstOrDefault() is { } bad
            ? $"--exclude-year must be between {YearRange.MinYear} and {YearRange.MaxYear} (got {bad})." : null);
        var xYears = Many("--exclude-years", "Leave out an inclusive year range A-B. Repeatable." + Ex);
        var xFormat = new Option<string[]>("--exclude-format")
        {
            Description = "Leave out format id(s). Repeat or comma-separate." + Ex,
            AllowMultipleArgumentsPerToken = true,
        };
        var xSearch = Many("--exclude-search", "Leave out tracks matching this search text (search syntax; is:favorite leaves out your favorites). Repeatable." + Ex);
        var recent = new Option<int?>("--recent")
        {
            Description = $"Start recent station N again (1 = the newest; see 'music-spin radio list'). Not with filters. No such station: exit 12.",
        };
        var station = new Option<string?>("--station") { Description = "Start a saved station (see 'radio save' and 'radio list'). Not with filters. Unknown: exit 12." };
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();

        var command = new Command("radio",
            "Endless random playback of the library in the background player; filters narrow it (OR within a filter, AND across filters). " +
            "No track repeats within the last 100 (or pool size - 1); the same artist/album is not played twice in a row when there is a choice. " +
            "--exclude-* options leave tracks out (any match excludes; exclude wins over include). " +
            "next/prev/pause/stop/status work as usual; 'radio end' ends the station and keeps the queue; 'play', 'queue clear' or 'load' replace it. Nothing matches: exit 12.")
        {
            artist, album, genre, year, years, format, search, hires, favorites, preferFavorites,
            xArtist, xAlbum, xGenre, xYear, xYears, xFormat, xSearch, recent, station, backend, device, exclusive,
        };
        command.Subcommands.Add(ListCommand(runner));
        command.Subcommands.Add(SaveCommand(runner));
        command.Subcommands.Add(DeleteCommand(runner));
        command.Validators.Add(r =>
        {
            foreach (var (name, option) in new[] { ("--years", years), ("--exclude-years", xYears) })
            {
                foreach (var text in Options.Get(r, option) ?? [])
                {
                    if (YearRange.TryParse(text, out var error) is null)
                    {
                        r.AddError($"{name}: {error}");
                    }
                }
            }

            foreach (var (name, option) in new[]
                     {
                         ("--artist", artist), ("--album", album), ("--genre", genre), ("--search", search),
                         ("--exclude-artist", xArtist), ("--exclude-album", xAlbum), ("--exclude-genre", xGenre), ("--exclude-search", xSearch),
                     })
            {
                if ((Options.Get(r, option) ?? []).Any(string.IsNullOrWhiteSpace))
                {
                    r.AddError($"{name} must not be empty.");
                }
            }
        });

        var end = new Command("end",
            "End the radio station and keep playing: the current track plays on (no gap) and the up-next tracks follow as a normal queue; " +
            "the station's remaining picks are dropped. Does nothing when no station is on air. No running player: exit 13.");
        PlayerSupport.Control(runner, end, "radio.end", name: "radio end");
        command.Subcommands.Add(end);

        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var filter = new RadioFilter
            {
                Artists = List(parseResult.GetValue(artist)),
                Albums = List(parseResult.GetValue(album)),
                Genres = List(parseResult.GetValue(genre)),
                Years = Years(parseResult.GetValue(year), parseResult.GetValue(years)),
                Formats = List(parseResult.GetValue(format)?.SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                Searches = List(parseResult.GetValue(search)),
                HiRes = parseResult.GetValue(hires),
                Favorites = parseResult.GetValue(favorites),
                PreferFavorites = parseResult.GetValue(preferFavorites),
                ExcludeArtists = List(parseResult.GetValue(xArtist)),
                ExcludeAlbums = List(parseResult.GetValue(xAlbum)),
                ExcludeGenres = List(parseResult.GetValue(xGenre)),
                ExcludeYears = Years(parseResult.GetValue(xYear), parseResult.GetValue(xYears)),
                ExcludeFormats = List(parseResult.GetValue(xFormat)?.SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                ExcludeSearches = List(parseResult.GetValue(xSearch)),
            };
            filter = Stored(runner, parseResult, filter, parseResult.GetValue(recent), parseResult.GetValue(station), cli: true);
            var (data, started) = await StartAsync(runner, parseResult, filter, parseResult.GetValue(backend)!, parseResult.GetValue(device),
                parseResult.GetValue(exclusive), cancellationToken).ConfigureAwait(false);
            PlayerSupport.WriteStatus(runner, json, "radio", data, new Dictionary<string, object?> { ["playerStarted"] = started });
            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Checks the filters against the library (unknown names and empty pools fail here, exit 12, without starting a player),
    /// then starts the station in the background player (starting it if needed). Shared by the CLI and the MCP tool.
    /// </summary>
    internal static async Task<(System.Text.Json.JsonElement Data, bool Started)> StartAsync(CommandRunner runner, ParseResult parseResult, RadioFilter filter,
        string backend, string? device, bool exclusive, CancellationToken cancellationToken)
    {
        filter.EnsureValid();
        using (var library = runner.OpenLibraryForRead(parseResult))
        {
            if (!library.HasRadioTracks(filter))
            {
                throw new MusicSpinException(MusicSpinErrorCode.NotFound, library.RadioEmptyPoolMessage(filter));
            }
        }

        var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, backend, device, exclusive, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return (await client.SendAsync("radio", new { filter, play = true }, cancellationToken).ConfigureAwait(false), started);
        }
    }

    /// <summary>
    /// The station to start: <paramref name="filter"/>, or the recent / saved one asked for (which may not be combined with
    /// filters, or with each other). Shared by the CLI and the MCP tool.
    /// </summary>
    internal static RadioFilter Stored(CommandRunner runner, ParseResult parseResult, RadioFilter filter, int? recent, string? station, bool cli)
    {
        if (recent is null && station is null)
        {
            return filter;
        }

        var (recentName, stationName) = cli ? ("--recent", "--station") : ("recent", "station");
        if (recent is not null && station is not null)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Give {recentName} or {stationName}, not both.");
        }

        if (!filter.IsEmpty || filter.HasExcludes)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument,
                $"{(recent is not null ? recentName : stationName)} starts a stored station as it was saved: leave out the filters.");
        }

        var store = Stations(runner, parseResult);
        return recent is { } n ? store.GetRecent(n).Filter : store.Get(station!).Filter;
    }

    internal static StationStore Stations(CommandRunner runner, ParseResult parseResult)
        => new(runner.DataDirectory(parseResult), runner.Env.Time);

    /// <summary>What <c>radio list</c> / MCP <c>radio_stations</c> return.</summary>
    internal sealed record StationList(IReadOnlyList<RecentStation> Recent, IReadOnlyList<SavedStation> Saved, string? Warning);

    internal static StationList List(CommandRunner runner, ParseResult parseResult)
    {
        var store = Stations(runner, parseResult);
        var recent = store.Recent();
        var warning = store.LoadWarning; // the same file: one warning is enough
        return new StationList(recent, store.Saved(), warning);
    }

    /// <summary>
    /// Saves the station on air (asked of the running player), or recent station <paramref name="recent"/>, as
    /// <paramref name="name"/>. Shared by the CLI and the MCP tool.
    /// </summary>
    internal static async Task<SavedStation> SaveAsync(CommandRunner runner, ParseResult parseResult, string name, int? recent, bool replace, CancellationToken cancellationToken)
    {
        var store = Stations(runner, parseResult);
        RadioFilter filter;
        if (recent is { } n)
        {
            filter = store.GetRecent(n).Filter;
        }
        else
        {
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
            var status = await client.SendAsync("status", null, cancellationToken).ConfigureAwait(false);
            filter = status.TryGetProperty("radio", out var radio) && radio.ValueKind == System.Text.Json.JsonValueKind.Object
                     && radio.TryGetProperty("filter", out var f) && Core.Remote.ProtocolJson.Deserialize<RadioFilter>(f) is { } onAir
                ? onAir
                : throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    "No radio station is on air. Start one with 'music-spin radio', or save a recent one with --recent N.");
        }

        return store.Save(name, filter, replace);
    }

    private static Command ListCommand(CommandRunner runner)
    {
        var command = new Command("list",
            $"List the recent radio stations (the last {StationStore.RecentLimit} started, newest first, numbered for 'radio --recent N') and the saved ones (by name).");
        runner.SetAction(command, (parseResult, json, _) =>
        {
            var list = List(runner, parseResult);
            if (json)
            {
                Output.JsonOutput.WriteSuccess(runner.Env.Out, "radio list", list);
                return Task.FromResult(ExitCodes.Success);
            }

            if (list.Warning is { } warning)
            {
                runner.Env.Error.WriteLine($"music-spin radio list: warning: {warning}");
            }

            var o = runner.Env.Out;
            o.WriteLine(list.Recent.Count == 0 ? "Recent stations: none yet ('music-spin radio' starts one)." : "Recent stations:");
            for (var i = 0; i < list.Recent.Count; i++)
            {
                o.WriteLine($"  {i + 1}. {list.Recent[i].Description}");
            }

            o.WriteLine(list.Saved.Count == 0 ? "Saved stations: none ('music-spin radio save <name>' saves the one on air)." : "Saved stations:");
            foreach (var saved in list.Saved)
            {
                o.WriteLine($"  {saved.Name}: {saved.Description}");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "radio list");
        return command;
    }

    private static Command SaveCommand(CommandRunner runner)
    {
        var name = new Argument<string>("name") { Description = $"Station name (case-insensitive, at most {StationStore.MaxNameLength} characters)." };
        var recent = new Option<int?>("--recent") { Description = "Save recent station N (1 = the newest) instead of the one on air." };
        var replace = new Option<bool>("--replace") { Description = "Overwrite a saved station with this name (otherwise exit 8)." };
        var command = new Command("save",
            "Save the radio station on air (or a recent one with --recent N) under a name; start it again with 'radio --station <name>'. " +
            "No station on air: exit 8; no player: exit 13.")
        {
            name, recent, replace,
        };
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var saved = await SaveAsync(runner, parseResult, parseResult.GetValue(name)!, parseResult.GetValue(recent), parseResult.GetValue(replace), cancellationToken)
                .ConfigureAwait(false);
            if (json)
            {
                Output.JsonOutput.WriteSuccess(runner.Env.Out, "radio save", saved);
            }
            else
            {
                runner.Env.Out.WriteLine($"Saved radio station '{saved.Name}': {saved.Description}.");
            }

            return ExitCodes.Success;
        }, "radio save");
        return command;
    }

    private static Command DeleteCommand(CommandRunner runner)
    {
        var name = new Argument<string>("name") { Description = "Saved station name (case-insensitive)." };
        var command = new Command("delete", "Delete a saved radio station. Unknown name: exit 12.") { name };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            var deleted = Stations(runner, parseResult).Delete(parseResult.GetValue(name)!);
            if (json)
            {
                Output.JsonOutput.WriteSuccess(runner.Env.Out, "radio delete", deleted);
            }
            else
            {
                runner.Env.Out.WriteLine($"Deleted radio station '{deleted.Name}'.");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "radio delete");
        return command;
    }

    /// <summary>--year values and --years ranges as one list of ranges (null when none).</summary>
    internal static IReadOnlyList<YearRange>? Years(IEnumerable<int>? single, IEnumerable<string>? ranges)
    {
        var all = (single ?? []).Select(y => new YearRange(y, y))
            .Concat((ranges ?? []).Select(r => YearRange.TryParse(r, out var error) ?? throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error!)))
            .ToList();
        return all.Count > 0 ? all : null;
    }

    internal static IReadOnlyList<string>? List(IEnumerable<string>? values)
        => values?.ToList() is { Count: > 0 } list ? list : null;

    private static Option<string[]> Many(string name, string description)
        => new(name) { Description = description, AllowMultipleArgumentsPerToken = false };
}
