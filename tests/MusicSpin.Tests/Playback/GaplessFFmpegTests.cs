using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>The decode-thread path against real FFmpeg decoding; skipped when FFmpeg is not installed.</summary>
public sealed class GaplessFFmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("musicspin-gapless-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }

        Directory.Delete(_dir, recursive: true);
    }

    private static void RequireFFmpeg()
        => Assert.SkipUnless(FFmpegRuntime.IsAvailable, $"FFmpeg {FFmpegRuntime.ExpectedFFmpegVersion} not available: {FFmpegRuntime.Status.Error}");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Two_24bit_tracks_play_through_the_ring_buffer_bit_exact_and_gapless()
    {
        RequireFFmpeg();
        const int rate = 96_000, channels = 2, frames = 30_000; // longer than one decode block, shorter than the buffer
        var first = TestAudio.WriteWav24(Path.Combine(_dir, "a.wav"), rate, channels, frames);
        var second = TestAudio.WriteWav24(Path.Combine(_dir, "b.wav"), rate, channels, frames);
        var outputs = new ManualOutputProvider();
        var player = new Player(new FFmpegDecoderFactory(), outputs);
        var queue = new QueuePlayer(player);
        _owned.Add(player);
        _owned.Add(queue);
        queue.SetQueue([new QueueItem(first), new QueueItem(second)]);

        var heard = new List<float>();
        while (true)
        {
            var block = outputs.Output.Pump(4096);
            heard.AddRange(block);
            if (block.Length < 4096)
            {
                break;
            }
        }

        Assert.Equal(1, outputs.Output.OpenCount); // one device open for both
        Assert.Equal(2 * frames * channels, heard.Count);
        for (var i = 0; i < heard.Count; i++)
        {
            var frame = (i / channels) % frames;
            var restored = (int)Math.Round(heard[i] * (1 << 23));
            Assert.Equal(TestAudio.Sample24(frame, i % channels), restored);
        }
    }

    // Fixtures: 1.000 s sine generated with ffmpeg 7.1 (lavfi sine, libmp3lame 128k / native aac 128k in mp4 / libopus 96k).
    // Encoders add priming samples and pad the last frame; FFmpeg trims them from the LAME/Xing header (MP3), the MP4
    // edit list (AAC) and the Ogg pre-skip/granule (Opus), so the decoded length is the original one.
    [Theory]
    [InlineData("gapless-44k.mp3", 44_100)]
    [InlineData("gapless-44k.m4a", 44_100)]
    [InlineData("gapless-48k.opus", 48_000)]
    public void Encoder_delay_and_padding_are_trimmed_so_lossy_tracks_join_without_silence(string fixture, int frames)
    {
        RequireFFmpeg();
        using var decoder = new FFmpegDecoderFactory().Open(Fixture(fixture));
        var buffer = new float[8192];
        long samples = 0;
        int n;
        while ((n = decoder.Read(buffer)) > 0)
        {
            samples += n;
        }

        Assert.Equal(frames, samples / decoder.Format.Channels);
    }
}
