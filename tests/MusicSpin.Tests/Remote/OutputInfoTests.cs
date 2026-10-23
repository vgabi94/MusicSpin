using MusicSpin.Core.Audio;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

public class OutputInfoTests
{
    [Fact]
    public void Without_an_opened_device_only_the_mode_is_described()
    {
        Assert.Equal("exclusive", new OutputInfo("wasapi", "BTR13", true).Describe());
        Assert.Equal("shared", new OutputInfo("wasapi", null, false).Describe());
    }

    [Fact]
    public void The_opened_device_replaces_what_was_asked()
    {
        var asked = new OutputInfo("wasapi", "BTR13", Exclusive: true);
        var info = asked.With(new OpenedOutput("FiiO BTR13", false, 48_000, 2, "f32", 32, Verified: true, Notice: "refused"));

        Assert.Equal(("wasapi", "FiiO BTR13", false, "refused"), (info.Backend, info.Device, info.Exclusive, info.Notice));
        Assert.Equal(new OutputFormat(48_000, 2, "f32", 32, true), info.Format);
        Assert.Equal("shared, 48 kHz 32-bit float", info.Describe());
    }

    [Theory]
    [InlineData(44_100, 24, "s24", "exclusive, 44.1 kHz 24-bit")]
    [InlineData(96_000, 24, "s24in32", "exclusive, 96 kHz 24-bit")]
    [InlineData(88_200, 16, "s16", "exclusive, 88.2 kHz 16-bit")]
    public void Rates_and_depths_read_like_a_dac_display(int rate, int bits, string format, string expected)
        => Assert.Equal(expected, new OutputInfo("wasapi", null, true).With(new OpenedOutput(null, true, rate, 2, format, bits, true)).Describe());

    [Fact]
    public void An_opened_device_without_a_name_keeps_the_requested_one()
        => Assert.Equal("BTR13", new OutputInfo("wasapi", "BTR13", true).With(new OpenedOutput(null, true, 44_100, 2, "s16", 16, true)).Device);
}
