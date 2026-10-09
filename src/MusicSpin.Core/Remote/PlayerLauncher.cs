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

    /// <summary>Set for the spawned player: its stdout/stderr already append to this log file.</summary>
    public const string LogEnvironmentVariable = "MUSICSPIN_PLAYER_LOG";

    /// <summary>
    /// Windows only: first argument of the short-lived helper that starts the player (see <see cref="RunWindowsSpawnHelper"/>).
    /// The CLI's entry point hands it over before normal command parsing.
    /// </summary>
    public const string WindowsSpawnVerb = "__spawn-player";

    /// <summary>
    /// Starts the current executable (single-file exe, apphost, or <c>dotnet music-spin.dll</c>) with
    /// <paramref name="arguments"/>, detached from this process: stdin from /dev/null (NUL), stdout/stderr appended to the
    /// player log, nothing else of ours inherited, not our child, so it survives the calling shell/agent exiting.
    /// </summary>
    public static IPlayerProcess Start(PlayerEndpoint endpoint, IReadOnlyList<string> arguments, string? executable = null, string? entryAssembly = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(arguments);
        var (exe, prefix) = ResolveCommand(executable, entryAssembly);
        PrepareLog(endpoint.LogFile);
        return OperatingSystem.IsWindows() ? StartWindows(endpoint, exe, prefix, arguments) : StartUnix(endpoint, exe, prefix, arguments);
    }

    private static DetachedProcess StartUnix(PlayerEndpoint endpoint, string exe, string[] prefix, IReadOnlyList<string> arguments)
    {
        // /bin/sh does the fd plumbing so nothing of ours (e.g. an agent's stdout pipe) is inherited by the player,
        // and backgrounds it: the shell exits at once, so the player is re-parented (init or a subreaper) instead of
        // staying our child. Tools that kill a whole process tree (e.g. an MCP client stopping `music-spin mcp`)
        // then leave the player alone. The player calls setsid itself (no controlling terminal, own process group).
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"\"$@\" </dev/null >>\"${LogEnvironmentVariable}\" 2>&1 & echo $!");
        info.ArgumentList.Add("music-spin-player");
        info.ArgumentList.Add(exe);
        foreach (var a in prefix.Concat(arguments))
        {
            info.ArgumentList.Add(a);
        }

        info.Environment[LogEnvironmentVariable] = endpoint.LogFile;
        using var process = Process.Start(info)
            ?? throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not start '{exe}'.");
        var line = process.StandardOutput.ReadLine();
        process.WaitForExit(5000);
        if (!int.TryParse(line, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not start '{exe}' via /bin/sh.");
        }

        return new DetachedProcess(pid);
    }

    /// <summary>
    /// Windows counterpart of the <c>/bin/sh ... &amp;</c> trick: a short-lived helper (this executable with
    /// <see cref="WindowsSpawnVerb"/>) starts the player and exits, so the player's parent is gone and a process-tree kill
    /// of the caller (e.g. an MCP client stopping <c>music-spin mcp</c>) does not reach it. Both are started with
    /// <c>CreateProcessW</c>, inheriting only the handles they are given, without a window, in their own process group, and
    /// outside the caller's job object when the job allows it.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DetachedProcess StartWindows(PlayerEndpoint endpoint, string exe, string[] prefix, IReadOnlyList<string> arguments)
    {
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        string? line;
        try
        {
            using (var nul = WindowsDetachedProcess.OpenNullInput())
            {
                WindowsDetachedProcess.Start(exe, [.. prefix, WindowsSpawnVerb, endpoint.LogFile, exe, .. prefix, .. arguments], endpoint.DataDirectory,
                    nul, pipe.ClientSafePipeHandle, pipe.ClientSafePipeHandle,
                    WindowsDetachedProcess.CreateNoWindow | WindowsDetachedProcess.CreateNewProcessGroup);
            }

            pipe.DisposeLocalCopyOfClientHandle(); // EOF once the helper exits
            using var reader = new StreamReader(pipe);
            line = reader.ReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not start '{exe}': {ex.Message}", ex);
        }

        if (!int.TryParse(line, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not start '{exe}': {line ?? "the start helper exited"}.");
        }

        return new DetachedProcess(pid);
    }

    /// <summary>
    /// Windows only: the helper process. Arguments: log file, executable, then the player's arguments. Starts the player
    /// with stdin from NUL and stdout/stderr appended to the log, prints its pid (or an error) and returns at once.
    /// </summary>
    public static int RunWindowsSpawnHelper(IReadOnlyList<string> args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (!OperatingSystem.IsWindows() || args.Count < 2)
        {
            output.WriteLine($"usage: {WindowsSpawnVerb} <log file> <executable> [arguments...] (Windows only)");
            return 2;
        }

        var logFile = args[0];
        try
        {
            Environment.SetEnvironmentVariable(LogEnvironmentVariable, logFile); // inherited by the player
            using var nul = WindowsDetachedProcess.OpenNullInput();
            using var log = WindowsDetachedProcess.OpenAppend(logFile);
            var pid = WindowsDetachedProcess.Start(args[1], args.Skip(2), Path.GetDirectoryName(logFile), nul, log, log,
                WindowsDetachedProcess.CreateNoWindow | WindowsDetachedProcess.CreateNewProcessGroup);
            output.WriteLine(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            output.WriteLine(ex.Message);
            return 1;
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
}
