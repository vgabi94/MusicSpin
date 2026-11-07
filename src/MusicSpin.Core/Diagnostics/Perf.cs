#if PERF_MARKERS
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MusicSpin.Core.Diagnostics;

/// <summary>
/// Operation spans and counters for library, cover, UI and animation work. Compiled only when
/// <c>EnablePerfMarkers</c> is true (<c>PERF_MARKERS</c>). A span or counter is recorded only while a listener is
/// attached (<see cref="AttachTrace"/>, <c>MUSICSPIN_TRACE</c>, or a test <see cref="ActivityListener"/>). With the
/// symbol defined and no listener, <see cref="Begin"/> is a listener check and returns an empty span.
/// </summary>
/// <remarks>
/// These measure work until it finishes (a scan phase, a decode, a list rebuild, an animation session). They are not
/// frame, compositor or GPU times. <c>MUSICSPIN_FPS</c> remains the window's layout and render overlay, and it is not
/// part of this switch. GC gauges here are managed-heap counters, not native bitmap, GPU or process working-set sizes.
/// Tags are counts and small integers. Paths, titles and search text are never tags.
/// </remarks>
public static class Perf
{
    public const string SourceName = "MusicSpin";

    public const string TraceVariable = "MUSICSPIN_TRACE";

    public const string Scan = "library.scan";
    public const string ScanWalk = "library.scan.walk";
    public const string ScanClassify = "library.scan.classify";
    public const string ScanRead = "library.scan.read";
    public const string ScanMoves = "library.scan.moves";
    public const string ScanIndex = "library.scan.index";
    public const string ScanCheckpoint = "library.scan.checkpoint";
    public const string ScanPrune = "library.scan.prune";

    public const string CoverFile = "cover.file";
    public const string CoverResize = "cover.resize";
    public const string CoverLoad = "cover.load";
    public const string CoverDecode = "cover.decode";
    public const string CoverAccent = "cover.accent";
    public const string CoverRecheck = "covers.recheck";

    public const string PageLoad = "ui.page.load";
    public const string PageLayout = "ui.page.layout";
    public const string Navigate = "ui.navigate";
    public const string Reload = "ui.library.reload";
    public const string SpinSession = "ui.spin.session";
    public const string SpectrumSession = "ui.spectrum.session";
    public const string SceneSession = "ui.scene";
    public const string NowPlayingShow = "ui.nowplaying.show";

    /// <summary>No usable cover.</summary>
    public const int ResultNone = 0;

    /// <summary>A cached file or an in-memory bitmap was reused.</summary>
    public const int ResultHit = 1;

    /// <summary>A new cached image was encoded.</summary>
    public const int ResultEncoded = 2;

    /// <summary>Tag <c>page</c> on <see cref="PageLoad"/> and <see cref="PageLayout"/>.</summary>
    public const int PageAlbums = 1;

    /// <summary>Tag <c>page</c> on <see cref="PageLoad"/> and <see cref="PageLayout"/>.</summary>
    public const int PageTracks = 2;

    /// <summary>A playlist or favorites list, which shares the track-list layout.</summary>
    public const int PageTrackList = 3;

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>The meter listeners subscribe to. Cover gauges in the app are added to it.</summary>
    public static Meter Meter { get; } = new(SourceName);

    private static readonly Counter<long> Batches = Meter.CreateCounter<long>("musicspin.scan.batches");
    private static readonly Counter<long> ProgressPosts = Meter.CreateCounter<long>("musicspin.scan.progress_posts");
    private static readonly Counter<long> MemoryHits = Meter.CreateCounter<long>("musicspin.covers.memory_hits");
    private static readonly Counter<long> LibraryChanges = Meter.CreateCounter<long>("musicspin.library.changes");

    private static int _scanIds;
    private static int _envGate;

    static Perf()
    {
        // HeapSizeBytes stays 0 until a collection, so this is the managed total the runtime reports without forcing one.
        Meter.CreateObservableGauge("musicspin.gc.total_memory_bytes", static () => GC.GetTotalMemory(forceFullCollection: false));
        Meter.CreateObservableGauge("musicspin.gc.allocated_bytes", static () => GC.GetTotalAllocatedBytes(precise: false));
        Meter.CreateObservableGauge("musicspin.gc.gen0", static () => (long)GC.CollectionCount(0));
        Meter.CreateObservableGauge("musicspin.gc.gen1", static () => (long)GC.CollectionCount(1));
        Meter.CreateObservableGauge("musicspin.gc.gen2", static () => (long)GC.CollectionCount(2));
    }

    /// <summary>The id of the latest <see cref="Scan"/>, or 0 before the first one. A reload tags this so the two can be tied together.</summary>
    public static int LastScanId => Volatile.Read(ref _scanIds);

    /// <summary>The next scan id. One scan at a time in the app; overlapping scans in tests each get their own id.</summary>
    public static int NextScanId() => Interlocked.Increment(ref _scanIds);

    /// <summary>
    /// Starts a file trace when <see cref="TraceVariable"/> is set. <c>1</c> writes <c>musicspin-trace.jsonl</c> in the
    /// working directory; any other value is the file path. Read once per process, on the first call.
    /// </summary>
    public static void EnableFromEnvironment()
    {
        if (Interlocked.CompareExchange(ref _envGate, 1, 0) != 0)
        {
            return;
        }

        var spec = Environment.GetEnvironmentVariable(TraceVariable);
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        PerfTrace.Start(PerfTrace.Resolve(spec));
    }

    /// <summary>Writes spans and gauge samples to <paramref name="path"/> (the same writer <see cref="EnableFromEnvironment"/> uses).</summary>
    public static void AttachTrace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PerfTrace.Start(path);
    }

    /// <summary>A span, or an empty one when nothing is listening. Empty spans do not allocate an <see cref="Activity"/>.</summary>
    public static PerfSpan Begin(string name)
    {
        EnableFromEnvironment();
        if (!Source.HasListeners())
        {
            return default;
        }

        return new PerfSpan(Source.StartActivity(name, ActivityKind.Internal));
    }

    public static void CountBatches() => Add(Batches);

    public static void CountProgress() => Add(ProgressPosts);

    public static void CountMemoryHits() => Add(MemoryHits);

    public static void CountLibraryChanges() => Add(LibraryChanges);

    private static void Add(Counter<long> counter)
    {
        if (counter.Enabled)
        {
            counter.Add(1);
        }
    }
}

/// <summary>One operation span. Copying it shares the activity. Disposing an empty span does nothing.</summary>
public readonly struct PerfSpan : IDisposable
{
    private readonly Activity? _activity;

    internal PerfSpan(Activity? activity) => _activity = activity;

    public bool IsRecording => _activity is { IsAllDataRequested: true };

    public PerfSpan Tag(string key, int value)
    {
        _activity?.SetTag(key, value);
        return this;
    }

    public void Dispose() => _activity?.Dispose();
}
#endif
