using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.Cli.Commands;

/// <summary>
/// <c>music-spin playlist ...</c>: saved playlists in the library database, M3U/M3U8/PLS import and export.
/// Positions are 1-based. Names are unique case-insensitively.
/// </summary>
internal static class PlaylistCommands
{
    public static Command Create(CommandRunner runner)
    {
        var playlist = new Command("playlist",
            "Saved playlists: list, show, create, rename, delete, add, remove, move, clear, import (M3U/M3U8/PLS), export. " +
            "Positions are 1-based. Without a subcommand: list.");
        var list = List(runner, "list");
        playlist.Add(list);
        playlist.Add(Show(runner));
        playlist.Add(CreatePlaylist(runner));
        playlist.Add(Rename(runner));
        playlist.Add(Delete(runner));
        playlist.Add(Add(runner));
        playlist.Add(Remove(runner));
        playlist.Add(Move(runner));
        playlist.Add(Clear(runner));
        playlist.Add(Import(runner));
        playlist.Add(Export(runner));
        runner.SetAction(playlist, (parseResult, json, _) => Task.FromResult(WriteList(runner, parseResult, json, "playlist list")), "playlist list");
        return playlist;
    }

    public static void ValidateName(CommandResult r, Argument<string> name)
    {
        try
        {
            MusicLibrary.ValidatePlaylistName(Options.Get(r, name));
        }
        catch (MusicSpinException ex)
        {
            r.AddError(ex.Message);
        }
    }

    private static Argument<string> NameArgument(string description = "Playlist name (case-insensitive).") => new("name") { Description = description };

    private static Command List(CommandRunner runner, string name)
    {
        var command = new Command(name, "List playlists: name, entries, missing entries, total duration.");
        runner.SetAction(command, (parseResult, json, _) => Task.FromResult(WriteList(runner, parseResult, json, "playlist list")), "playlist list");
        return command;
    }

    private static int WriteList(CommandRunner runner, ParseResult parseResult, bool json, string commandName)
    {
        using var library = runner.OpenLibraryForRead(parseResult);
        var playlists = library.Playlists();
        if (json)
        {
            JsonOutput.WriteSuccess(runner.Env.Out, commandName, new { total = playlists.Count, items = playlists });
            return ExitCodes.Success;
        }

        if (playlists.Count == 0)
        {
            runner.Env.Out.WriteLine("No playlists. Create one with 'music-spin playlist create <name>', 'playlist import <file>' or 'queue save <name>'.");
        }

        foreach (var p in playlists)
        {
            runner.Env.Out.WriteLine(SummaryLine(p));
        }

        return ExitCodes.Success;
    }

