using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

public class TrackInfoTests
{
    [Theory]
    [InlineData(44_100, 16, true, false)]  // CD quality
    [InlineData(44_100, 24, true, true)]   // 24-bit
    [InlineData(96_000, 16, true, true)]   // high sample rate
    [InlineData(2_822_400, 1, true, true)] // DSD64
    [InlineData(96_000, 24, false, false)] // lossy (AAC) is never hi-res
    public void HiRes_requires_lossless_and_more_than_cd_resolution(int rate, int bits, bool lossless, bool expected)
    {
        var track = TestAudio.Track(sampleRate: rate, bits: bits) with { IsLossless = lossless, Codec = lossless ? "flac" : "aac" };
        Assert.Equal(expected, track.IsHiRes);
    }

    [Fact]
    public void Common_tags_are_read_from_the_tag_map_and_blank_values_count_as_missing()
    {
        // A plain dictionary: TrackInfo itself looks keys up as given (lower case, as the decoder stores them).
        var track = TestAudio.Track() with
        {
            Tags = new Dictionary<string, string>
            {
                ["title"] = "Blue in Green",
                ["artist"] = "Miles Davis",
                ["album"] = "   ",
                ["album_artist"] = string.Empty,
                ["genre"] = "Jazz",
                ["date"] = "1959",
                ["track"] = "3/5",
            },
        };

        Assert.Equal(("Blue in Green", "Miles Davis", "Jazz", "1959", "3/5"), (track.Title, track.Artist, track.Genre, track.Date, track.TrackNumber));
        Assert.Null(track.Album);
        Assert.Null(track.AlbumArtist);
    }

    [Fact]
    public void Missing_tags_are_null()
    {
        var track = TestAudio.Track() with { Tags = new Dictionary<string, string>() };

        Assert.Null(track.Title);
        Assert.Null(track.Artist);
        Assert.Null(track.Album);
    }
}
