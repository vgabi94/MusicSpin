using System.CommandLine;
using MusicSpin.Core;
using MusicSpin.Core.Library;

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
        var search = Many("--search", "Search text, same syntax as 'music-spin search' (words, \"phrases\", artist:/album:/title:/genre:/year:). Repeat for several (any).");
        var hires = new Option<bool>("--hires") { Description = "Only hi-res tracks." };
        var backend = Options.Backend();
        var device = Options.Device();
        var exclusive = Options.Exclusive();

        var command = new Command("radio",
            "Endless random playback of the library in the background player; filters narrow it (OR within a filter, AND across filters). " +
            "No track repeats within the last 100 (or pool size - 1); the same artist/album is not played twice in a row when there is a choice. " +
            "next/prev/pause/stop/status work as usual; 'play', 'queue clear' or 'load' end radio. Nothing matches: exit 12.")
        {
            artist, album, genre, year, years, format, search, hires, backend, device, exclusive,
        };
        command.Validators.Add(r =>
        {
            foreach (var text in Options.Get(r, years) ?? [])
            {
                if (YearRange.TryParse(text, out var error) is null)
                {
                    r.AddError($"--years: {error}");
                }
            }

            foreach (var (name, option) in new[] { ("--artist", artist), ("--album", album), ("--genre", genre), ("--search", search) })
            {
                if ((Options.Get(r, option) ?? []).Any(string.IsNullOrWhiteSpace))
                {
                    r.AddError($"{name} must not be empty.");
                }
            }
        });

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
            };
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
        int poolSize;
        using (var library = runner.OpenLibraryForRead(parseResult))
        {
            poolSize = library.RadioPool(filter).Count;
        }

        if (poolSize == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.NotFound, filter.IsEmpty
                ? "The library is empty. Add music with 'music-spin library add <folder>' and 'music-spin library scan'."
                : $"No library tracks match the radio filters ({filter.Describe()}).");
        }

        var (client, started) = await PlayerSupport.ConnectOrStartAsync(runner, parseResult, backend, device, exclusive, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return (await client.SendAsync("radio", new { filter, play = true }, cancellationToken).ConfigureAwait(false), started);
        }
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
