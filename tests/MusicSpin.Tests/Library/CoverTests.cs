using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using TagLib;

namespace MusicSpin.Tests.Library;

public sealed class CoverTests : LibraryTestBase
{
    private static readonly byte[] Red = System.IO.File.ReadAllBytes(Path.Combine(LibraryTestData.FixturesDirectory, "cover-red.jpg"));
    private static readonly byte[] Blue = System.IO.File.ReadAllBytes(Path.Combine(LibraryTestData.FixturesDirectory, "cover-blue.png"));

    private string AlbumDir => Path.Combine(Music, "Artist", "Album");

    private string WriteTrack(string? folder = null, uint track = 1, string source = "flac", string album = "Album")
    {
        var dir = folder ?? AlbumDir;
        var (fixture, ext) = source == "mp3" ? (LibraryTestData.TinyMp3, ".mp3") : (LibraryTestData.Tiny16, ".flac");
        return LibraryTestData.Write(Path.Combine(dir, $"{track:D2}{ext}"), fixture, $"Song {track}", "Artist", album, "Artist", track, 1, 2001);
    }

    private string AlbumId(string album = "Album")
    {
        using var library = OpenRead();
        return library.Albums(new AlbumQuery()).Items.Single(a => a.Title == album).Id;
    }

    private string? Cover(CoverSize size = CoverSize.Medium, ICoverResizer? resizer = null, string album = "Album")
    {
        var id = AlbumId(album);
        CoverSource? source;
        using (var library = OpenRead())
        {
            source = library.CoverSources([id]).GetValueOrDefault(id);
        }

        return source is null ? null : new CoverStore(Data, resizer).GetPath(source, size);
    }

    private static byte[] Bytes(string? path)
    {
        Assert.NotNull(path);
        return System.IO.File.ReadAllBytes(path);
    }

    [Fact]
    public void Embedded_front_cover_is_found_and_cached_in_the_data_folder()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        AddAndScan();

        var path = Cover();

        Assert.Equal(Red, Bytes(path));
        Assert.StartsWith(Path.Combine(Data.CoversDirectory, AlbumId()) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.EndsWith("-orig.jpg", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Front_cover_is_preferred_to_other_embedded_pictures()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Blue, PictureType.BackCover), (Red, PictureType.FrontCover));
        AddAndScan();

