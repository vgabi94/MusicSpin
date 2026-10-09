using MusicSpin.Core.Audio;

namespace MusicSpin.Tests;

public class KnownFormatsTests
{
    [Fact]
    public void Ids_are_unique_and_every_format_has_decoders()
    {
        Assert.Equal(KnownFormats.All.Count, KnownFormats.All.Select(f => f.Id).Distinct().Count());
        Assert.All(KnownFormats.All, f => Assert.NotEmpty(f.Decoders));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("alac")]
    [InlineData("wav")]
    [InlineData("aiff")]
    [InlineData("dsd")]
    public void HiRes_lossless_formats_are_covered(string id)
    {
        var format = Assert.Single(KnownFormats.All, f => f.Id == id);
        Assert.True(format.Lossless);
        Assert.True(format.HiResCapable);
    }
}
