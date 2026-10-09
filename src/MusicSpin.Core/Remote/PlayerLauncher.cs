using System.Diagnostics;

namespace MusicSpin.Core.Remote;

/// <summary>A started (or reused) background player process.</summary>
public interface IPlayerProcess
{
    /// <summary>True once the process ended (e.g. it lost the start race to another player, or crashed).</summary>
    bool HasExited { get; }

    int? ExitCode { get; }
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
                return (await PlayerClient.ConnectAsync(endpoint, requestTimeout: requestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false), true);
            }
            catch (MusicSpinException ex) when (ex.Code is MusicSpinErrorCode.PlayerNotRunning or MusicSpinErrorCode.PlayerUnavailable)
            {
                last = ex;
            }

            // Our process died without another player taking over: fail fast with its exit code.
            if (process is { HasExited: true, ExitCode: not 0 } && !PlayerHost.IsLockHeld(endpoint))
            {
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
                    $"The background player exited with code {process.ExitCode} during startup. See {endpoint.LogFile}.");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 200));
        }

        throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
            $"The background player did not start within {startTimeout.TotalSeconds:0.#}s ({last?.Message}). See {endpoint.LogFile}.");
    }
}

/// <summary>Spawns <c>musicspin player run</c> as a detached background process.</summary>
public static class PlayerProcessLauncher
{
    /// <summary>Log files beyond this size are rotated to <c>player.log.1</c> when a new player starts.</summary>
    public const long MaxLogBytes = 1024 * 1024;

    /// <summary>Set for the spawned player on Linux/macOS: its stdout/stderr already append to this log file.</summary>
    public const string LogEnvironmentVariable = "MUSICSPIN_PLAYER_LOG";

    /// <summary>
    /// Starts the current executable (single-file exe, apphost, or <c>dotnet musicspin.dll</c>) with
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
            // /bin/sh does the fd plumbing so nothing of ours (e.g. an agent's stdout pipe) is inherited by the player.
            info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"exec \"$@\" </dev/null >>\"${LogEnvironmentVariable}\" 2>&1");
            info.ArgumentList.Add("musicspin-player");
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
        }

        return new SpawnedProcess(process);
    }

    private static (string Exe, string[] Prefix) ResolveCommand(string? executable, string? entryAssembly)
    {
        var exe = executable ?? Environment.ProcessPath
            ?? throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, "Cannot determine the MusicSpin executable path.");
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // `dotnet musicspin.dll ...`: re-run the same assembly.
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

    private sealed class SpawnedProcess(Process process) : IPlayerProcess
    {
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
