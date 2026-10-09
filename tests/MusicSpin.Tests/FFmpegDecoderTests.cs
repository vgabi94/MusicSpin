using MusicSpin.Core;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>Integration tests against the real FFmpeg shared libraries; skipped when they are not installed.</summary>
public sealed class FFmpegDecoderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("musicspin-tests-").FullName;
    private readonly FFmpegDecoderFactory _factory = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void RequireFFmpeg()
        => Assert.SkipUnless(FFmpegRuntime.IsAvailable, $"FFmpeg {FFmpegRuntime.ExpectedFFmpegVersion} not available: {FFmpegRuntime.Status.Error}");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Runtime_reports_version_and_license()
    {
        RequireFFmpeg();
        Assert.False(string.IsNullOrWhiteSpace(FFmpegRuntime.Status.Version));
        Assert.False(string.IsNullOrWhiteSpace(FFmpegRuntime.Status.License));
    }

    [Fact]
    public void Probes_hires_flac_fixture()
    {
        RequireFFmpeg();
        var track = _factory.Probe(Fixture("tone-24bit-96k.flac"));

        Assert.Equal("flac", track.Codec);
        Assert.Equal(96_000, track.SampleRate);
        Assert.Equal(24, track.BitsPerSample);
        Assert.Equal(2, track.Channels);
        Assert.True(track.IsLossless);
        Assert.True(track.IsHiRes);
        Assert.Equal("MusicSpin Test Tone", track.Title);
        Assert.Equal(0.25, track.Duration.TotalSeconds, 2);
    }

    [Fact]
    public void Probes_alac_fixture()
    {
        RequireFFmpeg();
        var track = _factory.Probe(Fixture("tone-16bit-44k.m4a"));

        Assert.Equal("alac", track.Codec);
        Assert.Equal(44_100, track.SampleRate);
        Assert.Equal(16, track.BitsPerSample);
        Assert.True(track.IsLossless);
        Assert.False(track.IsHiRes);
    }

    [Fact]
    public void Decodes_every_frame_of_flac_fixture()
    {
        RequireFFmpeg();
        using var decoder = _factory.Open(Fixture("tone-24bit-96k.flac"));
        var buffer = new float[4096];
        long total = 0;
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            total += read;
        }

        Assert.Equal(24_000 * 2, total);
        Assert.Equal(TimeSpan.FromSeconds(0.25), decoder.Position);
    }

    [Fact]
    public void Decoding_24bit_pcm_is_bit_exact()
    {
        RequireFFmpeg();
        const int rate = 96_000, channels = 2, frames = 9_600;
        var path = TestAudio.WriteWav24(Path.Combine(_dir, "exact.wav"), rate, channels, frames);

        using var decoder = _factory.Open(path);
        Assert.Equal(24, decoder.Track.BitsPerSample);
        Assert.Equal(rate, decoder.Format.SampleRate);

        var samples = new float[frames * channels];
        var read = 0;
        int n;
        while (read < samples.Length && (n = decoder.Read(samples.AsSpan(read))) > 0)
        {
            read += n;
        }

        Assert.Equal(samples.Length, read);
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                // float32 holds every 24-bit integer exactly: converting back must give the original sample.
                var restored = (int)Math.Round(samples[(f * channels) + c] * (1 << 23));
                Assert.Equal(TestAudio.Sample24(f, c), restored);
            }
        }
    }

    [Fact]
    public void Seek_is_sample_accurate()
    {
        RequireFFmpeg();
        const int rate = 48_000, channels = 2;
        var path = TestAudio.WriteWav24(Path.Combine(_dir, "seek.wav"), rate, channels, rate * 2);
        using var decoder = _factory.Open(path);

        decoder.Seek(TimeSpan.FromSeconds(1.25));
        var buffer = new float[channels * 4];
        Assert.Equal(buffer.Length, decoder.Read(buffer));

        const int frame = 60_000; // 1.25 s * 48 kHz
        Assert.Equal(TestAudio.Sample24(frame, 0), (int)Math.Round(buffer[0] * (1 << 23)));
        Assert.Equal(TestAudio.Sample24(frame, 1), (int)Math.Round(buffer[1] * (1 << 23)));
    }

    [Fact]
    public void Missing_file_is_FileNotFound()
    {
        RequireFFmpeg();
        var ex = Assert.Throws<MusicSpinException>(() => _factory.Probe(Path.Combine(_dir, "missing.flac")));
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public void Garbage_file_is_UnsupportedFormat()
    {
        RequireFFmpeg();
        var path = Path.Combine(_dir, "garbage.flac");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))]);

        var ex = Assert.Throws<MusicSpinException>(() => _factory.Probe(path));
        Assert.Equal(MusicSpinErrorCode.UnsupportedFormat, ex.Code);
    }

    [Fact]
    public void Reports_core_hires_formats_as_supported()
    {
        RequireFFmpeg();
        var formats = _factory.GetSupportedFormats();
        foreach (var id in new[] { "flac", "alac", "wav", "aiff", "dsd" })
        {
            Assert.True(Assert.Single(formats, f => f.Id == id).Supported, id);
        }
    }
}
