using MusicSpin.Core.Output.SoundFlow;
using SoundFlow.Enums;

namespace MusicSpin.Tests;

public class SoundFlowOutputTests
{
    [Theory]
    [InlineData(16, SampleFormat.S16)]
    [InlineData(24, SampleFormat.S24)]
    [InlineData(20, SampleFormat.S24)]
    [InlineData(32, SampleFormat.F32)]
    [InlineData(1, SampleFormat.F32)] // DSD is decoded to float PCM
    [InlineData(0, SampleFormat.F32)]
    public void Device_format_preserves_source_bit_depth(int bits, SampleFormat expected)
        => Assert.Equal(expected, SoundFlowAudioOutput.DeviceSampleFormat(bits));
}
