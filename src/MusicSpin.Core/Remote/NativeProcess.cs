using System.Runtime.InteropServices;

namespace MusicSpin.Core.Remote;

/// <summary>Tiny libc helpers for the detached background player (Linux/macOS only; no-ops elsewhere).</summary>
public static unsafe class NativeProcess
{
    private static readonly nint Libc = LoadLibc();

    /// <summary>
    /// Starts a new session (<c>setsid</c>) so the player has no controlling terminal: closing the terminal or the
    /// agent's shell (SIGHUP) and Ctrl+C in it no longer reach it. Returns false when unsupported or it failed.
    /// </summary>
    public static bool TryDetachFromTerminal()
    {
        if (Libc == 0 || !NativeLibrary.TryGetExport(Libc, "setsid", out var setsid))
        {
            return false;
        }

        return ((delegate* unmanaged<int>)setsid)() != -1;
    }

    /// <summary>Effective user id, or -1 when unknown (Windows).</summary>
    public static int EffectiveUserId()
    {
        if (Libc == 0 || !NativeLibrary.TryGetExport(Libc, "geteuid", out var geteuid))
        {
            return -1;
        }

        return (int)((delegate* unmanaged<uint>)geteuid)();
    }

    private static nint LoadLibc()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        foreach (var name in new[] { "libc.so.6", "libc.so", "/usr/lib/libSystem.B.dylib", "libc" })
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }
}
