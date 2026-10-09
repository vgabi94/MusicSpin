using MusicSpin.Core.Audio;

namespace MusicSpin.Tests;

public class PcmFormatTests
{
    [Fact]
    public void Converts_between_samples_and_time()
    {
        var format = new PcmFormat(96_000, 2, 24);

        Assert.Equal(192_000, format.SamplesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(1.5), format.SamplesToTime(288_000));
        Assert.Equal(288_000, format.TimeToSamples(TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void Degenerate_format_yields_zero_time()
        => Assert.Equal(TimeSpan.Zero, new PcmFormat(0, 0).SamplesToTime(1000));
}
