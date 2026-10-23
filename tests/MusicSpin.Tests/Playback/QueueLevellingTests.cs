using System.Collections.Concurrent;
using MusicSpin.Core;
using MusicSpin.Core.Normalization;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>
/// Volume levelling through the queue and player (#139), deterministic: a manual output pulls the audio, ramp decoders make
/// every sample unique, and a fake resolver stands in for the tags / analysis.
/// </summary>
public sealed class QueueLevellingTests : IDisposable
{
    private const int Block = 64;
    private const int RampFrames = 5; // 5 ms at the 1 kHz of the ramp decoders
    private readonly RampDecoderFactory _decoders = new();
    private readonly ManualOutputProvider _outputs = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private string Add(RampSpec spec)
    {
        var path = $"/l/{spec.Id}.flac";
        _decoders.Add(path, spec);
        return path;
    }

    private (Player Player, QueuePlayer Queue, FakeResolver Resolver) Create(bool resolver = true)
    {
        var player = new Player(_decoders, _outputs);
        var queue = new QueuePlayer(player);
        var fake = new FakeResolver();
        if (resolver)
        {
            queue.Normalizer = fake;
        }

        _owned.Add(player);
        _owned.Add(queue);
        return (player, queue, fake);
    }

    private float[] Drain()
    {
        var all = new List<float>();
        while (true)
        {
            var block = _outputs.Output.Pump(Block);
            all.AddRange(block);
            if (block.Length < Block)
            {
                return [.. all];
            }
        }
    }

    private static NormalizationGain Gain(double linear, GainSource source = GainSource.TrackTag)
        => new(linear, 20 * Math.Log10(linear), source, false);

    /// <summary>The factor the output applied to each frame of <paramref name="spec"/> (left channel), given the frames played from <paramref name="offset"/>.</summary>
    private static double[] Factors(float[] output, int offset, RampSpec spec)
        => [.. Enumerable.Range(0, (int)spec.Frames).Select(f => output[(offset + f) * 2] / (double)spec.Sample(f, 0))];

    // ---- the audio ------------------------------------------------------------------------------------------

    [Fact]
    public void Tracks_play_at_their_own_gain_and_the_change_at_a_gapless_boundary_is_a_ramp_not_a_step()
    {
        var a = new RampSpec(1, 400);
        var b = new RampSpec(2, 400);
        var c = new RampSpec(3, 400);
        var (_, queue, resolver) = Create();
        resolver.Set(Add(a), Gain(1.0));
        resolver.Set(Add(b), Gain(0.5));
        resolver.Set(Add(c), Gain(1.0));
        queue.SetQueue([new QueueItem("/l/1.flac"), new QueueItem("/l/2.flac"), new QueueItem("/l/3.flac")]);

        var output = Drain();

        Assert.Equal(1200 * 2, output.Length);
        Assert.Equal(a.Pcm(), output[..800]); // unity: bit-exact
        var fb = Factors(output, 400, b);
        var fc = Factors(output, 800, c);
        Assert.All(fb.Skip(RampFrames), f => Assert.Equal(0.5, f, 6));
        Assert.All(fc.Skip(RampFrames), f => Assert.Equal(1.0, f, 6));
        // across both boundaries the factor moves in small steps only: 0.5/RampFrames at most, never a jump
        double[] all = [.. Enumerable.Repeat(1.0, 400), .. fb, .. fc];
        for (var i = 1; i < all.Length; i++)
        {
            Assert.True(Math.Abs(all[i] - all[i - 1]) <= 0.5 / RampFrames + 1e-5, $"step at frame {i}: {all[i - 1]} -> {all[i]}");
        }
    }

    [Fact]
    public void A_track_without_a_resolver_plays_untouched()
    {
        var a = new RampSpec(1, 300);
        var (_, queue, _) = Create(resolver: false);
        queue.SetQueue([new QueueItem(Add(a))]);

        Assert.Equal(a.Pcm(), Drain());
        Assert.Null(queue.Status().Normalization);
    }

