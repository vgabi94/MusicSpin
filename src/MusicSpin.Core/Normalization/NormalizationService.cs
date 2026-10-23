using MusicSpin.Core.Audio;
using MusicSpin.Core.Library;

namespace MusicSpin.Core.Normalization;

/// <summary>Where tags and analysed loudness are kept between sessions (the library database).</summary>
public interface ILoudnessStore
{
    /// <summary>
    /// What the store knows about the files, by <see cref="NormalizationService.KeyOf"/>. Files it does not know are left
    /// out. An analysis whose file changed since it was stored is not returned (the tags are). Null: the store could not be
    /// read right now (library busy): ask again later.
    /// </summary>
    IReadOnlyDictionary<string, TrackLoudness>? Load(IReadOnlyCollection<string> paths);

    /// <summary>Keeps analysis results. False when it could not (library busy, track unknown): the caller keeps them and tries again.</summary>
    bool Save(IReadOnlyList<LoudnessUpdate> updates);
}

/// <summary>A store that knows nothing and keeps nothing (a player without a library).</summary>
public sealed class NoLoudnessStore : ILoudnessStore
{
    public IReadOnlyDictionary<string, TrackLoudness>? Load(IReadOnlyCollection<string> paths) => new Dictionary<string, TrackLoudness>();

    public bool Save(IReadOnlyList<LoudnessUpdate> updates) => true;
}

/// <summary>An analysis to keep, with the file's size and modification time it was measured on and the meter's <see cref="LoudnessMeter.AlgorithmVersion"/>.</summary>
public sealed record LoudnessUpdate(string Path, double? Lufs, double TruePeak, long SizeBytes, long ModifiedUtcTicks, int Version = LoudnessMeter.AlgorithmVersion);

