using System.CommandLine;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicSpin.Cli.Output;
using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Library;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MusicSpin.Cli.Commands;

/// <summary>
/// <c>music-spin mcp</c>: a Model Context Protocol server over stdio (official C# SDK) whose tools drive the same
/// background player as the CLI commands, plus read-only library tools.
/// </summary>
internal static class McpCommand
{
    public const string Instructions =
        "MusicSpin controls a local hi-res music player. play/queue_add start the background player when needed and return at once; " +
        "the other playback tools need a running player (error 'player_not_running' otherwise). Use search/tracks/albums/artists to find music, " +
        "then play with album/artist/search/title/albumContains/artistContains/playlist or file paths. favorite_add marks favorites (current: true for the track playing now); " +
        "play, tracks and radio take favorites: true. Tool errors start with a stable code such as 'not_found', 'invalid_state' or 'usage'.";

    public static Command Create(CommandRunner runner)
    {
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var command = new Command("mcp",
            "Run an MCP (Model Context Protocol) server on stdin/stdout. Tools: play, pause, resume, toggle, stop, next, previous, seek, volume, " +
            "status, queue, queue_add, queue_remove, queue_move, queue_clear, queue_save, repeat, shuffle, quit_player, search, tracks, albums, artists, library_stats, favorites, favorite_add, favorite_remove, playlists, playlist_show, " +
            "playlist_create, playlist_rename, playlist_add, playlist_remove, playlist_move, playlist_clear, playlist_delete, playlist_import, playlist_export. " +
            "--backend/--device/--exclusive apply " +
            "when a tool has to start the background player.")
        {
            backend, device, exclusive,
        };

        runner.SetAction(command, async (parseResult, _, cancellationToken) =>
        {
            var tools = new McpTools(runner, parseResult, parseResult.GetValue(backend)!, parseResult.GetValue(device), parseResult.GetValue(exclusive));
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "music-spin", Title = "MusicSpin", Version = Version },
                ServerInstructions = Instructions,
                ToolCollection = [.. tools.Create()],
            };
            var (input, output) = runner.Env.McpStreams?.Invoke() ?? (Console.OpenStandardInput(), Console.OpenStandardOutput());
            await using var transport = new StreamServerTransport(input, output, "music-spin");
            await using var server = McpServer.Create(transport, options);
            try
            {
                await server.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ctrl+C / client gone
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static string Version =>
        typeof(McpCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    private sealed class McpTools(CommandRunner runner, ParseResult parseResult, string backend, string? device, bool exclusive)
    {
        public IEnumerable<McpServerTool> Create()
        {
            yield return Tool("play", "Play files or library music in the background player (starts it if needed) and return its status. " +
                "Give one source: files, album (optionally with artist), artist, genre (optionally with artist), playlist, favorites, or a query (search and/or title/albumContains/artistContains, combined).",
                ([Description("Absolute paths of audio files, played in order.")] string[]? files = null,
                 [Description("Album title (exact, case-insensitive) or id from 'albums'.")] string? album = null,
                 [Description("Artist name (exact; all their tracks), or the album artist when album is given.")] string? artist = null,
                 [Description("Genre name, or an array of names (any of them): every library track of that genre (exact name, case/diacritic-insensitive; " +
                     "multi-genre tags split on ; / , |), albums by album artist then year. Combine with artist to narrow. No match: not_found.")] JsonElement? genre = null,
                 [Description("Play every library track matching this search (syntax as the search tool), in album order.")] string? search = null,
                 [Description("Library tracks whose title contains these words.")] string? title = null,
                 [Description("Library tracks whose album title contains these words.")] string? albumContains = null,
                 [Description("Library tracks whose artist contains these words.")] string? artistContains = null,
                 [Description("A saved playlist name (see 'playlists'). Missing entries are left out with a warning.")] string? playlist = null,
                 [Description("Play the favorite tracks, most recently favorited first.")] bool favorites = false,
                 [Description("Turn the player's shuffle mode on for this queue.")] bool shuffle = false,
                 [Description("Shuffle seed for a reproducible order.")] int? seed = null,
                 CancellationToken ct = default) => Guard(() => SetQueueAsync(files, new LibrarySelection(album, artist, search, title, albumContains, artistContains, favorites, Genres(genre)),
                    playlist, shuffle, seed, ct)));
            yield return Tool("radio", "Endless random playback of the library (or the tracks the filters select) in the background player; returns its status " +
                "(queueMode 'radio', radio.description, radio.poolSize). Each list is ORed, different filters are ANDed; exclude* lists leave matching tracks out (exclude wins). No track repeats within the last 100 " +
                "(or pool size - 1); the same artist/album is not played twice in a row when there is a choice. next/previous/pause/stop work as usual; " +
                "play or queue_clear end radio; shuffle is refused (radio is random); repeat one replays the current track. Nothing matches: not_found.",
                ([Description("Exact artist names (track or album artist); unknown: not_found.")] string[]? artists = null,
                 [Description("Exact album titles or album ids; unknown: not_found.")] string[]? albums = null,
                 [Description("Exact genre names (case/diacritic-insensitive; multi-genre tags are split on ; / , |).")] string[]? genres = null,
                 [Description("Years or inclusive ranges: \"1959\", \"1990-1999\".")] string[]? years = null,
                 [Description("Format ids, e.g. flac, dsd, mp3.")] string[]? formats = null,
                 [Description("Search texts (syntax as the search tool); a track matching any of them qualifies.")] string[]? searches = null,
                 [Description("Only hi-res tracks.")] bool hires = false,
                 [Description("Only favorite tracks; narrows the other filters.")] bool favorites = false,
                 [Description("Play favorites 3x as often as other tracks, without limiting the station to them.")] bool preferFavorites = false,
             [Description("Leave out these artists (track or album artist). Excludes win over includes; unknown names are ignored.")] string[]? excludeArtists = null,
             [Description("Leave out these albums (titles or ids; unknown ignored).")] string[]? excludeAlbums = null,
             [Description("Leave out these genres.")] string[]? excludeGenres = null,
             [Description("Leave out these years or inclusive ranges (\"1959\", \"1990-1999\").")] string[]? excludeYears = null,
             [Description("Leave out these format ids.")] string[]? excludeFormats = null,
             [Description("Leave out tracks matching any of these search texts.")] string[]? excludeSearches = null,
                 CancellationToken ct = default) => Guard(async () =>
                {
                    var filter = new RadioFilter
                    {
                        Artists = RadioCommand.List(artists), Albums = RadioCommand.List(albums), Genres = RadioCommand.List(genres),
                        Years = RadioCommand.Years(null, years), Formats = RadioCommand.List(formats), Searches = RadioCommand.List(searches), HiRes = hires, Favorites = favorites, PreferFavorites = preferFavorites,
                    ExcludeArtists = RadioCommand.List(excludeArtists), ExcludeAlbums = RadioCommand.List(excludeAlbums), ExcludeGenres = RadioCommand.List(excludeGenres),
                    ExcludeYears = RadioCommand.Years(null, excludeYears), ExcludeFormats = RadioCommand.List(excludeFormats), ExcludeSearches = RadioCommand.List(excludeSearches),
                    };
                    var (status, started) = await RadioCommand.StartAsync(runner, parseResult, filter, backend, device, exclusive, ct).ConfigureAwait(false);
                    return With(status, started, null, null);
                }));
            yield return Simple("pause", "Pause playback.");
            yield return Tool("resume", "Resume (or start) playback of the current queue entry. When no player runs but a saved queue exists, " +
                "starts the player, which restores that queue and continues where it stopped.",
                (CancellationToken ct = default) => Guard(async () =>
                {
                    var (data, started) = await PlayerSupport.ResumeAsync(runner, parseResult, backend, device, exclusive, ct).ConfigureAwait(false);
                    return started ? With(data, true, null, null) : data;
                }));
            yield return Simple("toggle", "Pause when playing, otherwise resume.");
            yield return Simple("stop", "Stop and rewind the current track (the queue is kept).");
            yield return Simple("next", "Play the next queue entry (in radio: the next pick, never runs out). Error 'invalid_state' at the end of the queue.");
            yield return Simple("previous", "Play the previous queue entry (in radio: back through the last 100 tracks). Error 'invalid_state' on the first entry.", "prev");
            yield return Tool("seek", "Seek within the current track. Absolute positions beyond the track are an error; relative ones are clamped.",
                ([Description("Seconds (90), mm:ss (1:30), h:mm:ss, or relative: +10, -10, +0:30.")] string position, CancellationToken ct = default)
                    => Guard(() =>
                    {
                        if (!SeekSyntax.TryParse(position, out var seconds, out var relative, out var error))
                        {
                            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error!);
                        }

                        return SendAsync("seek", relative ? new { delta = seconds } : new { seconds }, ct);
                    }));
            yield return Tool("volume", "Set or change the volume (1.0 = bit-perfect).",
                ([Description("0..1 (0.5), percent (50%), or a change: +0.1, -10%.")] string level, CancellationToken ct = default)
                    => Guard(() =>
                    {
                        if (!PlayerSupport.TryParseVolume(level, out var value, out var relative, out var error))
                        {
                            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error!);
                        }

                        return SendAsync("volume", relative ? new { delta = value } : new { value }, ct);
                    }));
            yield return Tool("status",
                "Player status: state, track (codec, sample rate, bit depth, tags), position, duration, volume, queue index/length, output. " +
                "Returns {\"running\":false} when no background player runs.",
                (CancellationToken ct = default) => Guard(async () =>
                {
                    try
                    {
                        return await SendAsync("status", null, ct).ConfigureAwait(false);
                    }
                    catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.PlayerNotRunning)
                    {
                        return JsonSerializer.SerializeToElement(new { running = false, message = ex.Message });
                    }
                }), readOnly: true);
            yield return Tool("queue", "List the player's queue.",
                (int offset = 0, int limit = 50, CancellationToken ct = default) => Guard(() => SendAsync("queue", new { offset, limit }, ct)), readOnly: true);
            yield return Tool("queue_add", "Add files, library tracks or a playlist to the queue: at the end, or up next with position (starts the player, stopped, if needed). " +
                "shuffle shuffles only the added tracks.",
                (string[]? files = null, string? album = null, string? artist = null, string? search = null, string? title = null, string? albumContains = null,
                 string? artistContains = null, string? playlist = null, bool favorites = false, bool shuffle = false, int? seed = null,
                 [Description("end (default): append; next: play right after the current track; upnext: after the tracks already added to play next. Radio: always up next.")] string? position = null,
                 CancellationToken ct = default)
                    => Guard(() => AddAsync(files, new LibrarySelection(album, artist, search, title, albumContains, artistContains, favorites), playlist, shuffle, seed, Position(position), ct)));
            yield return Tool("queue_save", "Save the player's queue (current play order) as a playlist.",
                ([Description("Playlist name (unique, case-insensitive).")] string name,
                 [Description("Overwrite an existing playlist with this name.")] bool replace = false,
                 CancellationToken ct = default) => Guard(async () =>
                    ToElement(await PlaylistCommands.SaveQueueAsync(runner, parseResult, name, replace, ct).ConfigureAwait(false))));
            yield return Tool("queue_clear", "Stop playback and empty the queue; with upNext, only remove the tracks added to play next (the current track and the rest keep playing).",
                ([Description("Only remove the up-next tracks.")] bool upNext = false, CancellationToken ct = default)
                    => Guard(() => SendAsync("queue.clear", upNext ? new { upNext } : null, ct)));
            yield return Tool("queue_remove", "Remove one queue entry by its 0-based index (as in 'queue'). The current track can't be removed (invalid_state): use next first.",
                ([Description("0-based index from 'queue'.")] int index, CancellationToken ct = default)
                    => Guard(() => SendAsync("queue.remove", new { index }, ct)));
            yield return Tool("queue_move", "Move a queue entry from one 0-based index to another (as in 'queue'). Moving an entry right after the current track, " +
                "or among the up-next tracks, makes it up next.",
                ([Description("0-based index of the entry to move.")] int from,
                 [Description("0-based index it should end up at.")] int to,
                 CancellationToken ct = default) => Guard(() => SendAsync("queue.move", new { from, to }, ct)));
            yield return Tool("repeat", "Show or set the repeat mode. off: play the queue once; one: a track that ends starts again (next/previous still move); " +
                "all: loop the queue (next/previous wrap around). Saved as the default for new players.",
                ([Description("off | one | all. Omit to just read the current mode.")] string? mode = null, CancellationToken ct = default) => Guard(async () =>
                {
                    if (mode is not null && !PlaybackModes.TryParseRepeat(mode, out _))
                    {
                        throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Unknown repeat mode '{mode}'. Use {PlaybackModes.RepeatValues}.");
                    }

                    var status = await SendAsync("repeat", mode is null ? null : new { mode = mode.Trim().ToLowerInvariant() }, ct).ConfigureAwait(false);
                    return mode is null ? status : WithWarning(status, PlayerSupport.SaveModes(runner, parseResult, status));
                }));
            yield return Tool("shuffle", "Show or change shuffle on the running queue. on: shuffle the entries after the current track (it keeps playing; " +
                "reshuffles when already on); off: restore the original order at the current track; toggle. New queues follow the mode.",
                ([Description("on | off | toggle. Omit to just read the current mode.")] string? mode = null,
                 [Description("Seed for a reproducible shuffle (only when turning shuffle on).")] int? seed = null,
                 CancellationToken ct = default) => Guard(async () =>
                {
                    if (mode is not null && !PlaybackModes.TryParseShuffle(mode, false, out _))
                    {
                        throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Unknown shuffle mode '{mode}'. Use on|off|toggle.");
                    }

                    if (mode is null && seed is not null)
                    {
                        throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "seed needs mode 'on'.");
                    }

                    var status = await SendAsync("shuffle", mode is null ? null : new { mode = mode.Trim().ToLowerInvariant(), seed }, ct).ConfigureAwait(false);
                    return mode is null ? status : WithWarning(status, PlayerSupport.SaveModes(runner, parseResult, status));
                }));
            yield return Tool("quit_player", "Shut the background player down.", (CancellationToken ct = default) => Guard(async () =>
            {
                await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, ct).ConfigureAwait(false);
                var info = await client.HelloAsync(ct).ConfigureAwait(false);
                await client.QuitAsync(cancellationToken: ct).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { stopped = true, pid = info.Pid });
            }));
            yield return Tool("search", "Search the music library: artists, albums and tracks, best matches first. Case- and diacritic-insensitive " +
                "(beyonce finds Beyoncé); every word must match somewhere; \"quoted phrases\" must appear as a whole; artist:, album:, title:, genre: restrict " +
                "a word or \"phrase\" to one field; year:1959 or year:1950-1959; is:favorite (is:fav) keeps favorite tracks only (no artists or albums then).",
                ([Description("Search text, e.g. miles blue, artist:\"miles davis\" year:1959, title:\"so what\".")] string text, int limit = 20) => Guard(() =>
                {
                    using var library = runner.OpenLibraryForRead(parseResult);
                    var result = library.Search(text, Paging(0, limit));
                    return Task.FromResult(ToElement(new SearchDto(text, [.. result.Artists.Select(ArtistDto.From)], [.. result.Albums.Select(AlbumDto.From)],
                        PageDto.From(result.Tracks, LibraryTrackDto.From))));
                }), readOnly: true);
            yield return Tool("tracks", "List library tracks with filters (all must match). Text filters are case/diacritic-insensitive; every word must match.",
                ([Description("Exact artist name (track or album artist); unknown: not_found.")] string? artist = null,
                 [Description("Exact album title or album id (GUID); unknown: not_found.")] string? album = null,
                 [Description("Search syntax as the search tool.")] string? search = null,
                 [Description("Title contains these words.")] string? title = null,
                 [Description("Album title contains these words.")] string? albumContains = null,
                 [Description("Artist contains these words.")] string? artistContains = null,
                 [Description("Format ids, e.g. flac, dsd, mp3.")] string[]? formats = null,
                 int? minSampleRate = null, int? minBitDepth = null, int? yearFrom = null, int? yearTo = null, bool hiresOnly = false,
                 [Description("Only favorite tracks.")] bool favorites = false,
                 [Description("default, relevance (with search), title, artist, album, year, duration, added, samplerate, bitdepth, size, path, favorited (with favorites: newest first; descending reverses).")] string sort = "default",
                 bool descending = false, int offset = 0, int limit = 50) => Guard(() =>
                {
                    var parsedSort = sort.ToLowerInvariant() switch
                    {
                        "samplerate" => TrackSort.SampleRate,
                        "bitdepth" => TrackSort.BitDepth,
                        var s when Enum.TryParse<TrackSort>(s, ignoreCase: true, out var v) && !int.TryParse(s, out _) => v,
                        _ => throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Unknown sort '{sort}'."),
                    };
                    using var library = runner.OpenLibraryForRead(parseResult);
                    var page = library.Tracks(new TrackQuery
                    {
                        Artist = artist, Album = album, Text = search, Title = title, AlbumContains = albumContains, ArtistContains = artistContains,
                        Formats = formats is { Length: > 0 } ? formats : null, MinSampleRate = minSampleRate, MinBitDepth = minBitDepth,
                        YearFrom = yearFrom, YearTo = yearTo, HiRes = hiresOnly ? true : null, Favorite = favorites ? true : null,
                        Sort = parsedSort, Descending = parsedSort == TrackSort.Favorited ? !descending : descending,
                        Paging = Paging(offset, limit),
                    });
                    return Task.FromResult(ToElement(PageDto.From(page, LibraryTrackDto.From)));
                }), readOnly: true);
            yield return Tool("albums", "List library albums: by one artist (exact name), or with title/artist words.",
                ([Description("Exact artist name; unknown: not_found.")] string? artist = null,
                 [Description("Album title contains these words.")] string? title = null,
                 [Description("Album artist or a track artist contains these words.")] string? artistContains = null,
                 bool hiresOnly = false, int offset = 0, int limit = 50) => Guard(() =>
                {
                    using var library = runner.OpenLibraryForRead(parseResult);
                    var page = library.Albums(new AlbumQuery
                    {
                        Artist = artist, Title = title, ArtistContains = artistContains, HiRes = hiresOnly ? true : null, Paging = Paging(offset, limit),
                    });
                    return Task.FromResult(ToElement(PageDto.From(page, AlbumDto.From)));
                }), readOnly: true);
            yield return Tool("artists", "List library artists, optionally those whose name contains some words.",
                ([Description("Name contains these words (case/diacritic-insensitive).")] string? name = null, int offset = 0, int limit = 100) => Guard(() =>
            {
                using var library = runner.OpenLibraryForRead(parseResult);
                return Task.FromResult(ToElement(PageDto.From(library.Artists(new ArtistQuery { Name = name, Paging = Paging(offset, limit) }), ArtistDto.From)));
            }), readOnly: true);
            yield return Tool("library_stats", "Library totals: tracks, albums, artists, formats, hi-res counts.", () => Guard(() =>
            {
                using var library = runner.OpenLibraryForRead(parseResult);
                return Task.FromResult(ToElement(library.Stats()));
            }), readOnly: true);
            yield return Tool("favorites", "List the favorite tracks, most recently favorited first, with how many favorites are no longer in the library (missing). " +
                "With missing: true, the paths of those instead.",
                ([Description("List the paths of favorites that are no longer in the library instead.")] bool missing = false,
                 int offset = 0, int limit = 50) => Guard(() =>
                {
                    using var library = runner.OpenLibraryForRead(parseResult);
                    var summary = library.FavoritesSummary();
                    if (missing)
                    {
                        return Task.FromResult(ToElement(new { count = summary.Missing, items = summary.MissingPaths }));
                    }

                    var page = PageDto.From(library.Tracks(new TrackQuery { Favorite = true, Sort = TrackSort.Favorited, Descending = true, Paging = Paging(offset, limit) }),
                        LibraryTrackDto.From);
                    return Task.FromResult(ToElement(new { page.Total, page.Offset, page.Limit, page.Count, page.Items, missing = summary.Missing }));
                }), readOnly: true);
            foreach (var add in new[] { true, false })
            {
                yield return Tool(add ? "favorite_add" : "favorite_remove", (add
                        ? "Mark library tracks as favorites (tracks that already are stay as they are; files outside the library: not_found)."
                        : "Unmark favorite tracks (others are left alone). Files may also be paths of favorites no longer in the library.") +
                    " Give one of: current (the track playing now), trackIds, files, album (optionally with artist), artist, or a query (search and/or title/albumContains/artistContains)" +
                    (add ? "." : ", or missing (every favorite no longer in the library).") + " Returns requested, changed, unchanged and the favorite count.",
                    ([Description("The track the background player is on now (player_not_running without a player; not_found when there is no current track).")] bool current = false,
                     [Description("Library track ids (GUIDs from search/tracks results).")] string[]? trackIds = null,
                     [Description("Absolute paths of library tracks.")] string[]? files = null,
                     string? album = null, string? artist = null, string? search = null, string? title = null, string? albumContains = null, string? artistContains = null,
                     [Description("favorite_remove only: remove every favorite that is no longer in the library.")] bool missing = false,
                     CancellationToken ct = default) => Guard(async () =>
                    {
                        var targets = new FavoriteTargets(files, new LibrarySelection(album, artist, search, title, albumContains, artistContains),
                            FavoriteCommands.SplitIds(trackIds), current, missing);
                        if ((missing && add ? "missing only applies to favorite_remove." : FavoriteCommands.Validate(targets, add, cli: false)) is { } error)
                        {
                            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error);
                        }

                        return ToElement(await FavoriteCommands.EditAsync(runner, parseResult, add, targets, ct).ConfigureAwait(false));
                    }));
            }
            yield return Tool("library_relocate",
                "After a music folder was moved or renamed on disk (or a drive is mounted elsewhere), point the library tracks under 'from' at 'to' " +
                "without rescanning; date added and playlist entries follow. Use dryRun first. 'file_not_found' if 'to' does not exist, 'not_found' if nothing " +
                "in the library is under 'from', 'invalid_state' if 'to' is outside the library folders, a new path is already a track, or no file exists at 'to' (without force).",
                ([Description("Old folder (a library folder or a folder inside one).")] string from,
                 [Description("Folder the files are in now (must exist).")] string to,
                 [Description("Only report what would change.")] bool dryRun = false,
                 [Description("Relocate even if none of the files exist at 'to'.")] bool force = false)
                    => Library(read: false, library => library.Relocate(from, to, dryRun, force)), destructive: true);
            foreach (var tool in PlaylistTools())
            {
                yield return tool;
            }
        }

        private IEnumerable<McpServerTool> PlaylistTools()
        {
            yield return Tool("playlists", "List saved playlists: name, entry count, missing entries, duration.", () => Library(read: true, library =>
                new { total = library.Playlists().Count, items = library.Playlists() }), readOnly: true);
            yield return Tool("playlist_show", "A playlist's entries (1-based position, status library|file|missing, path, tags).",
                (string name, int offset = 0, int limit = 100) => Library(read: true, library =>
                {
                    var details = library.Playlist(name, Paging(offset, limit));
                    return new { playlist = details.Playlist, entries = new PageDto<PlaylistEntry>(details.Entries.Total, details.Entries.Offset, details.Entries.Limit, details.Entries.Items.Count, details.Entries.Items) };
                }), readOnly: true);
            yield return Tool("playlist_create", "Create an empty playlist. Names are unique case-insensitively ('invalid_state' if taken).",
                (string name) => Library(read: false, library => library.CreatePlaylist(name)));
            yield return Tool("playlist_rename", "Rename a playlist.",
                (string name, [Description("New name.")] string newName) => Library(read: false, library => library.RenamePlaylist(name, newName)));
            yield return Tool("playlist_add", "Add to a playlist: give one of files, album (optionally with artist), artist, trackIds, or a query (search and/or title/albumContains/artistContains).",
                (string name,
                 [Description("Absolute paths of audio files (library tracks or other existing files).")] string[]? files = null,
                 string? album = null, string? artist = null, string? search = null, string? title = null, string? albumContains = null, string? artistContains = null,
                 [Description("Library track ids: GUIDs (lowercase 'D' format) from search/tracks results. Unknown: not_found; not a GUID: usage.")] string[]? trackIds = null,
                 [Description("Insert before this 1-based position (default: append).")] int? position = null) => Guard(() =>
                {
                    var selection = new LibrarySelection(album, artist, search, title, albumContains, artistContains);
                    var kinds = (files is { Length: > 0 } ? 1 : 0) + selection.Kinds + (trackIds is { Length: > 0 } ? 1 : 0);
                    if (kinds != 1)
                    {
                        throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, kinds == 0
                            ? "Nothing to add: give files, album, artist, search/title/albumContains/artistContains or trackIds."
                            : "Give only one of files, album/artist, search/title/albumContains/artistContains, trackIds.");
                    }

                    var warnings = new List<string>();
                    var inputs = PlaylistCommands.Inputs(runner, parseResult, files, selection, trackIds, warnings);
                    using var library = runner.OpenLibraryForWrite(parseResult);
                    return Task.FromResult(ToElement(PlaylistCommands.WithWarnings(library.AddToPlaylist(name, inputs, position), warnings)));
                }));
            yield return Tool("playlist_remove", "Remove entries by 1-based position(s): \"3\", \"2-5\" or \"1,3,5-7\".",
                (string name, string positions) => Library(read: false, library => library.RemoveFromPlaylist(name, positions)));
            yield return Tool("playlist_move", "Move the entry at 1-based position 'from' to position 'to'.",
                (string name, int from, int to) => Library(read: false, library => library.MovePlaylistEntry(name, from, to)));
            yield return Tool("playlist_clear", "Remove all entries of a playlist (the playlist is kept).",
                (string name) => Library(read: false, library => library.ClearPlaylist(name)), destructive: true);
            yield return Tool("playlist_delete", "Delete a playlist (music files are not touched).",
                (string name) => Library(read: false, library => new { deleted = true, playlist = library.DeletePlaylist(name) }), destructive: true);
            yield return Tool("playlist_import", "Import an M3U/M3U8/PLS file (path readable by this server) as a playlist; reports unresolved entries and skipped streams.",
                ([Description("Path of the .m3u/.m3u8/.pls file.")] string file,
                 [Description("Playlist name (default: the file name).")] string? name = null,
                 [Description("Overwrite an existing playlist with that name.")] bool replace = false) => Library(read: false, library => library.ImportPlaylist(file, name, replace)));
            yield return Tool("playlist_export", "Export a playlist to an M3U8/M3U/PLS file (path writable by this server).",
                (string name,
                 [Description("Target file path.")] string file,
                 [Description("m3u8, m3u or pls (default: from the extension, else m3u8).")] string? format = null,
                 [Description("Write paths relative to the file's folder.")] bool relative = false,
                 [Description("Overwrite an existing file.")] bool overwrite = false) => Library(read: true, library =>
                {
                    PlaylistFormat? chosen = null;
                    if (format is not null)
                    {
                        chosen = PlaylistFiles.TryParseFormat(format, out var parsed)
                            ? parsed
                            : throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Unknown format '{format}'. Use m3u8, m3u or pls.");
                    }

                    return library.ExportPlaylist(name, file, chosen, relative, overwrite);
                }), destructive: true);
        }

        private McpServerTool Simple(string name, string description, string? cmd = null)
            => Tool(name, description, (CancellationToken ct = default) => Guard(() => SendAsync(cmd ?? name, null, ct)));

        private static McpServerTool Tool(string name, string description, Delegate method, bool readOnly = false, bool destructive = false)
            => McpServerTool.Create(method, new McpServerToolCreateOptions
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                Destructive = destructive,
                OpenWorld = false,
            });

        /// <summary>Runs <paramref name="action"/> on a short-lived library handle and returns its result as JSON.</summary>
        private Task<CallToolResult> Library<T>(bool read, Func<MusicLibrary, T> action) => Guard(() =>
        {
            using var library = read ? runner.OpenLibraryForRead(parseResult) : runner.OpenLibraryForWrite(parseResult);
            return Task.FromResult(ToElement(action(library)));
        });

        private static Paging Paging(int offset, int limit)
            => MusicSpin.Core.Library.Paging.Validate(offset, limit) is { } error
                ? throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error.Replace("--", string.Empty, StringComparison.Ordinal))
                : new Paging(offset, limit);

        /// <summary>The MCP <c>genre</c> argument: a string or an array of strings (null when absent).</summary>
        internal static IReadOnlyList<string>? Genres(JsonElement? genre) => genre switch
        {
            null => null,
            { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            { ValueKind: JsonValueKind.String } g => [g.GetString()!],
            { ValueKind: JsonValueKind.Array } g when g.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                => g.GetArrayLength() == 0 ? null : [.. g.EnumerateArray().Select(e => e.GetString()!)],
            _ => throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "genre must be a string or an array of strings."),
        };

        private static JsonElement ToElement<T>(T value) => ProtocolJson.ToElement(value);

        private async Task<JsonElement> SendAsync(string cmd, object? args, CancellationToken ct)
        {
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, ct).ConfigureAwait(false);
            return await client.SendAsync(cmd, args, ct).ConfigureAwait(false);
        }

        private async Task<JsonElement> SetQueueAsync(string[]? files, LibrarySelection selection, string? playlist, bool shuffle, int? seed,
            CancellationToken ct)
        {
            if (QueueSource.ValidateValues(files, selection, playlist, shuffle, seed) is { } error)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error);
            }

            var resolved = QueueSource.Resolve(runner, parseResult, files, selection, playlist, shuffle: false, seed: null);
            var used = QueueSource.PickSeed(shuffle, seed);
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, backend, device, exclusive, ct).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var status = await client.SendAsync("queue.set", new { items = resolved.Items, play = true, shuffle = used is not null ? true : (bool?)null, seed = used }, ct)
                    .ConfigureAwait(false);
                return WithWarnings(With(status, started, used, null), resolved.Warnings);
            }
        }

        private async Task<JsonElement> AddAsync(string[]? files, LibrarySelection selection, string? playlist, bool shuffle, int? seed, string? position,
            CancellationToken ct)
        {
            if (QueueSource.ValidateValues(files, selection, playlist, shuffle, seed) is { } error)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error);
            }

            var resolved = QueueSource.Resolve(runner, parseResult, files, selection, playlist, shuffle, seed);
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, backend, device, exclusive, ct).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var status = await client.SendAsync("queue.add", new { items = resolved.Items, position }, ct).ConfigureAwait(false);
                return WithWarnings(With(status, started, resolved.Seed, resolved.Items.Count), resolved.Warnings);
            }
        }

        /// <summary>The queue_add position (end | next | upnext), checked before anything is resolved.</summary>
        private static string? Position(string? position)
            => position?.Trim().ToLowerInvariant() switch
            {
                null or "" => null,
                "end" or "next" or "upnext" => position.Trim().ToLowerInvariant(),
                _ => throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"Unknown position '{position}'. Use end|next|upnext."),
            };

        private static JsonElement WithWarnings(JsonElement status, List<string> warnings)
        {
            if (warnings.Count == 0)
            {
                return status;
            }

            var node = JsonNode.Parse(status.GetRawText())!.AsObject();
            node["warnings"] = new JsonArray([.. warnings.Select(w => (JsonNode?)JsonValue.Create(w))]);
            return JsonSerializer.SerializeToElement(node);
        }

        private static JsonElement WithWarning(JsonElement status, string? warning)
        {
            if (warning is null)
            {
                return status;
            }

            var node = JsonNode.Parse(status.GetRawText())!.AsObject();
            node["warnings"] = new JsonArray(warning);
            return JsonSerializer.SerializeToElement(node);
        }

        private static JsonElement With(JsonElement status, bool started, int? seed, int? added)
        {
            var node = JsonNode.Parse(status.GetRawText())!.AsObject();
            node["playerStarted"] = started;
            if (seed is not null)
            {
                node["seed"] = seed;
            }

            if (added is not null)
            {
                node["added"] = added;
            }

            return JsonSerializer.SerializeToElement(node);
        }

        /// <summary>
        /// Results are compact JSON text (same shape as the CLI's "data"). MusicSpin errors are tool errors
        /// (isError=true) whose text starts with the stable code: "player_not_running: No background player ...".
        /// </summary>
        private static async Task<CallToolResult> Guard(Func<Task<JsonElement>> action)
        {
            try
            {
                return Text(ProtocolJson.Serialize(await action().ConfigureAwait(false)), error: false);
            }
            catch (PlayerRequestException ex)
            {
                return Text($"{ExitCodes.Name(ex.ExitCode)}: {ex.Message}", error: true);
            }
            catch (MusicSpinException ex)
            {
                return Text($"{ExitCodes.Name(ExitCodes.From(ex.Code))}: {ex.Message}", error: true);
            }
        }

        private static CallToolResult Text(string text, bool error) => new()
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = error ? true : null,
        };
    }
}