    [Fact]
    public void A_gain_that_arrives_late_moves_the_playing_track_to_it()
    {
        var a = new RampSpec(1, 2000);
        var (_, queue, resolver) = Create();
        var path = Add(a);
        resolver.Set(path, NormalizationGain.Unity(GainSource.Unknown));
        queue.SetQueue([new QueueItem(path)]);
        var head = _outputs.Output.Pump(800);
        Assert.Equal(a.Pcm()[..800], head);

        resolver.Set(path, Gain(0.25, GainSource.Analysis));
        resolver.RaiseChanged(); // the analysis finished
        var status = queue.Status();
        Assert.Equal("analysis", status.Normalization!.Source);
        Assert.Equal(Math.Round(20 * Math.Log10(0.25), 2), status.Normalization.GainDb);
        var rest = Drain();

        var factors = Enumerable.Range(0, 1600).Select(f => rest[f * 2] / (double)a.Sample(400 + f, 0)).ToArray();
        Assert.All(factors.Skip(RampFrames), f => Assert.Equal(0.25, f, 6));
        Assert.True(factors[0] < 1.0 && factors[0] > 0.25); // it started moving at once, in steps
    }

    [Fact]
    public void The_player_ignores_gains_for_entries_it_does_not_know_and_after_dispose()
    {
        var a = new RampSpec(1, 300);
        var (player, queue, _) = Create(resolver: false);
        queue.SetQueue([new QueueItem(Add(a))]);

        player.SetGains(1f, [new TrackGain(new object(), 0.1f)]); // unknown token: nothing plays at it
        Assert.Equal(a.Pcm(), Drain());

        player.Dispose();
        player.SetGains(0.5f, []); // no throw
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_gains_call_that_races_a_gapless_boundary_cannot_give_the_new_track_the_old_ones_gain()
    {
        // The queue computes the gains for "the current track" from its own bookkeeping, which can still say A after the
        // player has crossed into B. The player must find B by its token, not take the first segment for the current one.
        var a = new RampSpec(1, 400);
        var b = new RampSpec(2, 400);
        var (player, queue, resolver) = Create();
        resolver.Set(Add(a), Gain(1.0));
        resolver.Set(Add(b), Gain(0.5));
        var crossed = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackAdvanced += (_, e) => crossed.TrySetResult(e.Token);
        queue.SetQueue([new QueueItem("/l/1.flac"), new QueueItem("/l/2.flac")]);
        var head = _outputs.Output.Pump((400 + 50) * 2); // all of A and 50 frames of B
        var tokenB = await crossed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // the stale call: A is still "current" at its gain, B is known only as a queued entry
        player.SetGains(1f, [new TrackGain(tokenB, 0.5f)]);
        var all = head.Concat(Drain()).ToArray();

        Assert.Equal(800 * 2, all.Length); // both tracks played in full
        var fb = Factors(all, 400, b);
        Assert.All(fb.Skip(RampFrames), f => Assert.Equal(0.5, f, 6)); // B stayed at 0.5 through the stale call
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Missing_gains_are_refused()
    {
        var (player, _, _) = Create(resolver: false);
        Assert.Throws<ArgumentNullException>(() => player.SetGains(1f, null!));
    }

    // ---- what the queue asks the resolver ----------------------------------------------------------------------

    [Fact]
    public void The_resolver_is_asked_about_the_current_track_and_the_ones_decoded_ahead_in_order()
    {
        var (_, queue, resolver) = Create();
        queue.SetQueue([new QueueItem(Add(new RampSpec(1, 5000))), new QueueItem(Add(new RampSpec(2, 5000))), new QueueItem(Add(new RampSpec(3, 5000)))]);

        Assert.Equal(["/l/1.flac", "/l/2.flac", "/l/3.flac"], resolver.LastWanted);
        Assert.Contains("/l/1.flac", resolver.Prepared);
    }

    [Fact]
    public void Album_context_is_for_tracks_of_one_album_played_in_order()
    {
        var (_, queue, resolver) = Create();
        string[] p = [Add(new RampSpec(1, 5000)), Add(new RampSpec(2, 5000)), Add(new RampSpec(3, 5000))];
        queue.SetQueue([
            new QueueItem(p[0], Album: "Kind of Blue", AlbumArtist: "Miles Davis"),
            new QueueItem(p[1], Album: "Kind of Blue", AlbumArtist: "Miles Davis"),
            new QueueItem(p[2], Album: "Another", AlbumArtist: "Someone")]);

        Assert.True(resolver.Context[p[0]]);
        Assert.True(resolver.Context[p[1]]);
        Assert.False(resolver.Context[p[2]]);
    }

    [Fact]
    public void Shuffled_tracks_are_not_an_album_context()
    {
        var (_, queue, resolver) = Create();
        string[] p = [Add(new RampSpec(1, 5000)), Add(new RampSpec(2, 5000))];
        queue.SetQueue([new QueueItem(p[0], Album: "A", AlbumArtist: "X"), new QueueItem(p[1], Album: "A", AlbumArtist: "X")]);
        Assert.True(resolver.Context[p[0]]);

        queue.SetShuffle(true, seed: 3);

        Assert.False(resolver.Context[p[0]]);
        Assert.False(resolver.Context[p[1]]);
    }

    [Fact]
    public void Changing_the_settings_updates_the_status_and_the_gain_of_the_current_track()
    {
        var (_, queue, resolver) = Create();
        var path = Add(new RampSpec(1, 5000));
        resolver.Set(path, Gain(0.5));
        queue.SetQueue([new QueueItem(path)]);
        Assert.Equal(("auto", "track-tag"), (queue.Status().Normalization!.Mode, queue.Status().Normalization!.Source));

        resolver.Settings = new NormalizationSettings(NormalizationMode.Off);
        resolver.Set(path, NormalizationGain.Unity(GainSource.Off));
        resolver.RaiseChanged();

        var level = queue.Status().Normalization!;
        Assert.Equal(("off", "off", 0.0), (level.Mode, level.Source, level.GainDb));
    }

    [Fact]
    public void Nothing_loaded_means_no_gain_in_the_status_but_the_settings_are_still_reported()
    {
        var (_, queue, _) = Create();

        var level = queue.Status().Normalization!;

        Assert.Equal(("auto", -18.0), (level.Mode, level.TargetLufs));
        Assert.Null(level.GainDb);
        Assert.Null(level.Source);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_resolver_change_after_the_queue_is_disposed_does_nothing()
    {
        var (_, queue, resolver) = Create();
        queue.SetQueue([new QueueItem(Add(new RampSpec(1, 5000)))]);
        queue.Dispose();

        resolver.RaiseChanged(); // no throw, no call into the player
        Assert.Equal(0, resolver.Listeners); // it let go of the resolver
    }

    [Fact]
    public void Replacing_the_resolver_stops_listening_to_the_old_one()
    {
        var (_, queue, first) = Create();
        var second = new FakeResolver();

        queue.Normalizer = second;

        Assert.Equal(0, first.Listeners);
        Assert.Equal(1, second.Listeners);
        queue.Normalizer = null;
        Assert.Equal(0, second.Listeners);
        Assert.Null(queue.Status().Normalization);
    }

    private sealed class FakeResolver : IGainResolver
    {
        private readonly ConcurrentDictionary<string, NormalizationGain> _gains = new(StringComparer.Ordinal);
        private int _listeners;

        public NormalizationSettings Settings { get; set; } = NormalizationSettings.Default;

        public ConcurrentDictionary<string, bool> Context { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<string> PreparedQueue { get; } = new();

        public List<string> Prepared => [.. PreparedQueue];

        public IReadOnlyList<string> LastWanted { get; private set; } = [];

        public int Listeners => Volatile.Read(ref _listeners);

        private EventHandler? _changed;

        public event EventHandler? Changed
        {
            add { _changed += value; Interlocked.Increment(ref _listeners); }
            remove { _changed -= value; Interlocked.Decrement(ref _listeners); }
        }

        public void Set(string path, NormalizationGain gain) => _gains[path] = gain;

        public void RaiseChanged() => _changed?.Invoke(this, EventArgs.Empty);

        public void Configure(NormalizationSettings settings) => Settings = settings;

        public NormalizationGain GainFor(string path, bool albumContext)
        {
            Context[path] = albumContext;
            return _gains.TryGetValue(path, out var gain) ? gain : NormalizationGain.Unity(GainSource.Unknown);
        }

        public NormalizationGain Prepare(string path, bool albumContext)
        {
            PreparedQueue.Enqueue(path);
            return GainFor(path, albumContext);
        }

        public void Want(IReadOnlyList<string> paths) => LastWanted = [.. paths];
    }
}
