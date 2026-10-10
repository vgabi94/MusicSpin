using MusicSpin.Core.Audio;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>The ring buffer on its own: single-threaded, the test plays both producer and consumer.</summary>
public sealed class PlaybackBufferTests
{
    private static readonly PcmFormat Stereo = new(1000, 2, 24);
    private int _signals;

    private static TrackSegment Segment(int id) => new(new RampDecoder($"/b/{id}", new RampSpec(id, 1000)), id, TimeSpan.Zero, [], []);

    private PlaybackBuffer Create(int capacity = 16, float volume = 1f)
        => new(Stereo, capacity, Segment(1), () => volume, () => _signals++);

    private static float[] Seq(int from, int count) => [.. Enumerable.Range(from, count).Select(i => (float)i)];

    [Fact]
    public void Samples_wrap_around_the_ring_exactly()
    {
        var buffer = Create(capacity: 10);
        var heard = new List<float>();
        for (var i = 0; i < 20; i++)
        {
            buffer.Write(Seq(i * 6, 6));
            var block = new float[6];
            Assert.Equal(6, buffer.Read(block));
            heard.AddRange(block);
        }

        Assert.Equal(Seq(0, 120), heard);
        Assert.Equal(0, buffer.Underruns);
        Assert.Equal(TimeSpan.FromMilliseconds(60), buffer.Position);
    }

    [Fact]
    public void Capacity_is_whole_frames_and_free_space_tracks_the_fill()
    {
        var buffer = Create(capacity: 15);
        Assert.Equal(14, buffer.Capacity);
        buffer.Write(Seq(0, 8));
        Assert.Equal(8, buffer.Buffered);
        Assert.Equal(6, buffer.Free);
    }

    [Fact]
    public void An_empty_buffer_plays_silence_and_counts_one_underrun_per_callback()
    {
        var buffer = Create();
        buffer.Write(Seq(1, 4));
        var block = Enumerable.Repeat(9f, 10).ToArray();
        Assert.Equal(10, buffer.Read(block));
        Assert.Equal([1, 2, 3, 4, 0, 0, 0, 0, 0, 0], block);
        Assert.Equal(1, buffer.Underruns);
        Assert.Equal(10, buffer.Read(block));
        Assert.Equal(2, buffer.Underruns);
        Assert.Equal(TimeSpan.FromMilliseconds(2), buffer.Position); // silence is not track time
    }

    [Fact]
    public void A_boundary_switches_segment_and_position_at_its_exact_sample()
    {
        var buffer = Create();
        var next = Segment(2);
        buffer.Write(Seq(0, 6));
        Assert.True(buffer.TryAddMarker(MarkerKind.Boundary, next));
        buffer.Write(Seq(100, 6));

        var block = new float[6];
        buffer.Read(block);
        Assert.Equal(1, buffer.Current.Token);
        Assert.Equal(0, buffer.Crossed);
        Assert.Equal(TimeSpan.FromMilliseconds(3), buffer.Position);

        var two = new float[2];
        buffer.Read(two);
        Assert.Equal([100, 101], two);
        Assert.Same(next, buffer.Current);
        Assert.Equal(6, next.StartIndex);
        Assert.Equal(1, buffer.Crossed);
        Assert.Equal(TimeSpan.FromMilliseconds(1), buffer.Position);
        Assert.Equal(1, _signals);
    }

    [Fact]
    public void A_read_spanning_a_boundary_is_continuous()
    {
        var buffer = Create();
        buffer.Write(Seq(0, 4));
        buffer.TryAddMarker(MarkerKind.Boundary, Segment(2));
        buffer.Write(Seq(4, 4));
        var block = new float[8];
        Assert.Equal(8, buffer.Read(block));
        Assert.Equal(Seq(0, 8), block);
        Assert.Equal(1, buffer.Crossed);
    }

    [Fact]
    public void The_end_marker_stops_the_stream_at_the_last_sample()
    {
        var buffer = Create();
        buffer.Write(Seq(0, 4));
        buffer.TryAddMarker(MarkerKind.End, null);
        var block = new float[10];
        Assert.Equal(4, buffer.Read(block));
        Assert.True(buffer.EndReached);
        Assert.Equal(0, buffer.Read(block));
        Assert.Equal(0, buffer.Underruns);
        Assert.Equal(1, _signals);
    }

    [Fact]
    public void Cutting_drops_everything_after_the_first_pending_marker()
    {
        var buffer = Create();
        buffer.Write(Seq(0, 4));
        buffer.TryAddMarker(MarkerKind.Boundary, Segment(2));
        buffer.Write(Seq(50, 4));
        buffer.TryAddMarker(MarkerKind.Boundary, Segment(3));
        buffer.Write(Seq(80, 2));
        buffer.TryAddMarker(MarkerKind.End, null);

        Assert.Equal(2, buffer.CutAtFirstMarker());
        Assert.Equal(4, buffer.Buffered);
        Assert.Equal(0, buffer.PendingMarkers);
        buffer.Write(Seq(4, 2));
        buffer.TryAddMarker(MarkerKind.End, null);
        var block = new float[10];
        Assert.Equal(6, buffer.Read(block));
        Assert.Equal(Seq(0, 6), block[..6]);
        Assert.Equal(1, buffer.Current.Token);
        Assert.Equal(0, buffer.CutAtFirstMarker()); // nothing pending
    }

