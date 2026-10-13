using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;
using Microsoft.Extensions.Time.Testing;

namespace MusicSpin.Tests.Playback;

/// <summary>Spectrum levels for the app's analyzer: the analysis, the tap in the audio path and the meter's subscriptions.</summary>
public sealed class SpectrumTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private static float[] Sine(double hz, int sampleRate, float amplitude = 1f, int count = SpectrumAnalyzer.FftSize)
        => [.. Enumerable.Range(0, count).Select(i => amplitude * (float)Math.Sin(2 * Math.PI * hz * i / sampleRate))];

    /// <summary>The band whose edges contain <paramref name="hz"/>.</summary>
    private static int BandOf(double hz)
        => Enumerable.Range(0, SpectrumAnalyzer.Bands).Single(b => hz >= SpectrumAnalyzer.BandEdges[b] && hz < SpectrumAnalyzer.BandEdges[b + 1]);

    private static int Loudest(float[] levels) => Array.IndexOf(levels, levels.Max());

    // ---- analysis ----------------------------------------------------------------------------------------------

    [Fact]
    public void Bands_are_log_spaced_from_40_hz_to_16_khz()
    {
        var edges = SpectrumAnalyzer.BandEdges;
        Assert.Equal(17, edges.Count);
        Assert.Equal(40, edges[0], 6);
        Assert.Equal(16_000, edges[^1], 6);
        var ratio = edges[1] / edges[0];
        Assert.All(Enumerable.Range(1, 16), b => Assert.Equal(ratio, edges[b] / edges[b - 1], 6));
    }

    [Theory]
    [InlineData(1000, 44_100)]
    [InlineData(1000, 48_000)]
    [InlineData(60, 44_100)]
    [InlineData(250, 48_000)]
    [InlineData(4000, 44_100)]
    [InlineData(12_000, 48_000)]
    public void A_full_scale_sine_lights_its_band(double hz, int sampleRate)
    {
        var levels = SpectrumAnalyzer.Analyze(Sine(hz, sampleRate), sampleRate);

        var band = BandOf(hz);
        Assert.Equal(band, Loudest(levels));
        Assert.InRange(levels[band], 0.95f, 1f);
        Assert.All(Enumerable.Range(0, SpectrumAnalyzer.Bands).Where(b => Math.Abs(b - band) > 2), b => Assert.True(levels[b] < 0.35f, $"band {b}: {levels[b]}"));
    }

    [Fact]
    public void Silence_gives_zeros()
        => Assert.All(SpectrumAnalyzer.Analyze(new float[SpectrumAnalyzer.FftSize], 44_100), l => Assert.Equal(0f, l));

    [Fact]
    public void Levels_follow_a_db_scale()
    {
        var full = SpectrumAnalyzer.Analyze(Sine(1000, 48_000), 48_000)[BandOf(1000)];
        var half = SpectrumAnalyzer.Analyze(Sine(1000, 48_000, 0.5f), 48_000)[BandOf(1000)]; // -6 dB
        var faint = SpectrumAnalyzer.Analyze(Sine(1000, 48_000, 0.0001f), 48_000)[BandOf(1000)]; // -80 dB

        Assert.Equal(full - (6.02f / 60), half, 0.02f);
        Assert.Equal(0f, faint);
    }

    [Fact]
    public void Bands_above_the_nyquist_frequency_are_zero()
    {
        var levels = SpectrumAnalyzer.Analyze(Sine(1000, 8000), 8000);

        Assert.Equal(BandOf(1000), Loudest(levels));
        Assert.All(Enumerable.Range(0, SpectrumAnalyzer.Bands).Where(b => SpectrumAnalyzer.BandEdges[b] >= 4000), b => Assert.Equal(0f, levels[b]));
    }

    [Fact]
    public void The_analysis_needs_one_fft_of_samples()
        => Assert.Throws<ArgumentException>(() => SpectrumAnalyzer.Analyze(new float[100], 44_100));

    // ---- tap ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(44_100, 1, 44_100)]
    [InlineData(48_000, 2, 48_000)]
    [InlineData(96_000, 2, 48_000)]
    [InlineData(192_000, 2, 48_000)]
    [InlineData(352_800, 2, 44_100)] // DSD64 converted to PCM
    public void The_tap_mixes_to_mono_and_averages_hi_res_rates_down(int rate, int channels, int expectedRate)
    {
        var tap = new SpectrumTap { Enabled = true };
        var mono = Sine(1000, rate, count: SpectrumAnalyzer.FftSize * (rate / expectedRate));
        var interleaved = mono.SelectMany(s => Enumerable.Repeat(s, channels)).ToArray();

        tap.Write(interleaved, new PcmFormat(rate, channels));
        var samples = new float[SpectrumAnalyzer.FftSize];
        tap.CopyLatest(samples);

        Assert.Equal(expectedRate, tap.SampleRate);
        var levels = SpectrumAnalyzer.Analyze(samples, tap.SampleRate);
        Assert.Equal(BandOf(1000), Loudest(levels));
        Assert.True(levels[BandOf(1000)] > 0.9f);
    }

    [Fact]
    public void The_tap_pads_with_zeros_until_it_has_enough_and_restarts_on_a_new_format()
    {
        var tap = new SpectrumTap();
        tap.Write([1f, 1f, 1f], new PcmFormat(44_100, 1));
        var samples = new float[8];
        tap.CopyLatest(samples);
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1], samples);

        tap.Write([0.5f, 0.5f], new PcmFormat(48_000, 2));
        tap.CopyLatest(samples);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0.5f], samples);
    }

    [Fact]
    public void Output_samples_are_bit_identical_with_and_without_the_tap()
    {
        float[] Play(bool tap)
        {
            var decoders = new FakeDecoderFactory().With(TestAudio.Track("/s.flac", sampleRate: 48_000, seconds: 1));
            decoders.Waves["/s.flac"] = (frame, channel) => (float)Math.Sin(frame * 0.0123 * (channel + 1)) * 0.7f;
            var outputs = new ManualOutputProvider();
            var player = new Player(decoders, outputs) { Volume = 0.8f };
            _owned.Add(player);
            player.SpectrumTap.Enabled = tap;
            player.Load("/s.flac");
            player.Play();
            return outputs.Output.Pump(48_000);
        }

        Assert.Equal(Play(tap: false), Play(tap: true));
    }

    // ---- meter -------------------------------------------------------------------------------------------------

    private (Player Player, ManualOutputProvider Outputs, SpectrumMeter Meter, FakeTimeProvider Time) Meter(double hz = 1000, int rate = 48_000)
    {
        var decoders = new FakeDecoderFactory().With(TestAudio.Track("/m.flac", sampleRate: rate, seconds: 10));
        decoders.Waves["/m.flac"] = FakeDecoderFactory.Sine(hz, rate);
        var outputs = new ManualOutputProvider();
        var player = new Player(decoders, outputs);
        var time = new FakeTimeProvider();
        var meter = new SpectrumMeter(player, time);
        _owned.Add(player);
        _owned.Add(meter);
        player.Load("/m.flac");
        return (player, outputs, meter, time);
    }

    [Fact]
    public void Nothing_is_copied_or_computed_without_a_subscriber()
    {
        var (player, outputs, meter, time) = Meter();
        player.Play();
        outputs.Output.Pump(SpectrumAnalyzer.FftSize * 2);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(meter.Active);
        Assert.False(player.SpectrumTap.Enabled);
        Assert.Equal(0, player.SpectrumTap.SampleRate); // never written
    }

    [Fact]
    public void Subscribers_get_levels_about_30_times_a_second_while_playing()
    {
        var (player, outputs, meter, time) = Meter();
        var updates = new List<float[]>();
        using var subscription = meter.Subscribe(updates.Add);
        Assert.True(player.SpectrumTap.Enabled);
        player.Play();
        outputs.Output.Pump(SpectrumAnalyzer.FftSize * 4);

        for (var i = 0; i < 30; i++)
        {
            time.Advance(SpectrumMeter.Interval);
        }

        Assert.Equal(30, updates.Count);
        Assert.All(updates, u => Assert.Equal(SpectrumAnalyzer.Bands, u.Length));
        Assert.Equal(BandOf(1000), Loudest(updates[^1]));
        Assert.True(updates[^1][BandOf(1000)] > 0.9f);
    }

    [Fact]
    public void A_muted_player_still_shows_the_music()
    {
        var (player, outputs, meter, time) = Meter();
        var updates = new List<float[]>();
        using var subscription = meter.Subscribe(updates.Add);
        player.Volume = 0;
        player.Play();
        Assert.All(outputs.Output.Pump(SpectrumAnalyzer.FftSize * 2), s => Assert.Equal(0f, s));

        time.Advance(SpectrumMeter.Interval);

        Assert.True(updates.Single()[BandOf(1000)] > 0.9f);
    }

    [Fact]
    public void Pausing_sends_zeros_once_then_nothing_until_playing_again()
    {
        var (player, outputs, meter, time) = Meter();
        var updates = new List<float[]>();
        using var subscription = meter.Subscribe(updates.Add);
        player.Play();
        outputs.Output.Pump(SpectrumAnalyzer.FftSize * 2);
        time.Advance(SpectrumMeter.Interval);

        player.Pause();
        time.Advance(SpectrumMeter.Interval);
        time.Advance(SpectrumMeter.Interval);
        time.Advance(SpectrumMeter.Interval);

        Assert.Equal(2, updates.Count);
        Assert.All(updates[1], l => Assert.Equal(0f, l));

        player.Play();
        time.Advance(SpectrumMeter.Interval);
        Assert.Equal(3, updates.Count);
    }

    [Fact]
    public void Stopped_players_send_zeros_once()
    {
        var (_, _, meter, time) = Meter();
        var updates = new List<float[]>();
        using var subscription = meter.Subscribe(updates.Add);

        time.Advance(SpectrumMeter.Interval * 5);

        Assert.All(updates.Single(), l => Assert.Equal(0f, l));
    }

    [Fact]
    public void The_last_unsubscribe_stops_the_copying_and_the_timer()
    {
        var (player, outputs, meter, time) = Meter();
        var a = new List<float[]>();
        var b = new List<float[]>();
        var first = meter.Subscribe(a.Add);
        var second = meter.Subscribe(b.Add);
        player.Play();
        outputs.Output.Pump(SpectrumAnalyzer.FftSize);

        first.Dispose();
        first.Dispose(); // twice is harmless
        time.Advance(SpectrumMeter.Interval);
        Assert.Empty(a);
        Assert.Single(b);
        Assert.True(player.SpectrumTap.Enabled);

        second.Dispose();
        time.Advance(SpectrumMeter.Interval * 3);
        Assert.Single(b);
        Assert.False(player.SpectrumTap.Enabled);
        Assert.False(meter.Active);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_others()
    {
        var (player, outputs, meter, time) = Meter();
        var updates = new List<float[]>();
        using var bad = meter.Subscribe(_ => throw new InvalidOperationException("broken"));
        using var good = meter.Subscribe(updates.Add);
        player.Play();
        outputs.Output.Pump(SpectrumAnalyzer.FftSize);

        time.Advance(SpectrumMeter.Interval);

        Assert.Single(updates);
    }

    [Fact]
    public void A_disposed_meter_refuses_subscriptions()
    {
        var (player, _, meter, _) = Meter();
        using (meter.Subscribe(_ => { }))
        {
            meter.Dispose();
            Assert.False(player.SpectrumTap.Enabled);
        }

        Assert.Throws<ObjectDisposedException>(() => meter.Subscribe(_ => { }));
    }
}
