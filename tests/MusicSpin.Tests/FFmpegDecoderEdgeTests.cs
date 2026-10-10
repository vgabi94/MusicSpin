using MusicSpin.Core;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>FFmpeg decoder edge cases: seek clamping, disposal, damaged files, the decoder list. Skipped without FFmpeg.</summary>
public sealed class FFmpegDecoderEdgeTests : IDisposable
{
    private readonly string _dir = TempDir.Create("ffedge");
    private readonly FFmpegDecoderFactory _factory = new();

    public void Dispose() => TempDir.TryDelete(_dir);

    private static void RequireFFmpeg()
        => Assert.SkipUnless(FFmpegRuntime.IsAvailable, $"FFmpeg {FFmpegRuntime.ExpectedFFmpegVersion} not available: {FFmpegRuntime.Status.Error}");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Seeks_outside_the_track_are_clamped_to_its_bounds()
    {
        RequireFFmpeg();
        var wav = TestAudio.WriteWav24(Path.Combine(_dir, "t.wav"), 48_000, 2, 48_000); // 1 s
        using var decoder = _factory.Open(wav);
        Assert.True(decoder.CanSeek);

        decoder.Seek(TimeSpan.FromSeconds(-5));
        var buffer = new float[64];
        Assert.True(decoder.Read(buffer) > 0);
        Assert.InRange(decoder.Position.TotalMilliseconds, 0, 5);

        decoder.Seek(TimeSpan.FromSeconds(30));
        var total = 0;
        int n;
        while ((n = decoder.Read(buffer)) > 0)
        {
            total += n;
        }

        Assert.InRange(total, 0, 2 * 48); // at most a sliver after the end
    }

    [Fact]
    public void A_disposed_decoder_rejects_seeks_and_tolerates_double_dispose()
    {
        RequireFFmpeg();
        var decoder = _factory.Open(TestAudio.WriteWav24(Path.Combine(_dir, "d.wav"), 8000, 1, 800));
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.Seek(TimeSpan.Zero));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_truncated_flac_decodes_what_is_there_and_ends_without_throwing()
    {
        RequireFFmpeg();
        var source = File.ReadAllBytes(Fixture("tone-24bit-96k.flac"));
        var cut = Path.Combine(_dir, "cut.flac");
        File.WriteAllBytes(cut, source[..(source.Length * 2 / 3)]);
        using var decoder = _factory.Open(cut);
        var buffer = new float[4096];
        long samples = 0;
        int n;
        while ((n = decoder.Read(buffer)) > 0)
        {
            samples += n;
        }

        Assert.True(samples > 0);
        Assert.Equal(0, decoder.Read(buffer)); // EOF stays EOF
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Corrupted_frames_in_the_middle_are_skipped_not_fatal()
    {
        RequireFFmpeg();
        var bytes = File.ReadAllBytes(Fixture("tone-24bit-96k.flac"));
        var random = new Random(1234);
        for (var i = bytes.Length / 3; i < bytes.Length / 2; i += 7)
        {
            bytes[i] = (byte)random.Next(256);
        }

        var damaged = Path.Combine(_dir, "damaged.flac");
        File.WriteAllBytes(damaged, bytes);
        using var decoder = _factory.Open(damaged);
        var buffer = new float[4096];
        long samples = 0;
        int n;
        while ((n = decoder.Read(buffer)) > 0)
        {
            samples += n;
        }

        Assert.True(samples > 0);
    }

    [Fact]
    public void The_decoder_list_contains_the_core_lossless_decoders_sorted()
    {
        RequireFFmpeg();
        var decoders = FFmpegDecoderFactory.GetAllAudioDecoders();
        Assert.Contains(decoders, d => d.Name == "flac");
        Assert.Contains(decoders, d => d.Name == "alac");
        Assert.Equal(decoders.Select(d => d.Name).Order(StringComparer.Ordinal), decoders.Select(d => d.Name));
        Assert.StartsWith("FFmpeg ", _factory.Name, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Probing_an_empty_file_is_unsupported_format()
    {
        RequireFFmpeg();
        var empty = Path.Combine(_dir, "empty.flac");
        File.WriteAllBytes(empty, []);
        var ex = Assert.Throws<MusicSpinException>(() => _factory.Probe(empty));
        Assert.Contains(ex.Code, new[] { MusicSpinErrorCode.UnsupportedFormat, MusicSpinErrorCode.DecodeFailed });
    }
}
