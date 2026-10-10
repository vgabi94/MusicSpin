using MusicSpin.Core.Settings;

namespace MusicSpin.Tests.Shared;

internal static class TestFiles
{
    /// <summary>Reads a file another thread/process may be replacing; null when it is missing or stays busy (poll again).</summary>
    public static string? TryReadShared(string path)
    {
        try
        {
            return SharedFile.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
