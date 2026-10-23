using System.Collections.Concurrent;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;
using MusicSpin.Core.Normalization;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Normalization;

/// <summary>
/// The background levelling service with a fake store and fake decoders. Nothing here sleeps: the tests wait for the
/// service's own <c>Changed</c> signal, or for a gate they open themselves.
/// </summary>
public sealed class NormalizationServiceTests : IDisposable
{
    private readonly string _dir = TempDir.Create("normalize");
    private readonly List<NormalizationService> _services = [];
    private readonly SemaphoreSlim _changes = new(0);

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        TempDir.TryDelete(_dir);
    }

    private string File(string name, int seconds = 3)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, name);
        return path;
    }

    private static TestDecoders Decoders(params (string Path, double Dbfs)[] tracks)
    {
        var decoders = new TestDecoders();
        foreach (var (path, dbfs) in tracks)
        {
            decoders.Add(path, dbfs);
        }

        return decoders;
    }

    private NormalizationService Create(TestDecoders decoders, FakeStore? store = null, NormalizationSettings? settings = null, Action<string>? log = null,
        int? maxEntries = null, TimeSpan? joinTimeout = null)
    {
        var service = new NormalizationService(decoders, store ?? new FakeStore(), settings, log)
        {
            MaxEntries = maxEntries ?? 8192,
            JoinTimeout = joinTimeout ?? NormalizationService.DefaultJoinTimeout,
        };
        service.Changed += (_, _) => _changes.Release();
        _services.Add(service);
        return service;
    }

    /// <summary>Waits for <paramref name="condition"/>, re-checking each time the service says something changed.</summary>
    private void WaitUntil(Func<bool> condition, string what)
    {
        while (!condition())
        {
            if (!_changes.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken))
            {
                Assert.Fail("Timed out waiting for: " + what);
            }
        }
    }

    /// <summary>Starts a track the way the queue does and waits for the resolver thread to deliver what it knows.</summary>
    private NormalizationGain Prepared(NormalizationService service, string path, bool albumContext = false)
    {
        service.Prepare(path, albumContext);
        WaitUntil(() => service.Known(path) is not null, "the lookup");
        return service.Prepare(path, albumContext);
    }

    [Fact]
    public void A_track_that_starts_is_not_looked_up_on_the_callers_thread_and_gets_its_gain_when_the_resolver_delivers_it()
    {
        var path = File("start.flac");
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(-5, 0.8, null, null)));
        var service = Create(Decoders((path, -30)), store);
        var caller = Environment.CurrentManagedThreadId;

        var first = service.Prepare(path, false);

        Assert.Equal(GainSource.Unknown, first.Source); // the answer for a track nobody has asked about does not wait for the library
        WaitUntil(() => service.Known(path) is not null, "the lookup");
        Assert.Equal((-5.0, GainSource.TrackTag), (service.Prepare(path, false).Db, service.Prepare(path, false).Source));
        Assert.DoesNotContain(caller, store.LoadThreads); // the library was read on the resolver's thread
        Assert.NotEmpty(store.LoadThreads);
    }

    [Fact]
    public void A_track_that_starts_goes_to_the_front_of_the_lookup()
    {
        var a = File("a-front.flac");
        var b = File("b-front.flac");
        var c = File("c-front.flac");
        var store = new FakeStore().With(a, new TrackLoudness(new ReplayGainTags(-1, 0.9, null, null)))
            .With(b, new TrackLoudness(new ReplayGainTags(-2, 0.9, null, null)))
            .With(c, new TrackLoudness(new ReplayGainTags(-3, 0.9, null, null)));
        using var inLoad = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforeFirstLoad = () =>
        {
            inLoad.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var service = Create(Decoders((a, -30), (b, -30), (c, -30)), store);
        service.Want([a]);
        Assert.True(inLoad.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)); // the resolver is busy with a
        service.Want([a, b, c]); // b and c wait their turn ...
        service.Prepare(c, false); // ... but c starts now: it jumps the queue

        release.Set();
        WaitUntil(() => service.Known(b) is not null && service.Known(c) is not null, "b and c");

        var batches = store.LoadedBatches;
        Assert.Equal(a, batches[0][0]);
        Assert.Equal(c, batches[1][0]); // c leads the batch that followed
    }

    [Fact]
    public void A_tagged_track_gets_its_tag_gain_and_is_never_analysed()
    {
        var path = File("tagged.flac");
        var decoders = Decoders((path, -30));
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(-5, 0.8, null, null)));
        using var service = Create(decoders, store);

        var gain = Prepared(service, path);

        Assert.Equal((-5.0, GainSource.TrackTag), (gain.Db, gain.Source));
        service.Want([path]);
        Assert.Equal(0, service.PendingAnalysis);
        service.Dispose(); // joins the workers
        Assert.Empty(decoders.OpenedPaths);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void An_untagged_track_plays_unchanged_until_the_background_analysis_finishes_then_gets_its_gain()
    {
        var path = File("plain.flac");
        var store = new FakeStore();
        var service = Create(Decoders((path, -30)), store);

        service.Want([path]);
        Assert.Equal(GainSource.Unknown, service.GainFor(path, false).Source); // never waits for the analysis

        WaitUntil(() => service.GainFor(path, false).Source == GainSource.Analysis, "the analysis");
        var gain = service.GainFor(path, false);
        // A -30 dBFS stereo sine measures about -30 LUFS: +12 dB would reach -18 (its peak, -30 dBFS, leaves plenty of room),
        // but a boost never goes past the safe maximum.
        Assert.Equal(GainCalculator.MaxBoostDb, gain.Db, 6);
        Assert.False(gain.Limited); // the cap is not the clipping limit

        WaitUntil(() => store.Saved.Count == 1, "the result to be kept");
        var saved = Assert.Single(store.Saved);
        Assert.Equal(path, saved.Path);
        Assert.InRange(saved.Lufs!.Value, -30.2, -29.8);
        Assert.InRange(saved.TruePeak, 0.030, 0.033);
        var stamp = FileStamp.Read(path)!.Value;
        Assert.Equal((stamp.SizeBytes, stamp.ModifiedUtcTicks), (saved.SizeBytes, saved.ModifiedUtcTicks));
    }

    [Fact]
    public void A_tag_gain_without_a_peak_is_held_back_until_the_true_peak_is_measured_then_it_may_boost()
    {
        // An Opus R128 tag or a gain-only MP3: +4 dB, but no peak to say how far it may be boosted.
        var path = File("opus.opus");
        var decoders = Decoders((path, -30));
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(4, null, null, null)));
        var service = Create(decoders, store);

        Assert.Equal((1.0, true), (Prepared(service, path).Linear, service.GainFor(path, false).Limited)); // no boost without a peak
        service.Want([path]);
        WaitUntil(() => service.GainFor(path, false).Db > 3.9, "the analysis");

        var gain = service.GainFor(path, false);
        Assert.Equal((4.0, GainSource.TrackTag, false), (Math.Round(gain.Db, 6), gain.Source, gain.Limited)); // the tag's gain, the measured peak
        Assert.Contains(path, decoders.OpenedPaths);
    }

    [Fact]
    public void A_tag_gain_with_its_peak_is_never_measured()
    {
        var path = File("both.flac");
        var decoders = Decoders((path, -30));
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(4, 0.5, null, null)));
        var service = Create(decoders, store);
        Prepared(service, path);

        service.Want([path]);
        service.Dispose();

        Assert.Empty(decoders.OpenedPaths);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_file_whose_size_and_time_cannot_be_read_is_levelled_for_the_session_but_not_kept_in_the_library()
    {
        var ghost = Path.Combine(_dir, "ghost.flac"); // the fake decoder serves it, the disk has no such file
        var store = new FakeStore();
        var service = Create(Decoders((ghost, -30)), store);

        service.Want([ghost]);
        WaitUntil(() => service.GainFor(ghost, false).Source == GainSource.Analysis, "the analysis");
        service.Dispose();

        Assert.Empty(store.SaveAttempts);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void The_tracks_that_play_now_and_next_are_never_dropped_when_the_memory_of_tracks_is_full()
    {
        var paths = Enumerable.Range(1, 6).Select(i => File($"t{i}.flac")).ToArray();
        var store = new FakeStore();
        foreach (var path in paths)
        {
            store.With(path, new TrackLoudness(new ReplayGainTags(-2, 0.9, null, null)));
        }

        var service = Create(Decoders(), store, maxEntries: 3);
        service.Want(paths[..3]);
        WaitUntil(() => paths[..3].All(p => service.Known(p) is not null), "the first three");

        service.Want([paths[2], paths[3]]); // t3 plays, t4 is next: the memory is full, t1 and t2 are not needed any more
        WaitUntil(() => service.Known(paths[3]) is not null, "t4");

        Assert.NotNull(service.Known(paths[2])); // the playing track keeps its gain
        Assert.Null(service.Known(paths[0]));
        Assert.Null(service.Known(paths[1]));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Disposing_does_not_wait_for_ever_for_a_decoder_that_is_stuck()
    {
        var path = File("stuck.flac");
        var decoders = Decoders((path, -30));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        decoders.BeforeFirstRead = _ =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30)); // a disk that never answers
        };
        var log = new ConcurrentQueue<string>();
        var service = Create(decoders, log: log.Enqueue, joinTimeout: TimeSpan.FromMilliseconds(100));
        service.Want([path]);
        Assert.True(started.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        await Task.Run(service.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Contains(log, l => l.Contains("did not stop in time", StringComparison.Ordinal));
        release.Set();
    }

    [Fact]
    public void A_cached_analysis_is_used_without_decoding_again()
    {
        var path = File("cached.flac");
        var decoders = Decoders((path, -30));
        var store = new FakeStore().With(path, new TrackLoudness(ReplayGainTags.None, -26, 0.2, Analyzed: true));
        var service = Create(decoders, store);

        var gain = Prepared(service, path);
        service.Want([path]);
        service.Dispose();

        Assert.Equal((GainCalculator.MaxBoostDb, GainSource.Analysis), (Math.Round(gain.Db, 6), gain.Source));
        Assert.Empty(decoders.OpenedPaths);
    }

    [Fact]
    public void Levelling_off_gives_unity_and_measures_nothing()
    {
        var path = File("off.flac");
        var decoders = Decoders((path, -30));
        var service = Create(decoders, settings: NormalizationSettings.Disabled);

        Assert.Equal((1.0, GainSource.Off), (service.Prepare(path, false).Linear, service.GainFor(path, false).Source));
        service.Want([path]);
        Assert.Equal(0, service.PendingAnalysis);
        service.Dispose();
        Assert.Empty(decoders.OpenedPaths);
    }

    [Fact]
    public void Turning_levelling_off_during_an_analysis_cancels_it_and_keeps_nothing()
    {
        var path = File("cancel.flac");
        var decoders = Decoders((path, -30));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        decoders.BeforeFirstRead = _ =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var store = new FakeStore();
        var service = Create(decoders, store);

        service.Want([path]);
        Assert.True(started.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        service.Configure(NormalizationSettings.Disabled);
        release.Set();
        service.Dispose();

        Assert.Empty(store.Saved);
        Assert.Equal(GainSource.Off, service.GainFor(path, false).Source);
    }

    [Fact]
    public void A_busy_library_is_asked_again_instead_of_being_taken_for_an_unknown_track()
    {
        var path = File("busy.flac");
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(-4, 0.9, null, null)));
        store.Busy = true;
        var service = Create(Decoders((path, -30)), store);

        Assert.Equal(GainSource.Unknown, service.Prepare(path, false).Source);
        Assert.True(store.Loads.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)); // the first attempt, while the library is busy
        Assert.Null(service.Known(path)); // not remembered as "no tags"

        store.Busy = false;
        Assert.Equal(GainSource.TrackTag, Prepared(service, path).Source);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_store_that_throws_leaves_the_track_playing_unchanged_and_is_logged()
    {
        var path = File("broken-store.flac");
        var log = new ConcurrentQueue<string>();
        var store = new FakeStore { ThrowOnLoad = true };
        using var logged = new SemaphoreSlim(0);
        var service = Create(Decoders((path, -30)), store, log: m =>
        {
            log.Enqueue(m);
            logged.Release();
        });

        var gain = service.Prepare(path, false);
        Assert.True(logged.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)); // the resolver thread logged it

        Assert.Equal((1.0, GainSource.Unknown), (gain.Linear, gain.Source));
        Assert.Contains(log, l => l.Contains("could not read the library", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_file_that_cannot_be_decoded_is_given_up_on_for_the_session_without_taking_the_worker_down()
    {
        var bad = File("bad.flac");
        var good = File("good.flac");
        var decoders = Decoders((good, -30));
        decoders.Fail.Add(bad);
        var log = new ConcurrentQueue<string>();
        var service = Create(decoders, log: log.Enqueue);

        service.Want([bad]);
        WaitUntil(() => decoders.OpenedPaths.Count == 1 && service.PendingAnalysis == 0, "the failed attempt");
        service.Want([bad]); // asking again does not retry
        service.Want([good]);
        WaitUntil(() => service.GainFor(good, false).Source == GainSource.Analysis, "the next track to be analysed");

        Assert.Equal(GainSource.Unknown, service.GainFor(bad, false).Source);
        Assert.Equal(1, decoders.OpenedPaths.Count(p => p == bad));
        Assert.Contains(log, l => l.Contains("could not analyse", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_analysis_of_a_file_that_changed_meanwhile_is_thrown_away()
    {
        var path = File("moving.flac");
        var decoders = Decoders((path, -30));
        decoders.BeforeFirstRead = p => System.IO.File.SetLastWriteTimeUtc(p, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // a tagger rewrites it
        var store = new FakeStore();
        var service = Create(decoders, store);

        service.Want([path]);
        WaitUntil(() => decoders.OpenedPaths.Count >= 1 && service.PendingAnalysis == 0, "the analysis");
        service.Dispose();

        Assert.Empty(store.Saved);
        Assert.Null(service.Known(path)); // looked up again when it is next wanted
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_result_the_library_could_not_take_is_kept_and_saved_with_the_next_one()
    {
        var first = File("a.flac");
        var second = File("b.flac");
        var store = new FakeStore { SaveResult = false };
        var service = Create(Decoders((first, -30), (second, -25)), store);

        service.Want([first]);
        WaitUntil(() => store.SaveAttempts.Count == 1, "the first save attempt");
        store.SaveResult = true;
        service.Want([second]);
        WaitUntil(() => store.Saved.Count == 2, "both results to be kept");

        Assert.Equal([first, second], store.Saved.Select(u => u.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Tracks_that_are_no_longer_wanted_are_dropped_from_the_analysis_queue()
    {
        var a = File("a.flac");
        var b = File("b.flac");
        var c = File("c.flac");
        var decoders = Decoders((a, -30), (b, -30), (c, -30));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        decoders.BeforeFirstRead = p =>
        {
            if (p == a)
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            }
        };
        var service = Create(decoders);

        service.Want([a, b, c]);
        Assert.True(started.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        WaitUntil(() => service.Known(b) is not null && service.Known(c) is not null, "all three to be looked up");
        service.Want([c]); // the queue moved on: b is not needed any more
        release.Set();
        WaitUntil(() => service.GainFor(c, false).Source == GainSource.Analysis, "c");
        service.Dispose();

        Assert.DoesNotContain(b, decoders.OpenedPaths);
        Assert.Contains(c, decoders.OpenedPaths);
    }

    [Fact]
    public void Changing_the_settings_raises_changed_and_the_gain_follows_but_the_same_settings_do_not()
    {
        var path = File("settings.flac");
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(-6, 0.5, null, null)));
        var service = Create(Decoders((path, -30)), store);
        Assert.Equal(-6.0, Prepared(service, path).Db, 6);

        var raised = 0;
        service.Changed += (_, _) => raised++;
        service.Configure(NormalizationSettings.Default);
        Assert.Equal(0, raised);

        service.Configure(new NormalizationSettings(NormalizationMode.Track, -23, 1));
        Assert.Equal(1, raised);
        Assert.Equal(-6 - 5 + 1.0, service.GainFor(path, false).Db, 6);

        service.Configure(new NormalizationSettings(NormalizationMode.Track, -99, 99)); // clamped
        Assert.Equal(new NormalizationSettings(NormalizationMode.Track, -30, 12), service.Settings);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void After_dispose_nothing_throws_and_nothing_is_measured()
    {
        var path = File("late.flac");
        var decoders = Decoders((path, -30));
        var service = Create(decoders);
        service.Dispose();
        service.Dispose();

        service.Want([path]);
        service.Configure(NormalizationSettings.Disabled); // ignored
        Assert.Equal(NormalizationSettings.Default, service.Settings);
        Assert.Equal(GainSource.Unknown, service.GainFor(path, false).Source);
        Assert.Empty(decoders.OpenedPaths);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_changed_handler_that_throws_does_not_stop_the_workers()
    {
        var path = File("handler.flac");
        var log = new ConcurrentQueue<string>();
        var decoders = Decoders((path, -30));
        var service = new NormalizationService(decoders, new FakeStore(), log: log.Enqueue);
        _services.Add(service);
        service.Changed += (_, _) => _changes.Release();
        service.Changed += (_, _) => throw new InvalidOperationException("boom");

        service.Want([path]);
        WaitUntil(() => service.GainFor(path, false).Source == GainSource.Analysis, "the analysis");
        Assert.Contains(log, l => l.Contains("change handler failed", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new NormalizationService(null!, new FakeStore()));
        Assert.Throws<ArgumentNullException>(() => new NormalizationService(new TestDecoders(), null!));
        var service = Create(new TestDecoders());
        Assert.Throws<ArgumentNullException>(() => service.Configure(null!));
        Assert.Throws<ArgumentNullException>(() => service.Want(null!));
    }

    [Fact]
    public void The_same_file_by_another_spelling_is_one_track()
    {
        var path = File("Spelling.flac");
        var store = new FakeStore().With(path, new TrackLoudness(new ReplayGainTags(-3, 0.9, null, null)));
        var service = Create(Decoders((path, -30)), store);
        var spelled = Path.Combine(_dir, ".", "Spelling.flac");

        Assert.Equal(NormalizationService.KeyOf(path), NormalizationService.KeyOf(spelled));
        Assert.Equal(-3.0, Prepared(service, spelled).Db, 6);
    }

    // -------------------------------------------------------------------------------------------- fakes

    private sealed class FakeStore : ILoudnessStore
    {
        private readonly Dictionary<string, TrackLoudness> _known = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        private int _loadCalls;

        public volatile bool Busy;

        /// <summary>Released after every <see cref="Load"/> call (the resolver thread's attempts).</summary>
        public SemaphoreSlim Loads { get; } = new(0);

        /// <summary>Runs inside the first <see cref="Load"/> (a gate for the test).</summary>
        public Action? BeforeFirstLoad { get; set; }

        public int LoadCalls => Volatile.Read(ref _loadCalls);

        public ConcurrentQueue<int> LoadThreadQueue { get; } = new();

        public List<int> LoadThreads => [.. LoadThreadQueue];

        public ConcurrentQueue<string[]> LoadedBatchQueue { get; } = new();

        public List<string[]> LoadedBatches => [.. LoadedBatchQueue];

        public bool ThrowOnLoad { get; set; }

        public bool SaveResult { get; set; } = true;

        public ConcurrentQueue<LoudnessUpdate> SaveAttempts { get; } = new();

        public List<LoudnessUpdate> Saved { get; } = [];

        public FakeStore With(string path, TrackLoudness loudness)
        {
            _known[NormalizationService.KeyOf(path)] = loudness;
            return this;
        }

        public IReadOnlyDictionary<string, TrackLoudness>? Load(IReadOnlyCollection<string> paths)
        {
            LoadThreadQueue.Enqueue(Environment.CurrentManagedThreadId);
            LoadedBatchQueue.Enqueue([.. paths]);
            if (Interlocked.Increment(ref _loadCalls) == 1)
            {
                BeforeFirstLoad?.Invoke();
            }

            Loads.Release();
            if (ThrowOnLoad)
            {
                throw new InvalidOperationException("library exploded");
            }

            if (Busy)
            {
                return null;
            }

            var result = new Dictionary<string, TrackLoudness>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var key = NormalizationService.KeyOf(path);
                if (_known.TryGetValue(key, out var found))
                {
                    result[key] = found;
                }
            }

            return result;
        }

        public bool Save(IReadOnlyList<LoudnessUpdate> updates)
        {
            foreach (var update in updates)
            {
                SaveAttempts.Enqueue(update);
            }

            if (!SaveResult)
            {
                return false;
            }

            lock (_gate)
            {
                Saved.AddRange(updates);
            }

            return true;
        }
    }

    /// <summary>Stereo 48 kHz three-second sines at chosen levels; opens are recorded, reads can be hooked.</summary>
    private sealed class TestDecoders : IAudioDecoderFactory
    {
        private readonly Dictionary<string, double> _levels = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _opened = new();

        public string Name => "test";

        public bool IsAvailable => true;

        public HashSet<string> Fail { get; } = new(StringComparer.Ordinal);

        public Action<string>? BeforeFirstRead { get; set; }

        public List<string> OpenedPaths => [.. _opened];

        public void Add(string path, double dbfs) => _levels[path] = dbfs;

        public TrackInfo Probe(string path) => throw new NotSupportedException();

        public IReadOnlyList<AudioFormatSupport> GetSupportedFormats() => [];

        public IAudioDecoder Open(string path)
        {
            _opened.Enqueue(path);
            if (Fail.Contains(path))
            {
                throw new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat, "cannot decode " + path);
            }

            var amplitude = (float)Math.Pow(10, _levels[path] / 20);
            var inner = new FakeDecoder(TestAudio.Track(path, sampleRate: 48_000, seconds: 3), FakeDecoderFactory.Sine(997, 48_000, amplitude));
            return new Hooked(inner, () => BeforeFirstRead?.Invoke(path));
        }
    }

    private sealed class Hooked(FakeDecoder inner, Action beforeFirstRead) : IAudioDecoder
    {
        private bool _first = true;

        public TrackInfo Track => inner.Track;

        public PcmFormat Format => inner.Format;

        public TimeSpan Position => inner.Position;

        public bool CanSeek => inner.CanSeek;

        public int Read(Span<float> buffer)
        {
            if (_first)
            {
                _first = false;
                beforeFirstRead();
            }

            return inner.Read(buffer);
        }

        public void Seek(TimeSpan position) => inner.Seek(position);

        public void Dispose() => inner.Dispose();
    }
}
