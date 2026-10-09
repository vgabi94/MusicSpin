using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicSpin.Core.Library;

/// <summary>Playlist file formats for import/export.</summary>
public enum PlaylistFormat
{
    /// <summary>Extended M3U, UTF-8 (<c>.m3u8</c>).</summary>
    M3u8,

    /// <summary>Extended M3U, Latin-1 when every entry fits, otherwise UTF-8 (<c>.m3u</c>).</summary>
    M3u,

    /// <summary>PLS version 2 (INI style, UTF-8).</summary>
    Pls,
}

/// <summary>One entry read from a playlist file.</summary>
/// <param name="Line">1-based line number in the file.</param>
/// <param name="Raw">The entry as written in the file.</param>
/// <param name="Candidates">Local paths to try, best first (as written, then URL-decoded). Empty for foreign absolute paths (e.g. <c>C:\...</c> on Linux).</param>
/// <param name="Display">A normalised form of the entry, stored when it cannot be resolved.</param>
internal sealed record ParsedPlaylistEntry(int Line, string Raw, IReadOnlyList<string> Candidates, string Display, string? Title, string? Artist, double? DurationSeconds);

internal sealed record ParsedPlaylist(PlaylistFormat Format, string Encoding, IReadOnlyList<ParsedPlaylistEntry> Entries, IReadOnlyList<string> Streams, IReadOnlyList<string> Warnings);

/// <summary>An entry to write to a playlist file.</summary>
internal sealed record PlaylistFileEntry(string Path, string? Title, string? Artist, double? DurationSeconds);

/// <summary>Reads and writes M3U / M3U8 / PLS files. Pure file-format logic; resolution against the library is done by <see cref="MusicLibrary"/>.</summary>
public static partial class PlaylistFiles
{
    /// <summary>Playlist files larger than this are rejected (a 100,000-entry M3U is a few MB).</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    private const int MaxWarnings = 50;

    public static string Extension(PlaylistFormat format) => format switch
    {
        PlaylistFormat.M3u => ".m3u",
        PlaylistFormat.Pls => ".pls",
        _ => ".m3u8",
    };

    public static string Name(PlaylistFormat format) => format.ToString().ToLowerInvariant();

    public static bool TryParseFormat(string? text, out PlaylistFormat format)
    {
        switch (text?.Trim().TrimStart('.').ToLowerInvariant())
        {
            case "m3u8":
                format = PlaylistFormat.M3u8;
                return true;
            case "m3u":
                format = PlaylistFormat.M3u;
                return true;
            case "pls":
                format = PlaylistFormat.Pls;
                return true;
            default:
                format = PlaylistFormat.M3u8;
                return false;
        }
    }

    /// <summary>The format implied by a file name (null when the extension is not a playlist extension).</summary>
    public static PlaylistFormat? FromExtension(string path)
        => TryParseFormat(Path.GetExtension(path), out var format) ? format : null;

    // ------------------------------------------------------------------ read