    private static Command Show(CommandRunner runner)
    {
        var name = NameArgument();
        var (limit, offset) = (Options.Limit(), Options.Offset());
        var command = new Command("show", "Show a playlist's entries (position, status library|file|missing, path, tags).") { name, limit, offset };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForRead(parseResult);
            var details = library.Playlist(parseResult.GetValue(name)!, new Paging(parseResult.GetValue(offset), parseResult.GetValue(limit)));
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "playlist show", new
                {
                    playlist = details.Playlist,
                    entries = new PageDto<PlaylistEntry>(details.Entries.Total, details.Entries.Offset, details.Entries.Limit, details.Entries.Items.Count, details.Entries.Items),
                });
                return Task.FromResult(ExitCodes.Success);
            }

            var o = runner.Env.Out;
            o.WriteLine(SummaryLine(details.Playlist));
            foreach (var e in details.Entries.Items)
            {
                var title = e.Title ?? Path.GetFileName(e.Path);
                var flag = e.Status switch
                {
                    PlaylistEntryStatus.Missing => "  [missing]",
                    PlaylistEntryStatus.File => "  [not in library]",
                    _ => string.Empty,
                };
                o.WriteLine($"{e.Position,5}. {(e.Artist is { } a ? $"{a} - {title}" : title)}{(e.DurationSeconds is { } d ? $"  ({Human.Time(TimeSpan.FromSeconds(d))})" : string.Empty)}{flag}");
            }

            if (details.Entries.Total > details.Entries.Items.Count)
            {
                o.WriteLine(details.Entries.Items.Count == 0
                    ? $"(offset {details.Entries.Offset} is past the end; {details.Entries.Total} total)"
                    : $"({details.Entries.Offset + 1}-{details.Entries.Offset + details.Entries.Items.Count} of {details.Entries.Total})");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "playlist show");
        return command;
    }

    private static Command CreatePlaylist(CommandRunner runner)
    {
        var name = NameArgument("Name of the new playlist (1-200 characters; unique, case-insensitive).");
        var command = new Command("create", "Create an empty playlist. Exit 8 if the name is taken.") { name };
        command.Validators.Add(r => ValidateName(r, name));
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            return Task.FromResult(WriteChange(runner, json, "playlist create", library.CreatePlaylist(parseResult.GetValue(name)!), "Created"));
        }, "playlist create");
        return command;
    }

    private static Command Rename(CommandRunner runner)
    {
        var name = NameArgument();
        var newName = new Argument<string>("new-name") { Description = "New name (unique, case-insensitive; changing only the case is allowed)." };
        var command = new Command("rename", "Rename a playlist. Exit 8 if another playlist has the new name.") { name, newName };
        command.Validators.Add(r => ValidateName(r, newName));
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            return Task.FromResult(WriteChange(runner, json, "playlist rename",
                library.RenamePlaylist(parseResult.GetValue(name)!, parseResult.GetValue(newName)!), "Renamed to"));
        }, "playlist rename");
        return command;
    }

    private static Command Delete(CommandRunner runner)
    {
        var name = NameArgument();
        var command = new Command("delete", "Delete a playlist (the music files are not touched).") { name };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var deleted = library.DeletePlaylist(parseResult.GetValue(name)!);
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "playlist delete", new { deleted = true, playlist = deleted });
            }
            else
            {
                runner.Env.Out.WriteLine($"Deleted playlist '{deleted.Name}' ({deleted.EntryCount} entries).");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "playlist delete");
        return command;
    }

    private static Command Add(CommandRunner runner)
    {
        var name = NameArgument();
        var files = new Argument<string[]>("files") { Description = "Audio files (library tracks or other existing files).", Arity = ArgumentArity.ZeroOrMore };
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
        var position = new Option<int?>("--position") { Description = "Insert before this 1-based position (default: append)." };
        var command = new Command("add", "Add files, an album, an artist's tracks, search results or track ids to a playlist (exit 12 if it does not exist).")
        {
            name, files, album, artist, search, title, albumContains, artistContains, trackIds, position,
        };
        command.Validators.Add(r =>
        {
            var selection = new LibrarySelection(Options.Get(r, album), Options.Get(r, artist), Options.Get(r, search), Options.Get(r, title),
                Options.Get(r, albumContains), Options.Get(r, artistContains));
            var kinds = (Options.Get(r, files) is { Length: > 0 } ? 1 : 0) + selection.Kinds + (Options.Get(r, trackIds) is { Length: > 0 } ? 1 : 0);
            if (kinds > 1)
            {
                r.AddError("Give either file(s), --album/--artist, --search/--title/--album-contains/--artist-contains, or --track-id, not several.");
            }
            else if (kinds == 0)
            {
                r.AddError("Nothing to add: give file(s), --album, --artist, --search or --track-id.");
            }
        });
        runner.SetAction(command, (parseResult, json, _) =>
        {
            var ids = (parseResult.GetValue(trackIds) ?? []).SelectMany(i => i.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            var selection = new LibrarySelection(parseResult.GetValue(album), parseResult.GetValue(artist), parseResult.GetValue(search),
                parseResult.GetValue(title), parseResult.GetValue(albumContains), parseResult.GetValue(artistContains));
            var warnings = new List<string>();
            var inputs = Inputs(runner, parseResult, parseResult.GetValue(files), selection, ids, warnings);
            using var library = runner.OpenLibraryForWrite(parseResult);
            var change = library.AddToPlaylist(parseResult.GetValue(name)!, inputs, parseResult.GetValue(position));
            return Task.FromResult(WriteChange(runner, json, "playlist add", change, $"Added {change.Added} entr{(change.Added == 1 ? "y" : "ies")} to", warnings));
        }, "playlist add");
        return command;
    }

    private static Command Remove(CommandRunner runner)
    {
        var name = NameArgument();
        var positions = new Argument<string>("positions") { Description = "1-based position(s): 3, 2-5 or 1,3,5-7." };
        var command = new Command("remove", "Remove entries by position or range (exit 2 for positions outside the playlist).") { name, positions };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var change = library.RemoveFromPlaylist(parseResult.GetValue(name)!, parseResult.GetValue(positions)!);
            return Task.FromResult(WriteChange(runner, json, "playlist remove", change, $"Removed {change.Removed} entr{(change.Removed == 1 ? "y" : "ies")} from"));
        }, "playlist remove");
        return command;
    }

    private static Command Move(CommandRunner runner)
    {
        var name = NameArgument();
        var from = new Argument<int>("from") { Description = "1-based position of the entry to move." };
        var to = new Argument<int>("to") { Description = "1-based position it should end up at." };
        var command = new Command("move", "Move one entry to another position.") { name, from, to };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var change = library.MovePlaylistEntry(parseResult.GetValue(name)!, parseResult.GetValue(from), parseResult.GetValue(to));
            return Task.FromResult(WriteChange(runner, json, "playlist move", change, "Moved an entry in"));
        }, "playlist move");
        return command;
    }

    private static Command Clear(CommandRunner runner)
    {
        var name = NameArgument();
        var command = new Command("clear", "Remove all entries (the playlist itself is kept).") { name };
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var change = library.ClearPlaylist(parseResult.GetValue(name)!);
            return Task.FromResult(WriteChange(runner, json, "playlist clear", change, "Cleared"));
        }, "playlist clear");
        return command;
    }

    private static Command Import(CommandRunner runner)
    {
        var file = new Argument<string>("file") { Description = "An .m3u, .m3u8 or .pls file." };
        var name = new Option<string?>("--name") { Description = "Playlist name (default: the file name without extension)." };
        var replace = new Option<bool>("--replace") { Description = "Overwrite an existing playlist with that name." };
        var command = new Command("import",
            "Import an M3U/M3U8/PLS file. Entries are matched to library tracks (relative paths are relative to the playlist file; " +
            "\\ and / separators, file:// URIs and %-encoded paths are understood). Files outside the library are kept; unresolved " +
            "entries are kept as missing entries and reported; http(s) streams are skipped with a warning.")
        {
            file, name, replace,
        };
        command.Validators.Add(r =>
        {
            if (Options.Get(r, name) is { } n)
            {
                try
                {
                    MusicLibrary.ValidatePlaylistName(n);
                }
                catch (MusicSpinException ex)
                {
                    r.AddError(ex.Message);
                }
            }
        });
        runner.SetAction(command, (parseResult, json, _) =>
        {
            using var library = runner.OpenLibraryForWrite(parseResult);
            var result = library.ImportPlaylist(parseResult.GetValue(file)!, parseResult.GetValue(name), parseResult.GetValue(replace));
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "playlist import", result);
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (var warning in result.Warnings)
            {
                runner.Env.Error.WriteLine($"music-spin playlist import: warning: {warning}");
            }

            var o = runner.Env.Out;
            o.WriteLine($"{(result.Replaced ? "Replaced" : "Imported")} playlist '{result.Playlist.Name}' from {result.File} ({result.Format}, {result.Encoding}):");
            o.WriteLine($"  {result.Entries} entries: {result.InLibrary} in the library, {result.OutsideLibrary} other files, {result.MissingCount} missing; {result.SkippedStreams.Count} stream(s) skipped");
            foreach (var m in result.Missing)
            {
                o.WriteLine($"  missing (line {m.Line}): {m.Entry}");
            }

            if (result.MissingCount > result.Missing.Count)
            {
                o.WriteLine($"  ... and {result.MissingCount - result.Missing.Count} more missing");
            }

            return Task.FromResult(ExitCodes.Success);
        }, "playlist import");
        return command;
    }

    private static Command Export(CommandRunner runner)
    {
        var name = NameArgument();
        var file = new Argument<string>("file") { Description = "Target file. The extension picks the format unless --format is given." };
        var format = new Option<string?>("--format") { Description = "m3u8 (UTF-8), m3u (Latin-1 if possible) or pls. Default: from the extension, else m3u8." };
        var relative = new Option<bool>("--relative") { Description = "Write paths relative to the playlist file's folder." };
        var force = new Option<bool>("--force") { Description = "Overwrite an existing file." };
        var command = new Command("export", "Export a playlist to an M3U8/M3U/PLS file (written atomically; exit 8 if the file exists without --force).")
        {
            name, file, format, relative, force,
        };
        command.Validators.Add(r =>
        {
            if (Options.Get(r, format) is { } f && !PlaylistFiles.TryParseFormat(f, out _))
            {
                r.AddError($"Unknown format '{f}'. Use m3u8, m3u or pls.");
            }
        });
        runner.SetAction(command, (parseResult, json, _) =>
        {
            PlaylistFormat? chosen = parseResult.GetValue(format) is { } f && PlaylistFiles.TryParseFormat(f, out var parsed) ? parsed : null;
            using var library = runner.OpenLibraryForRead(parseResult);
            var result = library.ExportPlaylist(parseResult.GetValue(name)!, parseResult.GetValue(file)!, chosen, parseResult.GetValue(relative), parseResult.GetValue(force));
            if (json)
            {
                JsonOutput.WriteSuccess(runner.Env.Out, "playlist export", result);
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (var warning in result.Warnings)
            {
                runner.Env.Error.WriteLine($"music-spin playlist export: warning: {warning}");
            }

            runner.Env.Out.WriteLine($"Exported '{result.Name}' ({result.Entries} entries, {result.Format}{(result.Relative ? ", relative paths" : string.Empty)}) to {result.File}");
            return Task.FromResult(ExitCodes.Success);
        }, "playlist export");
        return command;
    }

    /// <summary>Entries for <c>playlist add</c> (CLI and MCP): library tracks for album/artist/search, track ids, or files.</summary>
    public static IReadOnlyList<PlaylistInput> Inputs(CommandRunner runner, ParseResult parseResult, IReadOnlyList<string>? files, LibrarySelection selection,
        IReadOnlyList<string>? trackIds, List<string> warnings)
    {
        if (QueueSource.LibraryTracks(runner, parseResult, selection, warnings) is { } tracks)
        {
            return [.. tracks.Select(PlaylistInput.From)];
        }

        using var library = runner.OpenLibraryForRead(parseResult);
        return trackIds is { Count: > 0 } ? library.PlaylistInputsForTrackIds(trackIds) : library.PlaylistInputsForFiles(files ?? []);
    }

    /// <summary>Saves the running player's queue (current play order) as a playlist.</summary>
    /// <exception cref="MusicSpinException">PlayerNotRunning; InvalidState (empty queue or name taken); InvalidArgument (name, too long).</exception>
    public static async Task<PlaylistChange> SaveQueueAsync(CommandRunner runner, ParseResult parseResult, string name, bool replace, CancellationToken cancellationToken)
    {
        MusicLibrary.ValidatePlaylistName(name);
        var items = new List<QueueItem>();
        await using (var client = await PlayerSupport.ConnectAsync(runner, parseResult, cancellationToken).ConfigureAwait(false))
        {
            while (true)
            {
                var page = ProtocolJson.Deserialize<QueueListing>(
                    await client.SendAsync("queue", new { offset = items.Count, limit = 10_000 }, cancellationToken).ConfigureAwait(false))!;
                items.AddRange(page.Items.Select(e => e.Item));
                if (page.Items.Count == 0 || items.Count >= page.Total)
                {
                    break;
                }
            }
        }

        if (items.Count == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "The queue is empty; there is nothing to save.");
        }

        using var library = runner.OpenLibraryForWrite(parseResult);
        return library.CreatePlaylist(name, [.. items.Select(i => new PlaylistInput(i.Path, i.TrackId, i.Title, i.Artist, i.DurationSeconds))], replace);
    }

    public static int WriteChange(CommandRunner runner, bool json, string command, PlaylistChange change, string verb, IReadOnlyList<string>? warnings = null)
    {
        if (json)
        {
            JsonOutput.WriteSuccess(runner.Env.Out, command, WithWarnings(change, warnings));
        }
        else
        {
            foreach (var warning in warnings ?? [])
            {
                runner.Env.Error.WriteLine($"music-spin {command}: warning: {warning}");
            }

            runner.Env.Out.WriteLine($"{verb} playlist {SummaryLine(change.Playlist)}");
        }

        return ExitCodes.Success;
    }

    /// <summary>The change as JSON, with a <c>warnings</c> array when there are any.</summary>
    public static JsonNode WithWarnings(PlaylistChange change, IReadOnlyList<string>? warnings)
    {
        var node = JsonSerializer.SerializeToNode(change, JsonOutput.Options)!;
        if (warnings is { Count: > 0 })
        {
            node["warnings"] = JsonSerializer.SerializeToNode(warnings, JsonOutput.Options);
        }

        return node;
    }

    private static string SummaryLine(PlaylistSummary p)
        => string.Create(CultureInfo.InvariantCulture,
            $"'{p.Name}'  ({p.EntryCount} entr{(p.EntryCount == 1 ? "y" : "ies")}{(p.MissingCount > 0 ? $", {p.MissingCount} missing" : string.Empty)}, {Human.Time(TimeSpan.FromSeconds(p.DurationSeconds))})");
}
