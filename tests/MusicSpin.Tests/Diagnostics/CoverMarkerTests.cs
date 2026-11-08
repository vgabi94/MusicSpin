using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;
using TagLib;

namespace MusicSpin.Tests.Diagnostics;

/// <summary>Cover file cache: a miss encodes, a second request is a hit and does not resize.</summary>
public sealed class CoverMarkerTests : LibraryTestBase
{
    [Fact]
    public void A_cached_cover_is_a_hit_without_another_resize()
    {
        var track = LibraryTestData.Write(Path.Combine(Music, "Artist", "Album", "01.flac"), LibraryTestData.Tiny16, "Song", "Artist", "Album");
        LibraryTestData.SetPictures(track, (System.IO.File.ReadAllBytes(Path.Combine(LibraryTestData.FixturesDirectory, "cover-red.jpg")), PictureType.FrontCover));
        AddAndScan();
        string albumId;
        CoverSource source;
        using (var library = OpenRead())
        {
            albumId = library.Albums(new AlbumQuery()).Items.Single().Id;
            source = library.CoverSources([albumId]).Single().Value;
        }

        var resizer = new CountingResizer();
        using (var capture = MarkerCapture.Start())
        {
            var created = new CoverStore(Data, resizer).GetPath(source, CoverSize.Small);
            Assert.NotNull(created);
#if PERF_MARKERS
            Assert.Equal("2", Assert.Single(capture.Named("cover.file")).Tags["result"]);
            Assert.Single(capture.Named("cover.resize"));
            Assert.Equal(1, resizer.Calls);
#else
            Assert.Empty(capture.Marks);
            Assert.Equal(1, resizer.Calls);
#endif
        }

        using (var capture = MarkerCapture.Start())
        {
            var again = new CoverStore(Data, resizer).GetPath(source, CoverSize.Small);
            Assert.NotNull(again);
#if PERF_MARKERS
            Assert.Equal("1", Assert.Single(capture.Named("cover.file")).Tags["result"]);
            Assert.Empty(capture.Named("cover.resize"));
#endif
            Assert.Equal(1, resizer.Calls);
        }
    }

    private sealed class CountingResizer : ICoverResizer
    {
        public int Calls;

        public CoverImage? Resize(ReadOnlySpan<byte> image, int maxSize)
        {
            Calls++;
            return new CoverImage(image.ToArray(), ".jpg");
        }
    }
}
