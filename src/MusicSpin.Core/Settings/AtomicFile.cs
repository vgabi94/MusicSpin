using System.Runtime.InteropServices;

namespace MusicSpin.Core.Settings;

/// <summary>Publishes a fully written temp file under its final name only if that name is still free, atomically.</summary>
internal static partial class AtomicFile
{
    private const int EEXIST = 17; // Linux and macOS

    /// <summary>
    /// Moves <paramref name="temp"/> to <paramref name="destination"/> unless the destination exists; false (temp left in
    /// place) when it does. Atomic: of several processes racing, exactly one wins. <c>File.Move(overwrite: false)</c> is
    /// not enough on Unix, where .NET checks for the destination and then renames, so two racers can both "win".
    /// </summary>
    public static bool TryPublish(string temp, string destination)
        => TryPublish(temp, destination, OperatingSystem.IsWindows() ? null : NativeLink);

    /// <param name="link">link(2): 0 on success, else errno (null = no hard links: Windows).</param>
    internal static bool TryPublish(string temp, string destination, Func<string, string, int>? link)
    {
        if (link is not null)
        {
            // link(2) fails with EEXIST atomically. File systems without hard links (FAT/exFAT USB drives) fall through.
            try
            {
                var error = link(temp, destination);
                if (error == 0)
                {
                    File.Delete(temp);
                    return true;
                }

                if (error == EEXIST)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // no libc link: use the best-effort move below
            }
        }

        try
        {
            File.Move(temp, destination, overwrite: false); // MoveFileEx without REPLACE_EXISTING: atomic on Windows
            return true;
        }
        catch (IOException) when (Path.Exists(destination)) // a file, or a folder squatting on the name (as link(2) reports)
        {
            return false;
        }
    }

    private static int NativeLink(string existingPath, string newPath)
        => Link(existingPath, newPath) == 0 ? 0 : Marshal.GetLastPInvokeError();

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);
}