        Assert.Equal(Red, Bytes(Cover()));
    }

    [Fact]
    public void Without_a_front_cover_the_first_embedded_picture_is_used()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Blue, PictureType.Artist), (Red, PictureType.BackCover));
        AddAndScan();

        Assert.Equal(Blue, Bytes(Cover()));
    }

    [Fact]
    public void Mp3_pictures_are_found()
    {
        LibraryTestData.SetPictures(WriteTrack(source: "mp3"), (Red, PictureType.FrontCover));
        AddAndScan();

        Assert.Equal(Red, Bytes(Cover()));
    }

    [Fact]
    public void The_first_track_in_album_order_with_a_picture_wins()
    {
        WriteTrack(track: 1);
        LibraryTestData.SetPictures(WriteTrack(track: 3), (Red, PictureType.FrontCover));
        LibraryTestData.SetPictures(WriteTrack(track: 2), (Blue, PictureType.FrontCover));
        AddAndScan();

        Assert.Equal(Blue, Bytes(Cover()));
    }

    [Fact]
    public void Embedded_cover_wins_over_a_folder_image()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "cover.png"), Blue);
        AddAndScan();

        Assert.Equal(Red, Bytes(Cover()));
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("Folder.JPG")]
    [InlineData("front.jpeg")]
    [InlineData("album.webp")]
    public void Folder_images_are_found_by_name_in_any_case(string name)
    {
        WriteTrack();
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, name), name.EndsWith("webp", StringComparison.Ordinal) ? Webp() : Red);
        AddAndScan();

        Assert.NotNull(Cover());
    }

    [Fact]
    public void Folder_names_are_tried_in_order()
    {
        WriteTrack();
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "folder.jpg"), Red);
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "cover.png"), Blue);
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "scan.jpg"), Red);
        AddAndScan();

        Assert.Equal(Blue, Bytes(Cover()));
    }

    [Fact]
    public void Other_images_in_the_folder_are_ignored()
    {
        WriteTrack();
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "back.jpg"), Red);
        AddAndScan();

        Assert.Null(Cover());
    }

    [Fact]
    public void Albums_split_into_disc_folders_use_the_parent_folder_image()
    {
        WriteTrack(Path.Combine(AlbumDir, "CD1"), 1);
        WriteTrack(Path.Combine(AlbumDir, "Disc 2"), 2);
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "cover.jpg"), Red);
        AddAndScan();

        Assert.Equal(Red, Bytes(Cover()));
    }

    [Fact]
    public void An_album_without_art_has_no_cover()
    {
        WriteTrack();
        AddAndScan();

        Assert.Null(Cover());
        var id = AlbumId();
        using var library = OpenRead();
        Assert.Null(library.CoverPath(id, CoverSize.Small));
    }

    [Fact]
    public void Unknown_album_ids_have_no_source()
    {
        WriteTrack();
        AddAndScan();
        using var library = OpenRead();

        Assert.Empty(library.CoverSources([Guid.NewGuid().ToString("D"), "", "x"]));
        Assert.Null(library.CoverPath(Guid.NewGuid().ToString("D"), CoverSize.Medium));
    }

    [Fact]
    public void Sources_for_several_albums_are_read_at_once()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        WriteTrack(Path.Combine(Music, "Artist", "Other"), 1, album: "Other");
        AddAndScan();
        var (album, other) = (AlbumId(), AlbumId("Other"));
        using var library = OpenRead();

        var sources = library.CoverSources([album, other, album]);

        Assert.Equal(2, sources.Count);
        Assert.Single(sources[album].Embedded);
        Assert.Empty(sources[other].Embedded);
        Assert.Equal([Path.Combine(Music, "Artist", "Other")], sources[other].Folders);
    }

    [Fact]
    public void Garbage_embedded_pictures_are_skipped_for_the_folder_image()
    {
        LibraryTestData.SetPictures(WriteTrack(), ("not an image"u8.ToArray(), PictureType.FrontCover));
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "cover.png"), Blue);
        AddAndScan();

        Assert.Equal(Blue, Bytes(Cover()));
    }

    [Fact]
    public void Cached_covers_are_reused()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        AddAndScan();
        var resizer = new FakeResizer();

        var first = Cover(CoverSize.Small, resizer);
        var second = Cover(CoverSize.Small, resizer);

        Assert.Equal(first, second);
        Assert.Equal(1, resizer.Calls);
    }

    [Fact]
    public void Each_size_is_resized_to_twice_its_screen_size()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        AddAndScan();
        var resizer = new FakeResizer();

        var paths = new[] { CoverSize.Small, CoverSize.Medium, CoverSize.Large }.Select(s => Cover(s, resizer)).ToList();

        Assert.Equal([128, 720, 2048], resizer.Sizes);
        Assert.Equal(3, paths.Distinct().Count());
        Assert.All(paths, p => Assert.EndsWith(".png", p, StringComparison.Ordinal)); // the resizer's format
        Assert.EndsWith("-720.png", paths[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_the_resizer_cannot_decode_falls_back_to_the_folder_image_and_is_not_retried()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "cover.png"), Blue);
        AddAndScan();
        var resizer = new FakeResizer { Refuse = Red };

        Assert.NotNull(Cover(CoverSize.Medium, resizer));
        Assert.NotNull(Cover(CoverSize.Small, resizer));

        Assert.Equal(1, resizer.Refused); // the embedded picture is tried once, for the first size only
        Assert.Equal(3, resizer.Calls); // then the folder image, once per size
    }

    [Fact]
    public void A_throwing_resizer_counts_as_cannot_decode()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        AddAndScan();

        Assert.Null(Cover(CoverSize.Medium, new FakeResizer { Throw = true }));
    }

    [Fact]
    public void A_changed_embedded_cover_is_refreshed_after_a_scan()
    {
        var file = WriteTrack();
        LibraryTestData.SetPictures(file, (Red, PictureType.FrontCover));
        AddAndScan();
        var old = Cover();

        LibraryTestData.SetPictures(file, (Blue, PictureType.FrontCover));
        Touch(file);
        Rescan();
        var updated = Cover();

        Assert.NotEqual(old, updated);
        Assert.Equal(Blue, Bytes(updated));
        Assert.False(System.IO.File.Exists(old));
    }

    [Fact]
    public void A_changed_folder_image_is_refreshed_without_a_scan()
    {
        WriteTrack();
        var image = Path.Combine(AlbumDir, "cover.jpg");
        System.IO.File.WriteAllBytes(image, Red);
        AddAndScan();
        var old = Cover();

        System.IO.File.WriteAllBytes(image, [.. Red, 0, 0]);
        Touch(image);
        var updated = Cover();

        Assert.NotEqual(old, updated);
        Assert.Equal([.. Red, 0, 0], Bytes(updated));
        Assert.False(System.IO.File.Exists(old));
    }

    [Fact]
    public void A_new_folder_image_is_picked_up_for_an_album_that_had_none()
    {
        WriteTrack();
        AddAndScan();
        Assert.Null(Cover());

        System.IO.File.WriteAllBytes(Path.Combine(AlbumDir, "cover.jpg"), Red);

        Assert.Equal(Red, Bytes(Cover()));
    }

    [Fact]
    public void Covers_of_albums_that_left_the_library_are_pruned()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        AddAndScan();
        var id = AlbumId();
        Assert.NotNull(Cover());
        var dir = Path.Combine(Data.CoversDirectory, id);
        Assert.True(Directory.Exists(dir));

        using (var library = Open())
        {
            library.RemoveFolder(Music);
        }

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void A_scan_prunes_the_covers_of_renamed_albums()
    {
        var file = WriteTrack();
        LibraryTestData.SetPictures(file, (Red, PictureType.FrontCover));
        AddAndScan();
        var oldDir = Path.Combine(Data.CoversDirectory, AlbumId());
        Assert.NotNull(Cover());

        using (var tagged = TagLib.File.Create(file))
        {
            tagged.Tag.Album = "Renamed";
            tagged.Save();
        }

        Touch(file);
        Rescan();

        Assert.False(Directory.Exists(oldDir));
        Assert.Equal(Red, Bytes(Cover(album: "Renamed")));
    }

    [Fact]
    public void Concurrent_requests_agree_on_one_file()
    {
        LibraryTestData.SetPictures(WriteTrack(), (Red, PictureType.FrontCover));
        AddAndScan();
        var id = AlbumId();
        CoverSource source;
        using (var library = OpenRead())
        {
            source = library.CoverSources([id])[id];
        }

        var store = new CoverStore(Data, new FakeResizer());
        var paths = new string?[16];
        Parallel.For(0, paths.Length, i => paths[i] = store.GetPath(source, CoverSize.Medium));

        Assert.Single(paths.Distinct());
        Assert.Equal(Red, Bytes(paths[0]));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(Data.CoversDirectory, id)), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unwritable_cache_still_shows_a_folder_image()
    {
        RequirePosixNonRoot();
        WriteTrack();
        var image = Path.Combine(AlbumDir, "cover.jpg");
        System.IO.File.WriteAllBytes(image, Red);
        AddAndScan();
        Directory.CreateDirectory(Data.CoversDirectory);
        Chmod(Data.CoversDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Equal(image, Cover());
    }

    [Fact]
    public void Album_ids_must_be_ids_and_sizes_must_be_known()
    {
        var store = new CoverStore(Data);

        Assert.Throws<ArgumentException>(() => store.GetPath(new CoverSource("../x", [], []), CoverSize.Small));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPath(new CoverSource(Guid.NewGuid().ToString("D"), [], []), (CoverSize)42));
        Assert.Null(store.GetPath(new CoverSource(Guid.NewGuid().ToString("D"), [], []), CoverSize.Large));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D }, ".png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 }, ".gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00 }, ".bmp")]
    [InlineData(new byte[] { 0x00, 0x01 }, null)]
    [InlineData(new byte[] { }, null)]
    public void Image_formats_are_recognised_by_their_first_bytes(byte[] data, string? extension)
        => Assert.Equal(extension, CoverCache.ImageExtension(data));

    [Fact]
    public void Webp_is_recognised() => Assert.Equal(".webp", CoverCache.ImageExtension(Webp()));

    private static byte[] Webp() => [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8];

    /// <summary>Returns the image unchanged as ".png", counting calls; can refuse one image or throw.</summary>
    private sealed class FakeResizer : ICoverResizer
    {
        private int _calls;

        public int Calls => _calls;

        public int Refused { get; private set; }

        public List<int> Sizes { get; } = [];

        public byte[]? Refuse { get; init; }

        public bool Throw { get; init; }

        public CoverImage? Resize(ReadOnlySpan<byte> image, int maxSize)
        {
            Interlocked.Increment(ref _calls);
            lock (Sizes)
            {
                Sizes.Add(maxSize);
            }

            if (Throw)
            {
                throw new InvalidOperationException("broken decoder");
            }

            if (Refuse is not null && image.SequenceEqual(Refuse))
            {
                Refused++;
                return null;
            }

            return new CoverImage(image.ToArray(), ".png");
        }
    }
}
