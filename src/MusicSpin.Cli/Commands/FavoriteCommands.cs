using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Commands;

/// <summary>The result of <c>favorite add/remove</c>: how many tracks changed, and the favorites afterwards.</summary>
internal sealed record FavoriteChange(int Requested, int Changed, int Unchanged, int Favorites, int Missing, IReadOnlyList<string>? Warnings = null);

/// <summary>What <c>favorite add/remove</c> works on: exactly one of these (see <see cref="FavoriteCommands.Validate"/>).</summary>
internal sealed record FavoriteTargets(IReadOnlyList<string>? Files, LibrarySelection Selection, IReadOnlyList<string>? TrackIds, bool Current, bool Missing = false);

/// <summary><c>music-spin favorite add/remove</c> and <c>music-spin favorites</c>: favorite library tracks.</summary>
internal static class FavoriteCommands
{
    public static IEnumerable<Command> Create(CommandRunner runner)
    {
        var favorite = new Command("favorite", "Mark tracks as favorites or unmark them: add, remove. 'music-spin favorites' lists them.");
        favorite.Add(Edit(runner, add: true));
        favorite.Add(Edit(runner, add: false));
        yield return favorite;
        yield return List(runner);
    }

    private static Command Edit(CommandRunner runner, bool add)
    {
        var verb = add ? "add" : "remove";
        var files = new Argument<string[]>("files")
        {
            Description = add ? "Library tracks by path." : "Library tracks by path, or the path of a favorite that is no longer in the library ('favorites --missing').",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var album = new Option<string?>("--album") { Description = "A library album (title or id); with --artist to disambiguate." };
        var artist = new Option<string?>("--artist") { Description = "All library tracks by an artist, or narrow --album." };
        var search = new Option<string?>("--search") { Description = "Library tracks matching this search (syntax as 'music-spin search')." };
        var title = new Option<string?>("--title") { Description = "Library tracks whose title contains these words." };
        var albumContains = new Option<string?>("--album-contains") { Description = "Library tracks whose album title contains these words." };
        var artistContains = new Option<string?>("--artist-contains") { Description = "Library tracks whose artist contains these words." };
        var trackIds = new Option<string[]>("--track-id")
        {
            Description = "Library track id(s): GUIDs from 'tracks --json' / 'search --json'. Repeat or comma-separate. Unknown: exit 12; not a GUID: exit 2.",
            AllowMultipleArgumentsPerToken = true,
        };
        var current = new Option<bool>("--current") { Description = "The track the background player is on now. No player: exit 13; no current track: exit 12." };
        var command = new Command(verb, add
            ? "Mark library tracks as favorites. Tracks that already are stay as they are. Files outside the library: exit 12."
            : "Unmark favorite tracks. Tracks that aren't favorites are left alone.")
        {
            files, album, artist, search, title, albumContains, artistContains, trackIds, current,
        };
        var missing = new Option<bool>("--missing") { Description = "Remove every favorite that is no longer in the library ('favorites --missing')." };
        if (!add)
        {
            command.Add(missing);
        }

        FavoriteTargets Targets(CommandResult r) => new(Options.Get(r, files), Selection(Options.Get(r, album), Options.Get(r, artist), Options.Get(r, search),
            Options.Get(r, title), Options.Get(r, albumContains), Options.Get(r, artistContains)), Options.Get(r, trackIds), Options.Get(r, current), !add && Options.Get(r, missing));
        command.Validators.Add(r =>
        {
            if (Validate(Targets(r), add, cli: true) is { } error)
            {
                r.AddError(error);
            }
        });
        runner.SetAction(command, async (parseResult, json, cancellationToken) =>
        {
            var targets = new FavoriteTargets(parseResult.GetValue(files),
                Selection(parseResult.GetValue(album), parseResult.GetValue(artist), parseResult.GetValue(search), parseResult.GetValue(title),
                    parseResult.GetValue(albumContains), parseResult.GetValue(artistContains)),
                SplitIds(parseResult.GetValue(trackIds)), parseResult.GetValue(current), !add && parseResult.GetValue(missing));
            var change = await EditAsync(runner, parseResult, add, targets, cancellationToken).ConfigureAwait(false);
            WriteChange(runner, json, $"favorite {verb}", add, change);
            return ExitCodes.Success;
        }, $"favorite {verb}");
        return command;
    }

    private static LibrarySelection Selection(string? album, string? artist, string? search, string? title, string? albumContains, string? artistContains)
        => new(album, artist, search, title, albumContains, artistContains);

    public static List<string> SplitIds(IEnumerable<string>? ids)
        => [.. (ids ?? []).SelectMany(i => i.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    /// <summary>Exactly one kind of target. Null when valid. <paramref name="cli"/> picks option names over MCP parameter names.</summary>
    public static string? Validate(FavoriteTargets targets, bool add, bool cli)
    {
        var kinds = (targets.Files is { Count: > 0 } f && f.Any(p => !string.IsNullOrWhiteSpace(p)) ? 1 : 0) + targets.Selection.Kinds
            + (targets.TrackIds is { Count: > 0 } ? 1 : 0) + (targets.Current ? 1 : 0) + (targets.Missing ? 1 : 0);
        var names = cli
            ? $"file(s), --album/--artist, --search/--title/--album-contains/--artist-contains, --track-id, --current{(add ? string.Empty : " or --missing")}"
            : $"files, album/artist, search/title/albumContains/artistContains, trackIds, current{(add ? string.Empty : " or missing")}";
        return kinds > 1 ? $"Give one of {names}, not several."
            : kinds == 0 ? $"Nothing to {(add ? "add" : "remove")}: give {names}."
            : null;
    }

    /// <summary>Marks or unmarks the targets (CLI and MCP). Everything is checked before anything changes.</summary>
    /// <exception cref="MusicSpinException">NotFound (not library tracks, unknown ids or names, nothing playing); PlayerNotRunning (--current); InvalidArgument.</exception>
    public static async Task<FavoriteChange> EditAsync(CommandRunner runner, ParseResult parseResult, bool add, FavoriteTargets targets, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var ids = new List<string>();
        var missingPaths = new List<string>();
        if (targets.Current)
        {
            ids.Add(await CurrentTrackIdAsync(runner, parseResult, cancellationToken).ConfigureAwait(false));
        }
        else if (targets.TrackIds is { Count: > 0 } trackIds)
        {
            ids.AddRange(trackIds);
        }
        else if (QueueSource.LibraryTracks(runner, parseResult, targets.Selection, warnings) is { } tracks)
        {
            ids.AddRange(tracks.Select(t => t.Id));
        }
        else if (!targets.Missing)
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var outside = new List<string>();
            var missingFavorites = add ? [] : library.FavoritesSummary().MissingPaths.Select(LibraryKeys.PathKey).ToHashSet(StringComparer.Ordinal);
            foreach (var (path, id) in library.TrackIdsAt(targets.Files ?? []))
            {
                if (id is not null)
                {
                    ids.Add(id);
                }
                else if (missingFavorites.Contains(LibraryKeys.PathKey(path)))
                {
                    missingPaths.Add(path);
                }
                else
                {
                    outside.Add(path);
                }
            }

            if (outside.Count > 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.NotFound,
                    $"Not {(add ? "in the library" : "a library track or a missing favorite")}: {string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? $" and {outside.Count - 5} more" : string.Empty)}. " +
                    (add ? "Favorites are library tracks: add the folder with 'music-spin library add' and scan." : "See 'music-spin favorites' and 'music-spin favorites --missing'.") +
                    " Nothing was changed.");
            }
        }

        using (var library = runner.OpenLibraryForWrite(parseResult))
        {
            var requested = ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() + missingPaths.Count;
            var changed = add ? library.AddFavorites(ids) : library.RemoveFavorites(ids);
            if (targets.Missing)
            {
                requested = changed = library.RemoveMissingFavorites();
            }
            else if (missingPaths.Count > 0)
            {
                changed += library.RemoveMissingFavorites(missingPaths);
            }

            var summary = library.FavoritesSummary();
            return new FavoriteChange(requested, changed, requested - changed, summary.Count, summary.Missing, warnings.Count > 0 ? warnings : null);
        }
    }

    /// <summary>The library track the background player is on now.</summary>
    private static async Task<string> CurrentTrackIdAsync(CommandRunner runner, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            var status = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
            var path = status.Item?.Path ?? status.Track?.Path
                ?? throw new MusicSpinException(MusicSpinErrorCode.NotFound, "Nothing is playing: the player has no current track. Start one with 'music-spin play'.");
            using var library = runner.OpenLibraryForRead(parseResult);
            return library.TrackIdsAt([path])[0].TrackId
                ?? throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"The current track is not in the library: {path}. Favorites are library tracks.");
        }
    }

    private static void WriteChange(CommandRunner runner, bool json, string command, bool add, FavoriteChange change)
    {
        if (json)
        {
            JsonOutput.WriteSuccess(runner.Env.Out, command, change);
            return;
        }

        foreach (var warning in change.Warnings ?? [])
        {
            runner.Env.Error.WriteLine($"music-spin {command}: warning: {warning}");
        }

        var unchanged = change.Unchanged == 0 ? string.Empty
            : add ? $" ({change.Unchanged} already {(change.Unchanged == 1 ? "was" : "were")})"
            : $" ({change.Unchanged} {(change.Unchanged == 1 ? "wasn't one" : "weren't")})";
        var missing = change.Missing > 0 ? $", {change.Missing} no longer in the library ('favorites --missing')" : string.Empty;
        runner.Env.Out.WriteLine($"{(add ? "Added" : "Removed")} {Plural(change.Changed, "favorite")}{unchanged}. Favorites: {change.Favorites}{missing}.");
    }

    private static Command List(CommandRunner runner)
    {
        var (limit, offset, desc) = (Options.Limit(), Options.Offset(), Options.Descending());
        var sort = Options.Sort("favorited", BrowseCommands.TrackSorts);
        var missing = new Option<bool>("--missing") { Description = "List the paths of favorites that are no longer in the library instead." };
        var command = new Command("favorites",
            "List the favorite tracks, most recently favorited first (--sort favorited is newest first; --desc reverses). " +
            "The footer says how many favorites are no longer in the library; --missing lists them.")
        {
            sort, desc, limit, offset, missing,
        };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var summary = library.FavoritesSummary();
            var o = runner.Env.Out;
            if (parseResult.GetValue(missing))
            {
                if (json)
                {
                    JsonOutput.WriteSuccess(o, "favorites", new { count = summary.Missing, items = summary.MissingPaths });
                }
                else
                {
                    foreach (var path in summary.MissingPaths)
                    {
                        o.WriteLine(path);
                    }

                    o.WriteLine(summary.Missing == 0 ? "No missing favorites." : $"({Plural(summary.Missing, "favorite")} no longer in the library; 'music-spin favorite remove --missing' removes them)");
                }

                return Task.FromResult(ExitCodes.Success);
            }

            var page = PageDto.From(library.Tracks(BrowseCommands.WithSort(new TrackQuery
            {
                Favorite = true,
                Paging = new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)),
            }, parseResult.GetValue(sort)!, parseResult.GetValue(desc))), LibraryTrackDto.From);
            if (json)
            {
                var node = JsonSerializer.SerializeToNode(page, JsonOutput.Options)!;
                node["missing"] = summary.Missing;
                JsonOutput.WriteSuccess(o, "favorites", node);
                return Task.FromResult(ExitCodes.Success);
            }

            if (page.Total == 0)
            {
                o.WriteLine(library.NoFavoritesMessage());
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (var track in page.Items)
            {
                o.WriteLine(BrowseCommands.TrackLine(track));
            }

            var footer = page.Count == 0 ? $"(offset {page.Offset} is past the end; {page.Total} total)" : $"({page.Offset + 1}-{page.Offset + page.Count} of {page.Total})";
            o.WriteLine(summary.Missing == 0 ? footer : $"{footer} · {Plural(summary.Missing, "favorite")} no longer in the library: see 'music-spin favorites --missing'");
            return Task.FromResult(ExitCodes.Success);
        });
        return command;
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
