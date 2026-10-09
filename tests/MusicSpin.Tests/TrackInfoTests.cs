using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

public class TrackInfoTests
{
    [Theory]
    [InlineData(44_100, 16, true, false)]  // CD quality
    [InlineData(44_100, 24, true, true)]   // 24-bit
    [InlineData(96_000, 16, true, true)]   // high sample rate
    [InlineData(2_822_400, 1, true, true)] // DSD64
    [InlineData(96_000, 24, false, false)] // lossy is never hi-res
    public void HiRes_requires_lossless_and_more_than_cd_resolution(int rate, int bits, bool lossless, bool expected)
    {
        var track = TestAudio.Track(sampleRate: rate, bits: bits) with { IsLossless = lossless };
        Assert.Equal(expected, track.IsHiRes);
    }

    [Fact]
    public void Exposes_common_tags_case_insensitively()
    {
        var track = TestAudio.Track();
        Assert.Equal("Fake Song", track.Title);
        Assert.Equal("Fake Artist", track.Artist);
        Assert.Null(track.Album);
    }
}