    [Fact]
    public void Only_a_trailing_unreached_end_can_be_removed()
    {
        var buffer = Create();
        Assert.False(buffer.TryRemoveTrailingEnd());
        buffer.Write(Seq(0, 2));
        buffer.TryAddMarker(MarkerKind.Boundary, Segment(2));
        Assert.False(buffer.TryRemoveTrailingEnd());
        buffer.Write(Seq(2, 2));
        buffer.TryAddMarker(MarkerKind.End, null);
        Assert.True(buffer.TryRemoveTrailingEnd());
        Assert.Equal(1, buffer.PendingMarkers);
    }

    [Fact]
    public void Flush_discards_unplayed_audio_and_markers_and_restarts_the_clock()
    {
        var buffer = Create();
        buffer.Write(Seq(0, 8));
        buffer.TryAddMarker(MarkerKind.End, null);
        buffer.Read(new float[2]);
        var head = Segment(1);
        head.Start = TimeSpan.FromMilliseconds(500);
        buffer.Flush(head);
        Assert.Equal(0, buffer.Buffered);
        Assert.Equal(0, buffer.PendingMarkers);
        Assert.Equal(TimeSpan.FromMilliseconds(500), buffer.Position);
        buffer.Write(Seq(70, 2));
        var block = new float[2];
        buffer.Read(block);
        Assert.Equal([70, 71], block);
        Assert.Equal(TimeSpan.FromMilliseconds(501), buffer.Position);
    }

    [Fact]
    public void Flush_after_the_end_was_reached_lets_the_stream_play_again()
    {
        var buffer = Create();
        buffer.TryAddMarker(MarkerKind.End, null);
        Assert.Equal(0, buffer.Read(new float[2]));
        buffer.Flush(Segment(1));
        Assert.False(buffer.EndReached);
        buffer.Write(Seq(1, 2));
        Assert.Equal(2, buffer.Read(new float[2]));
    }

    [Fact]
    public void Marker_space_is_bounded()
    {
        var buffer = Create();
        for (var i = 0; i < PlaybackBuffer.MaxMarkers; i++)
        {
            Assert.True(buffer.TryAddMarker(MarkerKind.Boundary, Segment(i)));
        }

        Assert.False(buffer.TryAddMarker(MarkerKind.End, null));
    }

    [Fact]
    public void Volume_below_one_scales_and_one_passes_samples_through_untouched()
    {
        var quiet = Create(volume: 0.25f);
        quiet.Write([4, -8]);
        var block = new float[2];
        quiet.Read(block);
        Assert.Equal([1, -2], block);

        var exact = Create();
        float[] odd = [1.0000001f, -0.33333334f];
        exact.Write(odd);
        exact.Read(block);
        Assert.Equal(odd, block);
    }

    [Fact]
    public void Wait_until_readable_returns_for_data_or_a_pending_end_and_times_out_otherwise()
    {
        var buffer = Create();
        Assert.False(buffer.WaitUntilReadable(2, TimeSpan.Zero));
        buffer.Write(Seq(0, 2));
        Assert.True(buffer.WaitUntilReadable(2, TimeSpan.Zero));
        Assert.False(buffer.WaitUntilReadable(4, TimeSpan.Zero));
        buffer.TryAddMarker(MarkerKind.End, null);
        Assert.True(buffer.WaitUntilReadable(4, TimeSpan.Zero));
    }
}

public sealed class PlaybackSettingsTests : IDisposable
{
    private readonly string _root = TempDir.Create("playback-settings");

    public void Dispose() => TempDir.TryDelete(_root);

    [Fact]
    public void Defaults_are_a_two_second_buffer_and_gapless_on()
    {
        var settings = new AppSettings();
        Assert.Equal(2000, settings.PlaybackBufferMs);
        Assert.True(settings.Gapless);
        var options = new PlayerOptions();
        Assert.Equal(2000, options.EffectiveBufferMilliseconds);
        Assert.True(options.Gapless);
    }

    [Theory]
    [InlineData(2000, 2000)]
    [InlineData(100, 100)]
    [InlineData(99, 100)]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(10_000, 10_000)]
    [InlineData(10_001, 10_000)]
    [InlineData(int.MaxValue, 10_000)]
    [InlineData(int.MinValue, 100)]
    public void Buffer_sizes_are_clamped_to_sane_bounds(int requested, int effective)
    {
        Assert.Equal(effective, PlayerOptions.ClampBuffer(requested));
        Assert.Equal(effective, new PlayerOptions { BufferMilliseconds = requested }.EffectiveBufferMilliseconds);
    }

    [Fact]
    public void Settings_json_keys_round_trip_and_bad_values_fall_back_with_a_warning()
    {
        var data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        Directory.CreateDirectory(data.Path);
        var store = new SettingsStore(data);
        store.Save(new AppSettings { PlaybackBufferMs = 500, Gapless = false });
        var json = File.ReadAllText(data.SettingsFile);
        Assert.Contains("\"playbackBufferMs\": 500", json, StringComparison.Ordinal);
        Assert.Contains("\"gapless\": false", json, StringComparison.Ordinal);
        var loaded = store.Load();
        Assert.Equal(500, loaded.PlaybackBufferMs);
        Assert.False(loaded.Gapless);

        File.WriteAllText(data.SettingsFile, """{ "playbackBufferMs": "lots", "gapless": true }""");
        var fallback = store.Load();
        Assert.Equal(2000, fallback.PlaybackBufferMs);
        Assert.NotNull(store.LoadWarning);

        File.WriteAllText(data.SettingsFile, """{ "playbackBufferMs": 999999 }""");
        Assert.Equal(10_000, PlayerOptions.ClampBuffer(store.Load().PlaybackBufferMs));
        Assert.True(store.Load().Gapless); // missing key = default
    }
}
