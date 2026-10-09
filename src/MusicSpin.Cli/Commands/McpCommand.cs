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
        "the other playback tools need a running player (error 'player_not_running' otherwise). Use search/albums/artists to find music, " +
        "then play with album/artist/search or file paths. Tool errors start with a stable code such as 'not_found', 'invalid_state' or 'usage'.";

    public static Command Create(CommandRunner runner)
    {
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();
        var command = new Command("mcp",
            "Run an MCP (Model Context Protocol) server on stdin/stdout. Tools: play, pause, resume, toggle, stop, next, previous, seek, volume, " +
            "status, queue, queue_add, queue_clear, quit_player, search, albums, artists, library_stats. --backend/--device/--exclusive apply " +
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
                "Give exactly one of: files, album (optionally with artist), artist, search.",
                ([Description("Absolute paths of audio files, played in order.")] string[]? files = null,
                 [Description("Album title or id from 'albums'.")] string? album = null,
                 [Description("Artist name (all their tracks), or the album artist when album is given.")] string? artist = null,
                 [Description("Play every library track matching this text.")] string? search = null,
                 [Description("Turn the player's shuffle mode on for this queue.")] bool shuffle = false,
                 [Description("Shuffle seed for a reproducible order.")] int? seed = null,
                 CancellationToken ct = default) => Guard(() => SetQueueAsync(files, album, artist, search, shuffle, seed, ct)));
            yield return Simple("pause", "Pause playback.");
            yield return Simple("resume", "Resume (or start) playback of the current queue entry.");
            yield return Simple("toggle", "Pause when playing, otherwise resume.");
            yield return Simple("stop", "Stop and rewind the current track (the queue is kept).");
            yield return Simple("next", "Play the next queue entry. Error 'invalid_state' at the end of the queue.");
            yield return Simple("previous", "Play the previous queue entry. Error 'invalid_state' on the first entry.", "prev");
            yield return Tool("seek", "Seek within the current track. Absolute positions beyond the track are an error; relative ones are clamped.",
                ([Description("Seconds (90), mm:ss (1:30), h:mm:ss, or relative: +10, -10, +0:30.")] string position, CancellationToken ct = default)
                    => Guard(() =>
                    {
                        if (!PlayerSupport.TryParseSeek(position, out var seconds, out var relative, out var error))
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
            yield return Tool("queue_add", "Append files or library tracks to the queue (starts the player, stopped, if needed).",
                (string[]? files = null, string? album = null, string? artist = null, string? search = null, bool shuffle = false, int? seed = null,
                 CancellationToken ct = default) => Guard(() => AddAsync(files, album, artist, search, shuffle, seed, ct)));
            yield return Simple("queue_clear", "Stop playback and empty the queue.", "queue.clear");
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
            yield return Tool("search", "Search the music library: artists, albums and tracks containing the text (case-insensitive substring; the whole text must appear as typed, not word by word).",
                ([Description("Text to find in titles, artists, albums, genres and file names. Use a single name or phrase.")] string text, int limit = 20) => Guard(() =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "Search text must not be empty.");
                    }

                    using var library = runner.OpenLibraryForRead(parseResult);
                    var result = library.Search(text, Paging(0, limit));
                    return Task.FromResult(ToElement(new SearchDto(text, [.. result.Artists.Select(ArtistDto.From)], [.. result.Albums.Select(AlbumDto.From)],
                        PageDto.From(result.Tracks, LibraryTrackDto.From))));
                }), readOnly: true);
            yield return Tool("albums", "List library albums, optionally by one artist.",
                (string? artist = null, bool hiresOnly = false, int offset = 0, int limit = 50) => Guard(() =>
                {
                    using var library = runner.OpenLibraryForRead(parseResult);
                    var page = library.Albums(new AlbumQuery { Artist = artist, HiRes = hiresOnly ? true : null, Paging = Paging(offset, limit) });
                    return Task.FromResult(ToElement(PageDto.From(page, AlbumDto.From)));
                }), readOnly: true);
            yield return Tool("artists", "List library artists.", (int offset = 0, int limit = 100) => Guard(() =>
            {
                using var library = runner.OpenLibraryForRead(parseResult);
                return Task.FromResult(ToElement(PageDto.From(library.Artists(new ArtistQuery { Paging = Paging(offset, limit) }), ArtistDto.From)));
            }), readOnly: true);
            yield return Tool("library_stats", "Library totals: tracks, albums, artists, formats, hi-res counts.", () => Guard(() =>
            {
                using var library = runner.OpenLibraryForRead(parseResult);
                return Task.FromResult(ToElement(library.Stats()));
            }), readOnly: true);
        }

        private McpServerTool Simple(string name, string description, string? cmd = null)
            => Tool(name, description, (CancellationToken ct = default) => Guard(() => SendAsync(cmd ?? name, null, ct)));

        private static McpServerTool Tool(string name, string description, Delegate method, bool readOnly = false)
            => McpServerTool.Create(method, new McpServerToolCreateOptions
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                Destructive = false,
                OpenWorld = false,
            });

        private static Paging Paging(int offset, int limit)
            => MusicSpin.Core.Library.Paging.Validate(offset, limit) is { } error
                ? throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error.Replace("--", string.Empty, StringComparison.Ordinal))
                : new Paging(offset, limit);

        private static JsonElement ToElement<T>(T value) => ProtocolJson.ToElement(value);

        private async Task<JsonElement> SendAsync(string cmd, object? args, CancellationToken ct)
        {
            await using var client = await PlayerSupport.ConnectAsync(runner, parseResult, ct).ConfigureAwait(false);
            return await client.SendAsync(cmd, args, ct).ConfigureAwait(false);
        }

        private async Task<JsonElement> SetQueueAsync(string[]? files, string? album, string? artist, string? search, bool shuffle, int? seed, CancellationToken ct)
        {
            var (items, _) = Resolve(files, album, artist, search, shuffle: false, seed: shuffle ? null : seed);
            var used = QueueSource.PickSeed(shuffle, seed);
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, backend, device, exclusive, ct).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var status = await client.SendAsync("queue.set", new { items, play = true, shuffle = used is not null ? true : (bool?)null, seed = used }, ct)
                    .ConfigureAwait(false);
                return With(status, started, used, null);
            }
        }

        private async Task<JsonElement> AddAsync(string[]? files, string? album, string? artist, string? search, bool shuffle, int? seed, CancellationToken ct)
        {
            var (items, used) = Resolve(files, album, artist, search, shuffle, seed);
            var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, backend, device, exclusive, ct).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                var status = await client.SendAsync("queue.add", new { items }, ct).ConfigureAwait(false);
                return With(status, started, used, items.Count);
            }
        }

        private (List<QueueItem> Items, int? Seed) Resolve(string[]? files, string? album, string? artist, string? search, bool shuffle, int? seed)
        {
            if (QueueSource.ValidateValues(files, album, artist, search, shuffle, seed) is { } error)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error);
            }

            return QueueSource.Resolve(runner, parseResult, files, album, artist, search, shuffle, seed);
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