/// <summary>Size and modification time of a file: an analysis is valid only for the file it was measured on.</summary>
public readonly record struct FileStamp(long SizeBytes, long ModifiedUtcTicks)
{
    public static FileStamp? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>What the player needs from volume levelling (the player never waits on analysis).</summary>
public interface IGainResolver
{
    NormalizationSettings Settings { get; }

    /// <summary>Changes the settings; <see cref="Changed"/> is raised when they differ.</summary>
    void Configure(NormalizationSettings settings);

    /// <summary>Raised (on any thread) when gains may have changed: the settings, or a track's loudness became known.</summary>
    event EventHandler? Changed;

    /// <summary>The track's gain from what is known now; never blocks. An unknown track plays unchanged until it is known (<see cref="Changed"/>).</summary>
    NormalizationGain GainFor(string path, bool albumContext);

    /// <summary>
    /// For a track that is starting now: like <see cref="GainFor"/> (what is known, never blocks, never reads the library on the
    /// caller's thread) but a track that is not known yet goes to the front of the lookup, so its gain follows within a moment
    /// (<see cref="Changed"/>). The tracks that play next are looked up ahead (<see cref="Want"/>), so most are known by then.
    /// </summary>
    NormalizationGain Prepare(string path, bool albumContext);

    /// <summary>The tracks that play now and next, in order: what to look up and analyse (anything else queued earlier is dropped).</summary>
    void Want(IReadOnlyList<string> paths);
}

/// <summary>
/// Volume levelling (#139): per-track gains from ReplayGain / R128 tags, or from an EBU R128 analysis for untagged files,
/// computed on background threads (below normal priority where the OS honours that) and kept in the library. Nothing here blocks playback: a track whose
/// loudness is not known yet plays unchanged and is brought to its gain (a short ramp) when it is.
/// </summary>
public sealed class NormalizationService : IGainResolver, IDisposable
{
    private const int DefaultMaxEntries = 8192;
    private const int ResolveBatch = 32;

    /// <summary>How long <see cref="Dispose"/> waits for each worker thread (a decoder stuck on a dead disk must not hang the app on exit).</summary>
    internal static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(3);

    private readonly IAudioDecoderFactory _decoders;
    private readonly ILoudnessStore _store;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _resolveQueue = [];
    private readonly List<string> _analyzeQueue = [];
    private readonly List<LoudnessUpdate> _unsaved = [];
    private readonly CancellationTokenSource _stopping = new();
    private HashSet<string> _wantedKeys = new(StringComparer.Ordinal);
    private NormalizationSettings _settings;
    private CancellationTokenSource? _analysis;
    private Thread? _resolver;
    private Thread? _analyzer;
    private bool _disposed;

    public NormalizationService(IAudioDecoderFactory decoders, ILoudnessStore store, NormalizationSettings? settings = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(store);
        _decoders = decoders;
        _store = store;
        _log = log;
        _settings = (settings ?? NormalizationSettings.Default).Clamped();
    }

    public event EventHandler? Changed;

    /// <summary>How many tracks are remembered before the ones nobody wants are dropped (tests lower it).</summary>
    internal int MaxEntries { get; init; } = DefaultMaxEntries;

    /// <summary>How long <see cref="Dispose"/> waits for each worker (tests shorten it).</summary>
    internal TimeSpan JoinTimeout { get; init; } = DefaultJoinTimeout;

    public NormalizationSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>The key tracks are remembered by (the library's path key).</summary>
    public static string KeyOf(string path) => LibraryKeys.PathKey(LibraryKeys.NormalizePath(path));

    /// <summary>How many tracks wait for analysis (tests, diagnostics).</summary>
    public int PendingAnalysis
    {
        get
        {
            lock (_gate)
            {
                return _analyzeQueue.Count + (_entries.Values.Any(e => e.Analyzing) ? 1 : 0);
            }
        }
    }

    public void Configure(NormalizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clamped = settings.Clamped();
        lock (_gate)
        {
            if (_disposed || clamped == _settings)
            {
                return;
            }

            _settings = clamped;
            if (!clamped.IsOn)
            {
                _resolveQueue.Clear();
                _analyzeQueue.Clear();
                _analysis?.Cancel(); // nothing is measured while levelling is off
            }
        }

        RaiseChanged();
    }

    public NormalizationGain GainFor(string path, bool albumContext)
    {
        var key = KeyOf(path);
        lock (_gate)
        {
            if (!_settings.IsOn)
            {
                return NormalizationGain.Unity(GainSource.Off);
            }

            if (_entries.TryGetValue(key, out var entry) && entry.Resolved)
            {
                return GainCalculator.Compute(_settings, entry.Loudness, albumContext);
            }

            QueueResolve(path, key);
            return NormalizationGain.Unity(GainSource.Unknown);
        }
    }

    public NormalizationGain Prepare(string path, bool albumContext)
    {
        var key = KeyOf(path);
        lock (_gate)
        {
            if (!_settings.IsOn)
            {
                return NormalizationGain.Unity(GainSource.Off);
            }

            if (_entries.TryGetValue(key, out var entry) && entry.Resolved)
            {
                return GainCalculator.Compute(_settings, entry.Loudness, albumContext);
            }

            QueueResolve(path, key, first: true);
            return NormalizationGain.Unity(GainSource.Unknown);
        }
    }

    public void Want(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            if (_disposed || !_settings.IsOn)
            {
                return;
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            _wantedKeys = wanted;
            foreach (var path in paths)
            {
                var key = KeyOf(path);
                wanted.Add(key);
                if (!_entries.TryGetValue(key, out var entry) || !entry.Resolved)
                {
                    QueueResolve(path, key);
                }
                else if (NeedsAnalysis(entry))
                {
                    QueueAnalysis(path, key, entry);
                }
            }

            _analyzeQueue.RemoveAll(p => !wanted.Contains(KeyOf(p)));
            if (_analyzeQueue.Count > 0)
            {
                EnsureThreads();
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>The entry for a path as far as it is resolved (tests).</summary>
    internal TrackLoudness? Known(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(KeyOf(path), out var e) && e.Resolved ? e.Loudness : null;
        }
    }

    public void Dispose()
    {
        Thread? resolver, analyzer;
        List<LoudnessUpdate> unsaved;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping.Cancel();
            _analysis?.Cancel();
            _resolveQueue.Clear();
            _analyzeQueue.Clear();
            resolver = _resolver;
            analyzer = _analyzer;
            unsaved = [.. _unsaved];
            Monitor.PulseAll(_gate);
        }

        var stopped = JoinWorker(resolver);
        stopped &= JoinWorker(analyzer);
        if (unsaved.Count > 0)
        {
            TrySave(unsaved);
        }

        if (stopped)
        {
            _stopping.Dispose(); // a worker that is still running may yet use it
        }
    }

    private bool JoinWorker(Thread? worker)
    {
        if (worker is null || worker.Join(JoinTimeout))
        {
            return true;
        }

        _log?.Invoke($"loudness: '{worker.Name}' did not stop in time; leaving it to finish in the background");
        return false;
    }

    // ------------------------------------------------------------------------------------------------ queues

    private static bool NeedsAnalysis(Entry e) => !e.Failed && !e.Analyzing && e.Loudness is { IsKnown: false };

    /// <summary>Caller holds the gate.</summary>
    private void QueueResolve(string path, string key, bool first = false)
    {
        if (_disposed)
        {
            return;
        }

        var queued = _resolveQueue.FindIndex(p => KeyOf(p) == key);
        if (queued == 0 || (queued > 0 && !first))
        {
            return;
        }

        if (queued > 0)
        {
            _resolveQueue.RemoveAt(queued); // a track that starts now goes ahead of the rest
        }

        if (first)
        {
            _resolveQueue.Insert(0, path);
        }
        else
        {
            _resolveQueue.Add(path);
        }

        EnsureThreads();
        Monitor.PulseAll(_gate);
    }

    /// <summary>Caller holds the gate.</summary>
    private void QueueAnalysis(string path, string key, Entry entry)
    {
        if (_disposed || !_settings.IsOn || !NeedsAnalysis(entry) || _analyzeQueue.Exists(p => KeyOf(p) == key))
        {
            return;
        }

        _analyzeQueue.Add(path);
        EnsureThreads();
        Monitor.PulseAll(_gate);
    }

    /// <summary>Caller holds the gate.</summary>
    private Entry Resolve(string key, string path, IReadOnlyDictionary<string, TrackLoudness> loaded)
    {
        if (_entries.Count >= MaxEntries)
        {
            // never the tracks that play now and next (they are asked for again each time the queue moves), nor one being measured
            foreach (var stale in _entries.Where(e => !e.Value.Analyzing && !_wantedKeys.Contains(e.Key) && e.Key != key).Select(e => e.Key).ToList())
            {
                _entries.Remove(stale);
            }
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries[key] = entry;
        }
        else if (entry.Resolved)
        {
            return entry; // already known (an analysis kept in memory must not be replaced by an older lookup)
        }

        entry.Loudness = loaded.TryGetValue(key, out var found) ? found : new TrackLoudness(ReplayGainTags.None);
        entry.Resolved = true;
        QueueAnalysis(path, key, entry);
        return entry;
    }

    /// <summary>Caller holds the gate.</summary>
    private void EnsureThreads()
    {
        _resolver ??= Start("MusicSpin loudness lookup", ResolveLoop);
        _analyzer ??= Start("MusicSpin loudness analysis", AnalyzeLoop);
    }

    private static Thread Start(string name, ThreadStart body)
    {
        var thread = new Thread(body) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        thread.Start();
        return thread;
    }

    // ------------------------------------------------------------------------------------------------ workers

    private void ResolveLoop()
    {
        while (true)
        {
            List<string> batch;
            lock (_gate)
            {
                while (_resolveQueue.Count == 0)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                batch = [.. _resolveQueue.Take(ResolveBatch)];
                _resolveQueue.RemoveRange(0, batch.Count);
            }

            var loaded = Load(batch);
            if (loaded is null)
            {
                continue; // the library is busy: the next request for these tracks asks again
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var path in batch)
                {
                    Resolve(KeyOf(path), path, loaded);
                }
            }

            RaiseChanged();
        }
    }

    private IReadOnlyDictionary<string, TrackLoudness>? Load(IReadOnlyCollection<string> paths)
    {
        try
        {
            return _store.Load(paths);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Invoke($"loudness: could not read the library ({ex.Message}); tracks play unchanged");
            return null;
        }
    }

    private void AnalyzeLoop()
    {
        while (true)
        {
            string path;
            Entry entry;
            CancellationTokenSource job;
            lock (_gate)
            {
                while (_analyzeQueue.Count == 0 || !_settings.IsOn)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                path = _analyzeQueue[0];
                _analyzeQueue.RemoveAt(0);
                if (!_entries.TryGetValue(KeyOf(path), out entry!) || !NeedsAnalysis(entry))
                {
                    continue;
                }

                entry.Analyzing = true;
                job = _analysis = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            }

            LoudnessResult? result = null;
            var before = FileStamp.Read(path);
            Exception? failure = null;
            try
            {
                result = LoudnessAnalyzer.Analyze(_decoders, path, job.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }

            var after = FileStamp.Read(path);
            bool changed;
            LoudnessUpdate? update = null;
            lock (_gate)
            {
                entry.Analyzing = false;
                if (ReferenceEquals(_analysis, job))
                {
                    _analysis = null;
                }

                job.Dispose();
                if (failure is not null)
                {
                    entry.Failed = true; // not tried again this session: the track plays unchanged
                    _log?.Invoke($"loudness: could not analyse '{path}': {failure.Message}");
                }

                if (result is { } r && before == after)
                {
                    entry.Loudness = (entry.Loudness ?? new TrackLoudness(ReplayGainTags.None)) with
                    {
                        Lufs = r.IntegratedLufs,
                        TruePeak = r.TruePeak,
                        Analyzed = true,
                    };
                    update = new LoudnessUpdate(path, r.IntegratedLufs, r.TruePeak, before?.SizeBytes ?? 0, before?.ModifiedUtcTicks ?? 0);
                    if (before is not null)
                    {
                        _unsaved.Add(update);
                    }
                    else
                    {
                        // A file whose size and time cannot be read cannot be told apart from a changed one later, so the result is
                        // only kept for this session, not in the library.
                        update = null;
                    }
                }
                else if (result is not null)
                {
                    entry.Resolved = false; // the file changed while it was measured: look it up again when it is wanted
                }

                changed = result is not null || failure is not null;
            }

            if (update is not null)
            {
                FlushUnsaved();
            }

            if (changed)
            {
                RaiseChanged();
            }
        }
    }

    private void FlushUnsaved()
    {
        List<LoudnessUpdate> snapshot;
        lock (_gate)
        {
            snapshot = [.. _unsaved];
        }

        if (snapshot.Count == 0 || !TrySave(snapshot))
        {
            return;
        }

        lock (_gate)
        {
            _unsaved.RemoveAll(snapshot.Contains);
        }
    }

    private bool TrySave(List<LoudnessUpdate> updates)
    {
        try
        {
            return _store.Save(updates);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Invoke($"loudness: could not save analysis to the library ({ex.Message}); it is kept for this session");
            return false;
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Invoke($"loudness: change handler failed: {ex.Message}");
        }
    }

    private sealed class Entry
    {
        public TrackLoudness? Loudness { get; set; }

        public bool Resolved { get; set; }

        public bool Analyzing { get; set; }

        public bool Failed { get; set; }
    }
}
