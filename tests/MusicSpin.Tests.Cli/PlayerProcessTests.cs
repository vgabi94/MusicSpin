using System.Diagnostics;
using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>
/// End-to-end: the real <c>music-spin</c> apphost from the test output folder, a real detached background player,
/// real FFmpeg decoding and the null (realtime) audio backend. Every command is a separate process, like an AI agent
/// running shell commands.
/// </summary>
public sealed class PlayerProcessTests : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("ms-proc-").FullName;
    private readonly string _data;
    private readonly string[] _tracks;
    private readonly HashSet<int> _players = [];

    public PlayerProcessTests()
    {
        _data = Path.Combine(_root, ".usrdata");
        _tracks = [.. Enumerable.Range(1, 3).Select(i => TestAudio.WriteWav24(Path.Combine(_root, $"track{i}.wav"), 8_000, 1, 8_000 * 30))];
    }

    private static string Exe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "music-spin.exe" : "music-spin");

    private static void RequireEnvironment()
    {
        Assert.SkipUnless(File.Exists(Exe), $"apphost not found: {Exe}");
        Assert.SkipUnless(FFmpegRuntime.IsAvailable, $"FFmpeg {FFmpegRuntime.ExpectedFFmpegVersion} not available");
    }

    private sealed record Run(int ExitCode, string Out, string Err, TimeSpan Elapsed)
    {
        public JsonElement Data => JsonDocument.Parse(Out).RootElement.GetProperty("data");

        public JsonElement Error => JsonDocument.Parse(Out).RootElement.GetProperty("error");
    }

    private async Task<Run> RunAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(_data);
        info.Environment.Remove("MUSICSPIN_PLAYER_LOG");

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(CommandTimeout);
        try
        {
            // Reading to EOF proves the background player did not inherit our pipes.
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return new Run(process.ExitCode, (await stdout).Trim(), await stderr, watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"music-spin {string.Join(' ', args)} did not return within {CommandTimeout}");
        }
    }

    private async Task<JsonElement> Ok(params string[] args)
    {
        var run = await RunAsync([.. args, "--json"]);
        Assert.True(run.ExitCode == 0, $"music-spin {string.Join(' ', args)}: exit {run.ExitCode}: {run.Out} {run.Err}");
        return run.Data;
    }

    /// <summary>Reads player.log while the player may still have it open for writing (Windows needs FileShare.ReadWrite).</summary>
    private async Task<string> ReadLogAsync()
    {
        await using var stream = new FileStream(Path.Combine(_data, "player.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Everything needed to understand a failed player-process assertion: the run, player.json, player.log, endpoint.</summary>
    private async Task<string> DiagnoseAsync(Run run)
    {
        static string Safe(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"<{ex.GetType().Name}: {ex.Message}>";
            }
        }

        var log = File.Exists(Path.Combine(_data, "player.log")) ? await ReadLogAsync() : "<none>";
        var info = Path.Combine(_data, "player.json");
        var pipes = OperatingSystem.IsWindows()
            ? Safe(() => string.Join(", ", Directory.EnumerateFiles(@"\\.\pipe\").Select(Path.GetFileName).Where(n => n!.StartsWith("musicspin", StringComparison.OrdinalIgnoreCase))))
            : Safe(() => File.Exists(Path.Combine(_data, "player.sock")) ? "player.sock present" : "no player.sock");
        return $"""
            exit {run.ExitCode} after {run.Elapsed.TotalMilliseconds:0} ms
            stdout: {run.Out}
            stderr: {run.Err}
            player.json: {(File.Exists(info) ? Safe(() =>
            {
                using var reader = new StreamReader(new FileStream(info, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return reader.ReadToEnd();
            }) : "<none>")}
            endpoints: {pipes}
            player.log (tail): {log[Math.Max(0, log.Length - 3000)..]}
            """;
    }

    private async Task<int> PlayerPidAsync()
    {
        var status = await Ok("player", "status");
        Assert.True(status.GetProperty("running").GetBoolean(), status.ToString());
        var pid = status.GetProperty("pid").GetInt32();
        _players.Add(pid);
        return pid;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Background players for this data folder (Linux: /proc command lines).</summary>
    private int[] PlayerProcesses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        var found = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }

            try
            {
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0');
                if (cmd.Contains("run") && cmd.Contains("player") && cmd.Contains(_data) && !cmd.Contains("-c"))
                {
                    found.Add(pid);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return [.. found];
    }

    [Fact]
    public async Task Play_returns_immediately_and_separate_processes_control_the_player()
    {
        RequireEnvironment();
        var play = await RunAsync("play", _tracks[0], _tracks[1], "--backend", "null", "--json");
        Assert.True(play.ExitCode == 0, play.Out + play.Err);
        Assert.True(play.Elapsed < TimeSpan.FromSeconds(10), $"play took {play.Elapsed}");
        Assert.Equal("playing", play.Data.GetProperty("state").GetString());
        Assert.True(play.Data.GetProperty("playerStarted").GetBoolean());
        Assert.Equal("pcm_s24le", play.Data.GetProperty("track").GetProperty("codec").GetString());

        var pid = await PlayerPidAsync();
        Assert.NotEqual(Environment.ProcessId, pid);
        Assert.True(IsAlive(pid));
        if (OperatingSystem.IsLinux())
        {
            // Detached: own session (setsid) and re-parented away from the short-lived `play` process.
            var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat", TestContext.Current.CancellationToken);
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            Assert.Equal(pid, int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture));
            var parent = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.DoesNotContain("music-spin", await File.ReadAllTextAsync($"/proc/{parent}/cmdline", TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        if (OperatingSystem.IsWindows())
        {
            // Started by a helper that has exited: no live parent, so a process-tree kill of the caller cannot reach it.
            Assert.NotEqual(Environment.ProcessId, ParentProcessId(pid));
            Assert.False(ParentProcessId(pid) is { } parent && IsAlive(parent) && ProcessName(parent) == "music-spin");
        }

        Assert.Equal("paused", (await Ok("pause")).GetProperty("state").GetString());
        Assert.Equal("playing", (await Ok("toggle")).GetProperty("state").GetString());
        var next = await Ok("next");
        Assert.Equal(1, next.GetProperty("queueIndex").GetInt32());
        Assert.Equal(_tracks[1], next.GetProperty("track").GetProperty("path").GetString());
        Assert.InRange((await Ok("seek", "0:10")).GetProperty("positionSeconds").GetDouble(), 9.9, 11);
        Assert.Equal(ExitCodes.InvalidState, (await RunAsync("next", "--json")).ExitCode);
        Assert.Equal("stopped", (await Ok("stop")).GetProperty("state").GetString());

        var log = await ReadLogAsync();
        Assert.Contains($"pid {pid}", log, StringComparison.Ordinal);

        Assert.True((await Ok("quit")).GetProperty("stopped").GetBoolean());
        await PlayerTestKit.WaitUntilAsync(() => !IsAlive(pid), "player process exit");
        Assert.Equal(ExitCodes.PlayerNotRunning, (await RunAsync("status", "--json")).ExitCode);
        Assert.False(File.Exists(Path.Combine(_data, "player.json")));
        Assert.False(File.Exists(Path.Combine(_data, "player.sock")));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Kill_9_leaves_a_stale_endpoint_that_is_reported_and_recovered()
    {
        RequireEnvironment();
        await Ok("play", _tracks[0], "--backend", "null");
        var pid = await PlayerPidAsync();
        using (var victim = Process.GetProcessById(pid))
        {
            victim.Kill(); // SIGKILL: no cleanup
            await victim.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(Path.Combine(_data, "player.sock"))); // a named pipe disappears with its process
        }

        var status = await RunAsync("status", "--json");
        Assert.True(status.ExitCode == ExitCodes.PlayerNotRunning, await DiagnoseAsync(status));
        Assert.Contains($"pid {pid}", status.Error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(pid, (await Ok("player", "status")).GetProperty("stalePid").GetInt32());

        var replay = await Ok("play", _tracks[1], "--backend", "null");
        Assert.True(replay.GetProperty("playerStarted").GetBoolean());
        var newPid = await PlayerPidAsync();
        Assert.NotEqual(pid, newPid);
        await Ok("quit");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Racing_play_commands_start_exactly_one_player()
    {
        RequireEnvironment();
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => RunAsync("play", _tracks[i % 3], "--backend", "null", "--json")));
        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Out + r.Err));
        Assert.Single(runs, r => r.Data.GetProperty("playerStarted").GetBoolean());
        var pid = await PlayerPidAsync();
        if (OperatingSystem.IsLinux())
        {
            await PlayerTestKit.WaitUntilAsync(() => PlayerProcesses().Length == 1, "a single player process");
            Assert.Equal([pid], PlayerProcesses());
        }

        await Ok("quit");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Idle_player_exits_after_the_timeout_from_settings_json()
    {
        RequireEnvironment();
        Directory.CreateDirectory(_data);
        await File.WriteAllTextAsync(Path.Combine(_data, "settings.json"), """{ "playerIdleTimeoutSeconds": 3 }""", TestContext.Current.CancellationToken);
        var added = await Ok("queue", "add", _tracks[0], "--backend", "null");
        Assert.Equal("stopped", added.GetProperty("state").GetString());
        var pid = await PlayerPidAsync();
        Assert.Equal(3, (await Ok("player", "status")).GetProperty("idleTimeoutSeconds").GetDouble());
        await PlayerTestKit.WaitUntilAsync(() => !IsAlive(pid), "idle exit", timeoutMs: 15_000);
        Assert.Contains("stopped (idle)", await ReadLogAsync(), StringComparison.Ordinal);
        Assert.False((await Ok("player", "status")).GetProperty("running").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Sigterm_or_player_stop_shuts_the_player_down_cleanly_while_playing()
    {
        RequireEnvironment();
        await Ok("play", _tracks[0], "--backend", "null");
        var pid = await PlayerPidAsync();
        string reason;
        if (OperatingSystem.IsWindows())
        {
            // Windows has no SIGTERM, and console control events cannot be sent to a process on another (hidden) console.
            // The documented way to stop the player from outside is `music-spin player stop` (or `quit`).
            Assert.True((await Ok("player", "stop")).GetProperty("stopped").GetBoolean());
            reason = "stopped (quit)";
        }
        else
        {
            Assert.Equal(0, NativeKill(pid, 15));
            reason = "stopped (signal)";
        }

        await PlayerTestKit.WaitUntilAsync(() => !IsAlive(pid), "clean exit");
        Assert.False(File.Exists(Path.Combine(_data, "player.json")));
        Assert.Contains(reason, await ReadLogAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_with_captured_output_returns_promptly_and_the_callers_pipes_reach_eof()
    {
        // What an AI agent or CI step does: run `play` with stdin/stdout/stderr redirected and read both outputs to EOF.
        // EOF only comes once no process holds the write ends, so the still-running player must not have inherited them.
        RequireEnvironment();
        var run = await RunAsync("play", _tracks[0], "--backend", "null", "--json");
        Assert.True(run.ExitCode == 0, run.Out + run.Err);
        Assert.True(run.Elapsed < TimeSpan.FromSeconds(10), $"play with captured output took {run.Elapsed}");
        Assert.True(run.Data.GetProperty("playerStarted").GetBoolean());
        var pid = await PlayerPidAsync();
        Assert.True(IsAlive(pid), "the player keeps running after the caller's pipes closed");
        Assert.Contains("listening on", await ReadLogAsync(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Killing_the_mcp_server_process_tree_leaves_the_player_running()
    {
        // MCP clients stop a server by killing its whole process tree. The player it started must survive that.
        RequireEnvironment();
        var info = new ProcessStartInfo(Exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        foreach (var arg in new[] { "mcp", "--backend", "null", "--data-dir", _data })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment.Remove("MUSICSPIN_PLAYER_LOG");
        using var server = Process.Start(info)!;
        try
        {
            _ = server.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(CommandTimeout);
            async Task<JsonElement> Request(int id, string method, object parameters)
            {
                await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), cts.Token);
                await server.StandardInput.FlushAsync(cts.Token);
                while (await server.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                {
                    var message = JsonDocument.Parse(line).RootElement;
                    if (message.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
                    {
                        return message;
                    }
                }

                throw new InvalidOperationException("the MCP server closed its output");
            }

            await Request(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""".AsMemory(), cts.Token);
            var play = await Request(2, "tools/call", new { name = "play", arguments = new { files = _tracks } });
            Assert.False(play.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean(), play.ToString());
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var pid = await PlayerPidAsync();
        Assert.True(IsAlive(pid));
        Assert.Equal("playing", (await Ok("status")).GetProperty("state").GetString());
        await Ok("quit");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task An_mcp_server_whose_client_closes_stdin_mid_request_exits_0_and_the_player_keeps_working()
    {
        // An agent that disconnects closes the server's stdin, possibly while a tool call is still running.
        RequireEnvironment();
        var info = new ProcessStartInfo(Exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        foreach (var arg in new[] { "mcp", "--backend", "null", "--data-dir", _data })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment.Remove("MUSICSPIN_PLAYER_LOG");
        using var server = Process.Start(info)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(CommandTimeout);
        var stderr = server.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
            }).AsMemory(), cts.Token);
            await server.StandardInput.FlushAsync(cts.Token);
            Assert.Contains("\"id\":1", await server.StandardOutput.ReadLineAsync(cts.Token), StringComparison.Ordinal);
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""".AsMemory(), cts.Token);
            await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "play", arguments = new { files = _tracks } },
            }).AsMemory(), cts.Token);
            server.StandardInput.Close(); // the client is gone; the play call may still be in flight

            _ = server.StandardOutput.ReadToEndAsync(cts.Token); // drain whatever it still answers
            await server.WaitForExitAsync(cts.Token);
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
        }

        Assert.True(server.ExitCode == 0, $"exit {server.ExitCode}: {await stderr}");
        var status = await RunAsync("status", "--json");
        if (status.ExitCode == 0)
        {
            // The in-flight play may have started a player before the server noticed EOF: it must be usable and stoppable.
            Assert.Equal(3, status.Data.GetProperty("queueLength").GetInt32());
            await Ok("quit");
        }
        else
        {
            Assert.Equal(ExitCodes.PlayerNotRunning, status.ExitCode);
        }
    }

    [Fact]
    public async Task Mcp_server_process_controls_the_same_background_player()
    {
        RequireEnvironment();
        var transport = new ModelContextProtocol.Client.StdioClientTransport(new ModelContextProtocol.Client.StdioClientTransportOptions
        {
            Command = Exe,
            Arguments = ["mcp", "--backend", "null", "--data-dir", _data],
            WorkingDirectory = _root,
            Name = "music-spin",
        });
        await using (var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken))
        {
            async Task<JsonElement> Call(string tool, Dictionary<string, object?>? args = null)
            {
                var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
                var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
                Assert.False(result.IsError == true, text);
                return JsonDocument.Parse(text).RootElement;
            }

            var play = await Call("play", new() { ["files"] = _tracks });
            Assert.Equal("playing", play.GetProperty("state").GetString());
            Assert.True(play.GetProperty("playerStarted").GetBoolean());
        }

        // The player outlives the MCP server and is shared with the CLI.
        var status = await Ok("status");
        Assert.Equal("playing", status.GetProperty("state").GetString());
        Assert.Equal(3, status.GetProperty("queueLength").GetInt32());
        await Ok("quit");
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Windows: the parent process id recorded at creation (it may have exited since).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? ParentProcessId(int pid)
    {
        using var p = Process.GetProcessById(pid);
        var info = new nint[6]; // PROCESS_BASIC_INFORMATION; InheritedFromUniqueProcessId is the last field
        return NtQueryInformationProcess(p.Handle, 0, info, info.Length * IntPtr.Size, out _) == 0 ? (int)info[5] : null;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, [System.Runtime.InteropServices.Out] nint[] info, int length, out int returned);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int signal);

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(Exe) && Directory.Exists(_data))
        {
            try
            {
                await RunAsync("quit", "--json");
            }
            catch (TimeoutException)
            {
            }
        }

        foreach (var pid in PlayerProcesses().Concat(_players.Where(IsAlive)).Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (ArgumentException)
            {
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
