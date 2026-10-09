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
    /// Lookup key for a normalized path (tracks: unique index <see cref="TrackRecord.PathKey"/>; folder and scan issue ids).
    /// Upper-cased first where file systems are case-insensitive by default. Hashed because LiteDB index keys must be
    /// &lt; 1023 bytes and real paths (deep folders, unicode names) can exceed that.
    /// </summary>
    public static string PathKey(string normalizedPath)
        => Hash(CaseInsensitivePaths ? normalizedPath.ToUpperInvariant() : normalizedPath);

    /// <summary>
    /// A new track/playlist id: a UUIDv7 (RFC 9562; 48-bit Unix-millisecond timestamp + 74 random bits) in the lowercase
    /// "D" format (<c>0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e</c>). Time-ordered, so new ids append at the end of the
    /// database index and sort by creation time.
    /// </summary>
    public static string NewId(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp).ToString("D");

    /// <summary>
    /// Deterministic id derived from a name (albums, artists): a name-based UUIDv8 (RFC 9562 section 5.8 / appendix B.2: the
    /// first 128 bits of SHA-256, with the version and variant bits set) in the lowercase "D" format.
    /// </summary>
    public static string NameId(string name)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true).ToString("D");
    }

    /// <summary>True for an id in the current format: a GUID in the "D" format (any case).</summary>
    public static bool IsId(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    /// <summary>True for a 32-hex-digit id from MusicSpin before library schema 3 (path/name hashes).</summary>
    public static bool IsLegacyId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Validates an id given by a user/agent and returns it in canonical (lowercase "D") form.
    /// </summary>
    /// <exception cref="MusicSpinException">InvalidArgument: an old 32-hex id, or not a GUID.</exception>
    public static string ParseId(string value, string what)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (IsId(trimmed))
        {
            return trimmed.ToLowerInvariant();
        }

        throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, IsLegacyId(trimmed)
            ? $"'{trimmed}' is an old-format {what} id (32 hex digits, before library schema 3). Ids are GUIDs now " +
              "(e.g. 0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e); get the current id from 'music-spin tracks/albums/search --json'."
            : $"'{trimmed}' is not a valid {what} id. Ids are GUIDs in the form 0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e.");
    }

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

    /// <summary>Album id: <see cref="NameId"/> of the album artist key and title key (stable while those tags don't change).</summary>
    public static string AlbumId(string albumArtistKey, string albumKey) => NameId("album\u001f" + albumArtistKey + "\u001f" + albumKey);

    /// <summary>Artist id: <see cref="NameId"/> of the artist key.</summary>
    public static string ArtistId(string artistKey) => NameId("artist\u001f" + artistKey);

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
