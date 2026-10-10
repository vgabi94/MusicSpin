namespace MusicSpin.Core.Settings;

/// <summary>
/// Reads small JSON files (settings.json, queue.json, player.json) that another process may be replacing at that moment.
/// Writers publish with a temp file + rename; on Windows the replace briefly holds the destination, so an open that lands
/// in that window fails with a sharing violation (or access denied on a pending delete). Readers open with the widest
/// share mode and retry such conflicts a few times before giving up.
/// </summary>
internal static class SharedFile
{
    public const int Attempts = 10;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Opens <paramref name="path"/> for reading, retrying transient sharing conflicts.</summary>
    /// <param name="onRetry">Test hook, called with the attempt number before each retry instead of sleeping.</param>
    /// <exception cref="FileNotFoundException">and other IO errors that are not transient, or the last conflict.</exception>
    public static FileStream OpenRead(string path, Action<int>? onRetry = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex, path))
            {
                if (onRetry is null)
                {
                    Thread.Sleep(RetryDelay);
                }
                else
                {
                    onRetry(attempt);
                }
            }
        }
    }

    public static string ReadAllText(string path, Action<int>? onRetry = null)
    {
        using var stream = OpenRead(path, onRetry);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsTransient(Exception ex, string path) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => false,

        // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) on Windows; .NET's FileShare emulation on Unix.
        IOException io => (io.HResult & 0xFFFF) is 32 or 33 || !OperatingSystem.IsWindows(),

        // A file being replaced is "delete pending" for a moment; a real permission problem stays and fails at the end.
        UnauthorizedAccessException => File.Exists(path),
        _ => false,
    };
}
