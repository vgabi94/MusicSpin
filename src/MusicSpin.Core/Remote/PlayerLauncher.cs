using System.Diagnostics;

namespace MusicSpin.Core.Remote;

/// <summary>A started (or reused) background player process.</summary>
public interface IPlayerProcess
{
    /// <summary>True once the process ended (e.g. it lost the start race to another player, or crashed).</summary>
    bool HasExited { get; }

    /// <summary>Exit code once exited, when known (null for a detached player that is not our child).</summary>
    int? ExitCode { get; }

    /// <summary>Process id, when known: tells whether this launch became the player or lost the start race.</summary>
    int? Id => null;
}

/// <summary>Finds the running player for a data folder or starts one, then connects.</summary>
public static class PlayerConnector
{
    /// <summary>
    /// Connects to the player; when none runs, calls <paramref name="launch"/> once and waits for it to accept connections.
    /// Concurrent callers may each launch a process: exactly one wins the data folder's lock, the others exit at once,
    /// and every caller ends up connected to the winner.
    /// </summary>
    /// <exception cref="MusicSpinException">PlayerUnavailable when no player answers within <paramref name="startTimeout"/>.</exception>
    public static async Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(
        PlayerEndpoint endpoint,
        Func<CancellationToken, Task<IPlayerProcess?>> launch,
        TimeSpan startTimeout,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(launch);
        try
        {
            return (await PlayerClient.ConnectAsync(endpoint, requestTimeout: requestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false), false);
        }
        catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.PlayerNotRunning)
        {
            // start one below
        }

        var process = await launch(cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(20);
        MusicSpinException? last = null;
        while (clock.Elapsed < startTimeout)
        {
            try
            {
                var client = await PlayerClient.ConnectAsync(endpoint, requestTimeout: requestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (process?.Id is not { } id)
                {
                    return (client, true);
                }

                try
                {
                    // Several callers may have launched a process at the same time; only the lock winner "started" it.
                    var hello = await client.HelloAsync(cancellationToken).ConfigureAwait(false);
                    return (client, hello.Pid == id);
                }
                catch
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (MusicSpinException ex) when (ex.Code is MusicSpinErrorCode.PlayerNotRunning or MusicSpinErrorCode.PlayerUnavailable)
            {
                last = ex;
            }

            // Our process died without another player taking over: fail fast with its exit code.
            // (Exit code 0 = it lost the start race; unknown = detached process, judged by the lock alone.)
            if (process is { HasExited: true } && process.ExitCode is not 0 && !PlayerHost.IsLockHeld(endpoint))
            {
                var code = process.ExitCode is { } c ? $" with code {c}" : string.Empty;
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
                    $"The background player exited{code} during startup. See {endpoint.LogFile}.");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 200));
        }

        throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
            $"The background player did not start within {startTimeout.TotalSeconds:0.#}s ({last?.Message}). See {endpoint.LogFile}.");
    }
}

/// <summary>Spawns <c>music-spin player run</c> as a detached background process.</summary>
public static class PlayerProcessLauncher
{
    /// <summary>Log files beyond this size are rotated to <c>player.log.1</c> when a new player starts.</summary>
    public const long MaxLogBytes = 1024 * 1024;

    /// <summary>Set for the spawned player on Linux/macOS: its stdout/stderr already append to this log file.</summary>
    public const string LogEnvironmentVariable = "MUSICSPIN_PLAYER_LOG";

    /// <summary>
    /// Starts the current executable (single-file exe, apphost, or <c>dotnet music-spin.dll</c>) with
    /// <paramref name="arguments"/>, detached from this process: stdin from /dev/null (NUL), stdout/stderr appended to the
    /// player log, own session (the child calls setsid), so it survives the calling shell/agent exiting.
    /// </summary>
    public static IPlayerProcess Start(PlayerEndpoint endpoint, IReadOnlyList<string> arguments, string? executable = null, string? entryAssembly = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(arguments);
        var (exe, prefix) = ResolveCommand(executable, entryAssembly);
        PrepareLog(endpoint.LogFile);

        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in prefix.Concat(arguments))
            {
                info.ArgumentList.Add(a);
            }
        }
        else
        {
            // /bin/sh does the fd plumbing so nothing of ours (e.g. an agent's stdout pipe) is inherited by the player,
            // and backgrounds it: the shell exits at once, so the player is re-parented (init or a subreaper) instead of
            // staying our child. Tools that kill a whole process tree (e.g. an MCP client stopping `music-spin mcp`)
            // then leave the player alone. The player calls setsid itself (no controlling terminal, own process group).
            info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"\"$@\" </dev/null >>\"${LogEnvironmentVariable}\" 2>&1 & echo $!");
            info.ArgumentList.Add("music-spin-player");
            info.ArgumentList.Add(exe);
            foreach (var a in prefix.Concat(arguments))
            {
                info.ArgumentList.Add(a);
            }

            info.Environment[LogEnvironmentVariable] = endpoint.LogFile;
        }

        var process = Process.Start(info)
            ?? throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not start '{exe}'.");
        if (OperatingSystem.IsWindows())
        {
            // Close our ends: the player logs to its own file and must not keep our handles alive.
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
            return new SpawnedProcess(process);
        }

        using (process)
        {
            var line = process.StandardOutput.ReadLine();
            process.WaitForExit(5000);
            if (!int.TryParse(line, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
            {
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not start '{exe}' via /bin/sh.");
            }

            return new DetachedProcess(pid);
        }
    }

    private static (string Exe, string[] Prefix) ResolveCommand(string? executable, string? entryAssembly)
    {
        var exe = executable ?? Environment.ProcessPath
            ?? throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, "Cannot determine the MusicSpin executable path.");
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // `dotnet music-spin.dll ...`: re-run the same assembly.
            var dll = entryAssembly ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll))
            {
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, "Cannot determine the MusicSpin assembly to run.");
            }

            return (exe, [dll]);
        }

        return (exe, []);
    }

    private static void PrepareLog(string logFile)
    {
        try
        {
            var info = new FileInfo(logFile);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                File.Move(logFile, logFile + ".1", overwrite: true);
            }

            if (!File.Exists(logFile))
            {
                using (File.Create(logFile))
                {
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(logFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the player reports a not-writable data folder itself
        }
    }

    /// <summary>A player that is no longer our child: its exit code is unknown, only whether it still runs.</summary>
    private sealed class DetachedProcess : IPlayerProcess
    {
        private readonly Process? _process;

        public DetachedProcess(int pid)
        {
            Id = pid;
            try
            {
                _process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                _process = null; // already gone
            }
        }

        public int? Id { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process is null || _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int? ExitCode => null;
    }

    private sealed class SpawnedProcess(Process process) : IPlayerProcess
    {
        public int? Id { get; } = process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int? ExitCode => HasExited ? process.ExitCode : null;
    }
}
