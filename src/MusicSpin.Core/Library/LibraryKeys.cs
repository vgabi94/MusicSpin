using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MusicSpin.Core.Library;

/// <summary>Normalisation and id helpers shared by the scanner, indexer and queries.</summary>
public static class LibraryKeys
{
    public const string VariousArtists = "Various Artists";
    public const string UnknownArtist = "Unknown Artist";
    public const string UnknownAlbum = "Unknown Album";

    /// <summary>True on file systems that are case-insensitive by default (Windows, macOS).</summary>
    public static bool CaseInsensitivePaths { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparison PathComparison => CaseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Absolute, separator-trimmed path.</summary>
    public static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Stable id for a path. Hashed because LiteDB index keys must be &lt; 1023 bytes and real paths
    /// (deep folders, unicode names) can exceed that.
    /// </summary>
    public static string PathId(string normalizedPath)
        => Hash(CaseInsensitivePaths ? normalizedPath.ToUpperInvariant() : normalizedPath);

    /// <summary>True when <paramref name="path"/> equals or is inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (path.Equals(folder, PathComparison))
        {
            return true;
        }

        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>Case/whitespace-insensitive key for artist/album names ("The  Beatles" == "the beatles").</summary>
    public static string NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        var space = false;
        foreach (var c in name.Trim().Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }

        var key = sb.ToString();
        return key is "various" or "various artist" or "va" or "v.a." or "v/a" ? "various artists" : key;
    }

    public static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    public static string AlbumId(string albumArtistKey, string albumKey) => Hash("album\u001f" + albumArtistKey + "\u001f" + albumKey);

    public static string ArtistId(string artistKey) => Hash("artist\u001f" + artistKey);

    /// <summary>Picks the most common spelling of a name (ties: first seen).</summary>
    public static string DisplayName(IEnumerable<string?> names, string fallback)
        => names.Where(n => !string.IsNullOrWhiteSpace(n))
               .Select(n => n!.Trim())
               .GroupBy(n => n, StringComparer.Ordinal)
               .OrderByDescending(g => g.Count())
               .Select(g => g.Key)
               .FirstOrDefault() ?? fallback;

    internal static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
