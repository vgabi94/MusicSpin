using System.Diagnostics.CodeAnalysis;

namespace MusicSpin.Tests.Shared;

/// <summary>Unique temp folders under the system temp dir, cleaned up best-effort at process exit.</summary>
internal static class TempDir
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "musicspin-tests", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    static TempDir()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(Root);
    }

    public static string Create(string prefix = "t")
    {
        var path = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            // Restore permissions changed by tests so cleanup can proceed. Entries that vanish
            // mid-enumeration (e.g. a player socket removed during shutdown) are skipped; .NET then
            // reports an invalid mode (ArgumentException) or a missing file for them.
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (OperatingSystem.IsWindows())
                {
                    continue;
                }

                try
                {
                    entry.Refresh();
                    if (!entry.Exists || entry.LinkTarget is not null)
                    {
                        continue;
                    }

                    entry.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // entry vanished or is not chmod-able; skip it
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // best effort
        }
    }
}

/// <summary>Builds real, tagged audio files from the tiny committed fixtures (TagLib# retagging).</summary>
internal static class LibraryTestData
{
    public static string FixturesDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Tiny16 => Path.Combine(FixturesDirectory, "tiny-16bit-44k.flac");

    public static string Tiny24 => Path.Combine(FixturesDirectory, "tiny-24bit-96k.flac");

    public static string TinyMp3 => Path.Combine(FixturesDirectory, "tiny-44k.mp3");

    /// <summary>Copies <paramref name="source"/> to <paramref name="path"/> and writes the given tags.</summary>
    [SuppressMessage("Design", "CA1054", Justification = "test helper")]
    public static string Write(string path, string source, string? title = null, string? artist = null, string? album = null,
        string? albumArtist = null, uint track = 0, uint disc = 0, uint year = 0, string? genre = null, bool compilation = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(source, path, overwrite: true);
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = title;
        tag.Performers = artist is null ? [] : [artist];
        tag.AlbumArtists = albumArtist is null ? [] : [albumArtist];
        tag.Album = album;
        tag.Track = track;
        tag.Disc = disc;
        tag.Year = year;
        tag.Genres = genre is null ? [] : [genre];
        if (compilation && file.GetTag(TagLib.TagTypes.Xiph, true) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField("COMPILATION", "1");
        }

        file.Save();
        return path;
    }

    /// <summary>Replaces the pictures embedded in <paramref name="path"/> (none: removes them).</summary>
    public static void SetPictures(string path, params (byte[] Data, TagLib.PictureType Type)[] pictures)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Pictures = pictures.Select(p => (TagLib.IPicture)new TagLib.Picture(new TagLib.ByteVector(p.Data)) { Type = p.Type }).ToArray();
        file.Save();
    }

    /// <summary>Writes a file with no tags at all (stripped copy of a fixture).</summary>
    public static string WriteUntagged(string path, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(source, path, overwrite: true);
        using var file = TagLib.File.Create(path);
        file.RemoveTags(TagLib.TagTypes.AllTags);
        file.Save();
        return path;
    }

    /// <summary>A small, varied library: 2 artists, 3 albums (one compilation), hi-res and CD-quality, an MP3.</summary>
    /// <summary>
    /// Genre edge cases (8 tracks): case/diacritics ("JAZZ", "Électronique"), multi-genre tags ("Jazz; Fusion", "Blues/Jazz"),
    /// a near miss ("Folk Rock" is not "Rock"), an untagged track, and album artists that sort Coltrane, Dylan, Miles Davis (1959, 1970), Nina.
    /// </summary>
    public static string CreateGenreLibrary(string root)
    {
        Write(Path.Combine(root, "Miles", "Bitches Brew", "01.flac"), Tiny16, "Pharaoh's Dance", "Miles Davis", "Bitches Brew", "Miles Davis", 1, 1, 1970, "Jazz; Fusion");
        Write(Path.Combine(root, "Miles", "Kind of Blue", "02.flac"), Tiny16, "Freddie Freeloader", "Miles Davis", "Kind of Blue", "Miles Davis", 2, 1, 1959, "JAZZ");
        Write(Path.Combine(root, "Miles", "Kind of Blue", "01.flac"), Tiny16, "So What", "Miles Davis", "Kind of Blue", "Miles Davis", 1, 1, 1959, "jazz");
        Write(Path.Combine(root, "Coltrane", "Blue Train", "01.flac"), Tiny16, "Blue Train", "John Coltrane", "Blue Train", "John Coltrane", 1, 1, 1957, "Blues/Jazz");
        Write(Path.Combine(root, "Nina", "Pastel", "01.flac"), Tiny16, "Pastel Blues", "Nina Simone", "Pastel Blues", "Nina Simone", 1, 1, 1965, "Blues");
        Write(Path.Combine(root, "Dylan", "Folk", "01.flac"), Tiny16, "Folk Song", "Bob Dylan", "Folk", "Bob Dylan", 1, 1, 1965, "Folk Rock");
        Write(Path.Combine(root, "Air", "Moon", "01.flac"), Tiny16, "La Femme", "Air", "Moon Safari", "Air", 1, 1, 1998, "Électronique");
        Write(Path.Combine(root, "Untagged", "01.flac"), Tiny16, "No Genre", "Nobody", "Untagged", "Nobody", 1, 1, 2000);
        return root;
    }

    public static string CreateSmallLibrary(string root)
    {
        Write(Path.Combine(root, "Miles", "Kind of Blue", "01 So What.flac"), Tiny24, "So What", "Miles Davis", "Kind of Blue", "Miles Davis", 1, 1, 1959, "Jazz");
        Write(Path.Combine(root, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac"), Tiny24, "Freddie Freeloader", "Miles Davis", "Kind of Blue", "Miles Davis", 2, 1, 1959, "Jazz");
        Write(Path.Combine(root, "Miles", "Bitches Brew", "01 Pharaoh's Dance.flac"), Tiny16, "Pharaoh's Dance", "miles davis", "Bitches Brew", null, 1, 1, 1970, "Jazz");
        Write(Path.Combine(root, "Comp", "01.flac"), Tiny16, "Song A", "Artist A", "Jazz Hits", null, 1, 1, 2001, "Jazz");
        Write(Path.Combine(root, "Comp", "02.flac"), Tiny16, "Song B", "Artist B", "Jazz Hits", null, 2, 1, 2001, "Jazz");
        Write(Path.Combine(root, "Lossy", "track.mp3"), TinyMp3, "Lossy Song", "Artist A", "Singles", null, 1, 0, 2010, "Pop");
        return root;
    }
}
