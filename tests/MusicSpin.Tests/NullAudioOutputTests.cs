using MusicSpin.Core.Audio;
using MusicSpin.Core.Output;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

public class NullAudioOutputTests
{
    [Fact]
    public async Task Plays_a_track_to_the_end_without_a_sound_card()
    {
        var decoders = new FakeDecoderFactory().With(TestAudio.Track("/a.flac", sampleRate: 48_000, seconds: 2));
        using var player = new Player(decoders, new NullAudioOutputProvider(realtime: false));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackEnded += (_, _) => ended.TrySetResult();

        player.Load("/a.flac");
        player.Play();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerState.Stopped, player.State);
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position);
    }

    [Fact]
    public void Provider_reports_a_single_null_device()
    {
        var device = Assert.Single(new NullAudioOutputProvider().GetDevices());
        Assert.Equal("null", device.Id);
        Assert.True(device.IsDefault);
    }

    [Fact]
    public void Stop_before_start_is_safe()
    {
        using var output = new NullAudioOutput(realtime: false);
        output.Open(new FakeDecoder(TestAudio.Track()));
        output.Stop();
        Assert.False(output.IsOpen);
        Assert.Equal(0, output.SamplesConsumed);
    }

    [Fact]
    public void Open_requires_source()
        => Assert.Throws<ArgumentNullException>(() => new NullAudioOutput().Open((IPcmSource)null!));
}
