using System.Text.Json;
using MusicSpin.Tests.Shared;
using MusicSpin.Core;

namespace MusicSpin.Tests.Cli;

/// <summary>#66: 'queue add' of only unplayable files leaves entries with no current one; editing that queue must work.</summary>
public sealed class QueueNoCurrentEntryCliTests : IAsyncDisposable
{
    private static readonly string[] Gone = [.. new[] { "a", "b", "c" }.Select(n => Path.GetFullPath($"/music/gone/{n}.flac"))];

    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    private async Task<(int ExitCode, string Code, string Message)> Fails(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.NotEqual(0, result.ExitCode);
        var error = result.Json().GetProperty("error");
        return (result.ExitCode, error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    private async Task<List<string?>> QueuePaths()
        => [.. (await Ok("queue")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("path").GetString())];

    private async Task DeadQueueAsync()
    {
        var added = await Ok(["queue", "add", .. Gone, "--backend", "null"]);
        Assert.Equal(3, added.GetProperty("queueLength").GetInt32());
        Assert.False(added.TryGetProperty("track", out var track) && track.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Queue_move_and_remove_edit_a_queue_without_a_current_entry()
    {
        await DeadQueueAsync();

        await Ok("queue", "move", "1", "3");
        Assert.Equal([Gone[1], Gone[2], Gone[0]], await QueuePaths());
        await Ok("queue", "remove", "2");
        Assert.Equal([Gone[1], Gone[0]], await QueuePaths());
        Assert.Equal(ExitCodes.Usage, (await Fails("queue", "move", "1", "9")).ExitCode);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Transport_reports_stable_codes()
    {
        await DeadQueueAsync();

        var next = await Fails("next");
        Assert.Equal((ExitCodes.FileNotFound, "file_not_found"), (next.ExitCode, next.Code));
        var prev = await Fails("prev");
        Assert.Equal((ExitCodes.InvalidState, "invalid_state"), (prev.ExitCode, prev.Code));
        Assert.Contains("none of the 3 queued entries could be played", prev.Message, StringComparison.Ordinal);
        Assert.Equal(3, (await QueuePaths()).Count);
    }
}
