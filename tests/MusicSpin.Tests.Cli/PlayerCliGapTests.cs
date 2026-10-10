using System.Net.Sockets;
using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Background-player CLI paths: output options handed to a new player, settings that cannot be saved,
/// status text for paused/error/skipped states, and a player that never answers.</summary>
public sealed class PlayerCliGapTests : IAsyncDisposable
{
    private static readonly string[] Long = PlayerCliTests.Long;
    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    [Fact]
    public async Task Device_and_exclusive_reach_the_new_player_and_show_in_its_status()
    {
        PlayerLaunchRequest? seen = null;
        _cli.Launcher = (request, ct) =>
        {
            seen = request;
            return _cli.LaunchInProcess(request, ct);
        };

        var play = await Ok("play", Long[0], "--backend", "null", "--device", "USB DAC", "--exclusive");

        Assert.NotNull(seen);
        Assert.Equal(["player", "run", "--data-dir", _cli.DataDir, "--backend", "null", "--device", "USB DAC", "--exclusive"], seen.Arguments);
        Assert.Equal(("USB DAC", true, "null"), (seen.Device, seen.Exclusive, seen.Backend));
        var output = play.GetProperty("output");
        Assert.Equal("USB DAC", output.GetProperty("device").GetString());
        Assert.True(output.GetProperty("exclusive").GetBoolean());

        var text = await _cli.RunAsync("status");
        Assert.Contains("  Output: null (realtime) · USB DAC · exclusive", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_device_options_the_player_gets_neither_flag()
    {
        PlayerLaunchRequest? seen = null;
        _cli.Launcher = (request, ct) =>
        {
            seen = request;
            return _cli.LaunchInProcess(request, ct);
        };

        await Ok("play", Long[0], "--backend", "null");

        Assert.Equal(["player", "run", "--data-dir", _cli.DataDir, "--backend", "null"], seen!.Arguments);
        var text = await _cli.RunAsync("status");
        Assert.Contains("· default device · shared", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Modes_that_cannot_be_saved_still_change_the_player_and_warn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs POSIX permissions and a non-root user");
        await Ok("play", Long[0], "--backend", "null");
        var settings = Path.Combine(_cli.DataDir, "settings.json");
        var before = File.Exists(settings) ? await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken) : null;
        var mode = File.GetUnixFileMode(_cli.DataDir);
        File.SetUnixFileMode(_cli.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var status = await Ok("repeat", "all");

            Assert.Equal("all", status.GetProperty("repeat").GetString());
            Assert.StartsWith("Not saved as the default in settings.json: Data folder", status.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);

            var text = await _cli.RunAsync("shuffle", "on");
            Assert.Equal(0, text.ExitCode);
            Assert.Contains("music-spin shuffle: warning: Not saved as the default in settings.json", text.Err, StringComparison.Ordinal);
            Assert.Contains("Shuffle: on", text.Out, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(_cli.DataDir, mode);
        }

        Assert.Equal(before, File.Exists(settings) ? await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken) : null);
    }

    [Fact]
    public async Task Status_text_shows_paused_skipped_files_and_the_last_error()
    {
        var missing = Path.GetFullPath("/music/bg/gone.flac");
        await Ok("play", Long[0], missing, Long[1], "--backend", "null");
        await Ok("next"); // skips the missing entry
        await Ok("pause");

        var text = await _cli.RunAsync("status");

        Assert.Equal(0, text.ExitCode);
        var lines = text.Lines();
        Assert.StartsWith("Paused  [3/3]  ", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("Skipped 1 unplayable file(s), e.g. " + missing, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("Last error", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Status_text_shows_the_last_error_after_a_device_failure()
    {
        var faulty = new FaultyOutputProvider(new MusicSpin.Core.Output.NullAudioOutputProvider(realtime: true)) { FailStarts = 1 };
        await using var cli = new CliHarness { Decoders = _cli.Decoders, Outputs = _ => faulty };
        Assert.Equal(ExitCodes.AudioDeviceError, (await cli.RunAsync("play", Long[0], "--backend", "null", "--json")).ExitCode);

        var text = await cli.RunAsync("status");

        Assert.Equal(0, text.ExitCode);
        Assert.StartsWith("Stopped  [1/1]", text.Lines()[0], StringComparison.Ordinal);
        Assert.Contains(text.Lines(), l => l.StartsWith("Last error: ", StringComparison.Ordinal) && l.Contains("device busy", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_that_never_answers_is_exit_14_for_control_commands_and_stop()
    {
        await using var cli = new CliHarness { PlayerRequestTimeout = TimeSpan.FromMilliseconds(300) };
        Directory.CreateDirectory(cli.DataDir);
        await using var silent = ListenWithoutAnswering(PlayerEndpoint.For(cli.DataDir));

        foreach (var args in new[] { new[] { "status" }, ["pause"], ["player", "stop"] })
        {
            var result = await cli.RunAsync([.. args, "--json"]);

            Assert.Equal(ExitCodes.PlayerUnavailable, result.ExitCode);
            var json = result.Json();
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Equal("player_unavailable", json.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("did not answer", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        var text = await cli.RunAsync("status");
        Assert.Equal(ExitCodes.PlayerUnavailable, text.ExitCode);
        Assert.StartsWith("music-spin status: ", text.Err, StringComparison.Ordinal);
        Assert.Empty(text.Out);
    }

    private static IAsyncDisposable ListenWithoutAnswering(PlayerEndpoint endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new System.IO.Pipes.NamedPipeServerStream(endpoint.Address, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            return new PipeServers(endpoint.Address, pipe);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
        socket.Listen(8);
        return new Holder(socket.Dispose);
    }

    /// <summary>Keeps accepting pipe clients (one per command) without ever answering them.</summary>
    private sealed class PipeServers : IAsyncDisposable
    {
        private readonly List<System.IO.Pipes.NamedPipeServerStream> _pipes = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public PipeServers(string name, System.IO.Pipes.NamedPipeServerStream first)
        {
            _pipes.Add(first);
            _loop = Task.Run(async () =>
            {
                var current = first;
                while (!_stop.IsCancellationRequested)
                {
                    await current.WaitForConnectionAsync(_stop.Token);
                    current = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    lock (_pipes)
                    {
                        _pipes.Add(current);
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            lock (_pipes)
            {
                foreach (var p in _pipes)
                {
                    p.Dispose();
                }
            }

            _stop.Dispose();
        }
    }

    private sealed class Holder(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}