    /// <exception cref="MusicSpinException">FileNotFound; UnsupportedFormat (binary, too big, not a playlist); InvalidArgument (a folder).</exception>
    internal static ParsedPlaylist Read(string file)
    {
        var full = Path.GetFullPath(file);
        if (Directory.Exists(full))
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"'{full}' is a folder, not a playlist file.");
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"Playlist file not found: {full}");
            }

            if (info.Length > MaxFileBytes)
            {
                throw new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat,
                    $"'{full}' is {info.Length / (1024 * 1024)} MB; playlist files over {MaxFileBytes / (1024 * 1024)} MB are not imported.");
            }

            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"Cannot read '{full}': {ex.Message}", ex);
        }

        var (text, encoding) = Decode(bytes, FromExtension(full) == PlaylistFormat.M3u8);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat, $"'{full}' is not a playlist (binary data).");
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var firstLine = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        var format = FromExtension(full) ?? (firstLine.Equals("[playlist]", StringComparison.OrdinalIgnoreCase) ? PlaylistFormat.Pls : PlaylistFormat.M3u);
        if (format == PlaylistFormat.Pls && !firstLine.Equals("[playlist]", StringComparison.OrdinalIgnoreCase) && firstLine.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            format = PlaylistFormat.M3u; // misnamed file
        }

        var baseDir = Path.GetDirectoryName(full)!;
        return format == PlaylistFormat.Pls ? ReadPls(full, lines, baseDir, encoding) : ReadM3u(lines, baseDir, encoding, format);
    }

    /// <summary>BOM (UTF-8/16) first; otherwise strict UTF-8, falling back to Latin-1 (classic .m3u files).</summary>
    private static (string Text, string Encoding) Decode(byte[] bytes, bool expectUtf8)
    {
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8-bom");
        }

        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le");
        }

        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be");
        }

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), expectUtf8 ? "latin1 (invalid UTF-8)" : "latin1");
        }
    }

    private static ParsedPlaylist ReadM3u(string[] lines, string baseDir, string encoding, PlaylistFormat format)
    {
        var entries = new List<ParsedPlaylistEntry>();
        var streams = new List<string>();
        var warnings = new List<string>();
        (string? Title, string? Artist, double? Duration)? info = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    info = ParseExtInf(line["#EXTINF:".Length..]);
                    if (info is null)
                    {
                        Warn(warnings, $"line {i + 1}: malformed #EXTINF ignored");
                    }
                }

                continue; // #EXTM3U, #PLAYLIST, #EXTALB, ... and comments
            }

            AddEntry(entries, streams, warnings, i + 1, line, baseDir, info?.Title, info?.Artist, info?.Duration);
            info = null;
        }

        return new ParsedPlaylist(format, encoding, entries, streams, warnings);
    }

    /// <summary><c>#EXTINF:123,Artist - Title</c> (attributes like <c>tvg-id="x"</c> before the comma are ignored).</summary>
    private static (string? Title, string? Artist, double? Duration)? ParseExtInf(string value)
    {
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        var head = (comma < 0 ? value : value[..comma]).Trim();
        var durationText = head.Split(' ', 2)[0];
        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds))
        {
            return null;
        }

        var name = comma < 0 ? null : value[(comma + 1)..].Trim();
        string? artist = null;
        string? title = string.IsNullOrEmpty(name) ? null : name;
        if (title is not null && title.IndexOf(" - ", StringComparison.Ordinal) is var dash and > 0)
        {
            artist = title[..dash].Trim();
            title = title[(dash + 3)..].Trim();
        }

        return (title, artist, seconds > 0 ? seconds : null);
    }

    private static ParsedPlaylist ReadPls(string file, string[] lines, string baseDir, string encoding)
    {
        var warnings = new List<string>();
        var files = new SortedDictionary<int, (int Line, string Value)>();
        var titles = new Dictionary<int, string>();
        var lengths = new Dictionary<int, double>();
        int? declared = null;
        var sawHeader = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sawHeader |= line.Equals("[playlist]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                Warn(warnings, $"line {i + 1}: not a key=value line, ignored");
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("NumberOfEntries", StringComparison.OrdinalIgnoreCase))
            {
                declared = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
                if (declared is null)
                {
                    Warn(warnings, $"line {i + 1}: NumberOfEntries '{value}' is not a number");
                }

                continue;
            }

            if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = PlsKey().Match(key);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                Warn(warnings, $"line {i + 1}: unknown key '{key}' ignored");
                continue;
            }

            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "file":
                    if (files.ContainsKey(index))
                    {
                        Warn(warnings, $"line {i + 1}: File{index} appears twice; the last one is used");
                    }

                    files[index] = (i + 1, value);
                    break;
                case "title":
                    titles[index] = value;
                    break;
                default:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && double.IsFinite(seconds))
                    {
                        lengths[index] = seconds;
                    }

                    break;
            }
        }

        if (!sawHeader)
        {
            if (files.Count == 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat, $"'{file}' is not a PLS playlist (no [playlist] section and no FileN entries).");
            }

            Warn(warnings, "the [playlist] header is missing");
        }

        if (declared is { } count && count != files.Count)
        {
            Warn(warnings, $"NumberOfEntries says {count} but the file lists {files.Count} entries; all {files.Count} listed entries are used");
        }

        var entries = new List<ParsedPlaylistEntry>();
        var streams = new List<string>();
        foreach (var (index, (line, value)) in files)
        {
            string? title = titles.GetValueOrDefault(index);
            string? artist = null;
            if (title is not null && title.IndexOf(" - ", StringComparison.Ordinal) is var dash and > 0)
            {
                artist = title[..dash].Trim();
                title = title[(dash + 3)..].Trim();
            }

            AddEntry(entries, streams, warnings, line, value, baseDir, string.IsNullOrEmpty(title) ? null : title, artist, lengths.TryGetValue(index, out var s) ? s : null);
        }

        return new ParsedPlaylist(PlaylistFormat.Pls, encoding, entries, streams, warnings);
    }

    private static void AddEntry(List<ParsedPlaylistEntry> entries, List<string> streams, List<string> warnings, int line, string raw, string baseDir,
        string? title, string? artist, double? duration)
    {
        var value = raw.Trim().Trim('"');
        if (value.Length == 0)
        {
            return;
        }

        if (IsStream(value))
        {
            streams.Add(value);
            Warn(warnings, $"line {line}: stream '{value}' skipped (only local files are supported)");
            return;
        }

        var (candidates, display) = LocalCandidates(value, baseDir);
        entries.Add(new ParsedPlaylistEntry(line, raw, candidates, display, title, artist, duration));
    }

    /// <summary>A URL with a scheme of two or more letters other than file: (http, https, rtsp, mms, ...). <c>C:\x</c> is not a URL.</summary>
    internal static bool IsStream(string value)
        => UrlScheme().Match(value) is { Success: true } m && !m.Groups[1].Value.Equals("file", StringComparison.OrdinalIgnoreCase);

    /// <summary>Local path candidates for an entry: file URIs, relative paths (to the playlist's folder), both separators, %-encoding.</summary>
    internal static (IReadOnlyList<string> Candidates, string Display) LocalCandidates(string value, string baseDir)
    {
        var raw = value;
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            raw = FromFileUri(raw);
        }

        var candidates = new List<string>(2);
        string display;
        if (IsForeignAbsolute(raw))
        {
            // e.g. "C:\Music\a.flac" or "\\server\share\a.flac" on Linux/macOS: not resolvable here; matched by path suffix later.
            display = raw.Replace('\\', '/');
            return (candidates, display);
        }

        var local = ToLocal(raw, baseDir);
        candidates.Add(local);
        display = local;
        if (raw.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                var decoded = Uri.UnescapeDataString(raw);
                if (!decoded.Equals(raw, StringComparison.Ordinal) && !IsForeignAbsolute(decoded))
                {
                    candidates.Add(ToLocal(decoded, baseDir));
                }
            }
            catch (UriFormatException)
            {
                // keep the literal path
            }
        }

        return (candidates, display);
    }

    private static string FromFileUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var local = uri.LocalPath;
            // file:///C:/x on Unix gives "/C:/x"; keep it recognisable as a Windows path.
            return !OperatingSystem.IsWindows() && local.Length > 3 && local[0] == '/' && char.IsAsciiLetter(local[1]) && local[2] == ':' ? local[1..] : local;
        }

        // Malformed URI: strip the scheme and decode what we can.
        var rest = value["file:".Length..].TrimStart('/');
        try
        {
            rest = Uri.UnescapeDataString(rest);
        }
        catch (UriFormatException)
        {
            // keep as is
        }

        return OperatingSystem.IsWindows() || (rest.Length > 1 && rest[1] == ':') ? rest : "/" + rest;
    }

    private static bool IsForeignAbsolute(string path)
        => !OperatingSystem.IsWindows()
           && ((path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') || path.StartsWith(@"\\", StringComparison.Ordinal));

    private static string ToLocal(string path, string baseDir)
    {
        var p = OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path.Replace('\\', '/');
        try
        {
            return LibraryKeys.NormalizePath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return p;
        }
    }

    private static void Warn(List<string> warnings, string message)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(message);
        }
        else if (warnings.Count == MaxWarnings)
        {
            warnings.Add("(more warnings not shown)");
        }
    }

    [GeneratedRegex(@"^(file|title|length)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlsKey();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]+)://", RegexOptions.CultureInvariant)]
    private static partial Regex UrlScheme();

    // ------------------------------------------------------------------ write

    /// <summary>Writes atomically (temp file + rename). Returns warnings (e.g. an .m3u that needed UTF-8).</summary>
    /// <exception cref="MusicSpinException">FileNotFound (folder missing), InvalidState (exists and not <paramref name="overwrite"/>), InvalidArgument (a folder), Unknown (cannot write).</exception>
    internal static IReadOnlyList<string> Write(string file, PlaylistFormat format, IReadOnlyList<PlaylistFileEntry> entries, bool relative, bool overwrite)
    {
        var full = Path.GetFullPath(file);
        if (Directory.Exists(full))
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"'{full}' is a folder; give a file name.");
        }

        var dir = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(dir))
        {
            throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"Folder not found: {dir}");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState, $"'{full}' already exists. Use --force to overwrite it.");
        }

        var warnings = new List<string>();
        string Location(string path) => relative ? Relative(dir, path) : path;
        var sb = new StringBuilder();
        if (format == PlaylistFormat.Pls)
        {
            sb.Append("[playlist]\n");
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var n = i + 1;
                sb.Append(CultureInfo.InvariantCulture, $"File{n}={Location(e.Path)}\n");
                if (DisplayName(e) is { } name)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"Title{n}={name}\n");
                }

                sb.Append(CultureInfo.InvariantCulture, $"Length{n}={(e.DurationSeconds is { } d ? Math.Round(d) : -1)}\n");
            }

            sb.Append(CultureInfo.InvariantCulture, $"NumberOfEntries={entries.Count}\nVersion=2\n");
        }
        else
        {
            sb.Append("#EXTM3U\n");
            foreach (var e in entries)
            {
                sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{(e.DurationSeconds is { } d ? Math.Round(d) : -1)},{DisplayName(e) ?? Path.GetFileNameWithoutExtension(e.Path)}\n");
                sb.Append(Location(e.Path)).Append('\n');
            }
        }

        var text = sb.ToString();
        var encoding = (Encoding)new UTF8Encoding(false);
        if (format == PlaylistFormat.M3u)
        {
            if (text.All(c => c <= '\u00FF'))
            {
                encoding = Encoding.Latin1;
            }
            else
            {
                warnings.Add("Some entries contain characters outside Latin-1; the .m3u file was written as UTF-8 (use --format m3u8 for a file that is UTF-8 by definition).");
            }
        }

        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(temp, text, encoding);
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new MusicSpinException(MusicSpinErrorCode.Unknown, $"Cannot write '{full}': {ex.Message}", ex);
        }

        return warnings;
    }

    private static string? DisplayName(PlaylistFileEntry e)
        => e.Title is null ? null : (e.Artist is null ? e.Title : $"{e.Artist} - {e.Title}").ReplaceLineEndings(" ");

    private static string Relative(string dir, string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetRelativePath(dir, path) : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
