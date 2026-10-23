using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>The volume-levelling gain applied by the ring buffer's consumer.</summary>
public sealed class PlaybackBufferGainTests
{
    // 2 kHz: the 5 ms ramp is exactly 10 frames.
    private static readonly PcmFormat Stereo = new(2000, 2, 24);
    private const int RampFrames = 10;

    private static TrackSegment Segment(int id, float gain = 1f)
        => new(new RampDecoder($"/g/{id}", new RampSpec(id, 1000)), id, TimeSpan.Zero, [], []) { Gain = gain };

    private static PlaybackBuffer Create(TrackSegment first, float volume = 1f)
        => new(Stereo, 400, first, () => volume, () => { });

    private static float[] Constant(float value, int frames) => [.. Enumerable.Repeat(value, frames * 2)];

    private static float[] ReadAll(PlaybackBuffer buffer, int samples)
    {
        var block = new float[samples];
        Assert.Equal(samples, buffer.Read(block));
        return block;
    }

    [Fact]
    public void At_unity_gain_and_full_volume_the_samples_are_untouched_bit_for_bit()
    {
        var buffer = Create(Segment(1));
        var input = Enumerable.Range(0, 60).Select(i => MathF.Sin(i * 0.37f) * 0.123456789f).ToArray();
        buffer.Write(input);

        var output = ReadAll(buffer, input.Length);

        Assert.Equal(input.Select(BitConverter.SingleToInt32Bits), output.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void A_track_that_starts_with_a_gain_plays_with_it_from_its_first_sample()
    {
        var buffer = Create(Segment(1, 0.5f));
        buffer.Write(Constant(0.8f, 30));

        Assert.All(ReadAll(buffer, 60), s => Assert.Equal(0.4f, s, 6));
    }

    [Fact]
    public void The_gain_and_the_player_volume_multiply()
    {
        var buffer = Create(Segment(1, 0.5f), volume: 0.5f);
        buffer.Write(Constant(1f, 30));

        Assert.All(ReadAll(buffer, 60), s => Assert.Equal(0.25f, s, 6));
    }

    [Fact]
    public void A_boost_is_applied_without_clamping_it_is_the_gain_calculators_job_to_keep_peaks_below_full_scale()
    {
        var buffer = Create(Segment(1, 2f));
        buffer.Write(Constant(0.25f, 30));

        Assert.All(ReadAll(buffer, 60), s => Assert.Equal(0.5f, s, 6));
    }

    [Fact]
    public void Crossing_a_gapless_boundary_into_a_track_with_another_gain_ramps_instead_of_stepping()
    {
        var first = Segment(1, 1f);
        var second = Segment(2, 0.25f);
        var buffer = Create(first);
        buffer.Write(Constant(1f, 40));
        Assert.True(buffer.TryAddMarker(MarkerKind.Boundary, second));
        buffer.Write(Constant(1f, 40));

        var heard = ReadAll(buffer, 160);

        // frames 0..39 belong to the first track: untouched. Then the gain slides to 0.25 over RampFrames frames.
        Assert.All(heard.Take(80), s => Assert.Equal(1f, s));
        var left = heard.Where((_, i) => i % 2 == 0).Skip(40).ToArray();
        Assert.Equal(1f - 0.75f / RampFrames, left[0], 5); // already moving at the very first sample of the new track
        for (var i = 1; i < left.Length; i++)
        {
            Assert.True(left[i] <= left[i - 1] + 1e-6f, "the ramp never goes back up");
            Assert.True(left[i - 1] - left[i] <= 0.75f / RampFrames + 1e-5f, $"no step bigger than one ramp increment at frame {i}");
        }

        Assert.Equal(0.25f, left[RampFrames - 1], 5);
        Assert.All(left.Skip(RampFrames), s => Assert.Equal(0.25f, s, 6));
        Assert.Equal(heard[80], heard[81]); // both channels move together
    }

    [Fact]
    public void A_gain_that_changes_during_a_track_for_instance_when_its_analysis_arrives_ramps_too()
    {
        var segment = Segment(1, 1f);
        var buffer = Create(segment);
        buffer.Write(Constant(1f, 100));
        var before = ReadAll(buffer, 40);
        Assert.All(before, s => Assert.Equal(1f, s));

        segment.Gain = 2f;
        var after = ReadAll(buffer, 120).Where((_, i) => i % 2 == 0).ToArray();

        Assert.Equal(1.1f, after[0], 5);
        Assert.Equal(2f, after[RampFrames - 1], 5);
        Assert.All(after.Skip(RampFrames), s => Assert.Equal(2f, s, 6));
        for (var i = 1; i < after.Length; i++)
        {
            Assert.True(after[i] >= after[i - 1] - 1e-6f);
        }
    }

    [Fact]
    public void Going_back_to_unity_restores_bit_exact_samples_after_the_ramp()
    {
        var segment = Segment(1, 0.5f);
        var buffer = Create(segment);
        buffer.Write(Constant(0.7f, 100));
        ReadAll(buffer, 20);

        segment.Gain = 1f;
        ReadAll(buffer, 2 * RampFrames);
        var rest = ReadAll(buffer, 2 * 30);

        Assert.All(rest, s => Assert.Equal(BitConverter.SingleToInt32Bits(0.7f), BitConverter.SingleToInt32Bits(s)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.5f)]
    [Trait("Category", "Unhappy")]
    public void A_gain_that_is_not_a_usable_number_is_unity(float gain)
    {
        var segment = Segment(1, gain);
        Assert.Equal(1f, segment.Gain);
        var buffer = Create(segment);
        buffer.Write(Constant(0.3f, 10));

        Assert.All(ReadAll(buffer, 20), s => Assert.Equal(0.3f, s));
    }

    [Fact]
    public void Silence_stays_silent_whatever_the_gain()
    {
        var buffer = Create(Segment(1, 4f));
        var block = Enumerable.Repeat(9f, 10).ToArray();
        buffer.Read(block);

        Assert.All(block, s => Assert.Equal(0f, s)); // underrun: nothing to amplify
    }
}
