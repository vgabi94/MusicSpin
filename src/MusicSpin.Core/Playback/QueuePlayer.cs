using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;

namespace MusicSpin.Core.Playback;

/// <summary>A queue entry was skipped because it could not be opened.</summary>
public sealed class TrackSkippedEventArgs(SkippedItem item) : EventArgs
{
    public SkippedItem Item { get; } = item;
}

/// <summary>
/// Queue + transport on top of an <see cref="IPlayer"/>: play/pause/resume/toggle/stop, next/prev, seek (absolute or
/// relative), volume, auto-advance, and skipping of unplayable entries (deleted/moved files). Thread-safe; used by the
/// background player, <c>music-spin serve</c> and (later) the desktop app.
/// <para>Semantics (documented for agents): <c>next</c>/<c>prev</c> always start playback; <c>next</c> on the last entry and
/// <c>prev</c> on the first are <see cref="MusicSpinErrorCode.InvalidState"/>; after the last entry ends the queue is
/// <see cref="PlayerStatus.Ended"/> and play/resume continue with entries added since, or restart from the first.</para>
/// <para>Repeat: <see cref="RepeatMode.One"/> replays a track that ends naturally (next/prev still move, with the same
/// limits as off); <see cref="RepeatMode.All"/> loops the queue and makes next/prev wrap around.</para>
/// <para>Shuffle: the queue keeps its original order next to the play order. Turning shuffle on shuffles the entries after
/// the current one (the current track keeps playing, entries already played stay before it); turning it off restores the
/// original order, positioned at the current track. A new queue set while shuffle is on is shuffled; entries added while
/// shuffle is on are inserted at random positions after the current one. Every full shuffle uses a fresh seed (given or
/// random) that is reported as <see cref="PlayerStatus.ShuffleSeed"/>, so it can be reproduced.</para>
/// </summary>
public sealed class QueuePlayer : IDisposable
{
    public const int MaxQueueLength = 100_000;
    private const int MaxSkippedKept = 50;

    // A repeat loop (repeat-one replay or repeat-all wrap) that played less than this much audio counts as "rapid"; after
    // MaxRapidLoops rapid loops in a row (tracks that end immediately, e.g. empty or broken files) repeat gives up.
    // Measured in audio played, not wall-clock time: on a busy machine a loop over a broken file can take longer than
    // RapidLoopThreshold of real time, and repeat must still give up.
    private static readonly TimeSpan RapidLoopThreshold = TimeSpan.FromMilliseconds(50);
    private const int MaxRapidLoops = 20;

    private readonly IPlayer _player;
    private readonly Lock _gate = new();
    private readonly List<Slot> _items = [];
    private readonly List<SkippedItem> _skipped = [];
    private int _index = -1;
    private bool _ended;
    private bool _stoppedByUser;
    private string? _lastError;
    private bool _disposed;
    private RepeatMode _repeat;
    private bool _shuffle;
    private int? _seed;
    private Random _random = new();
    private long _nextOrder;
    private TimeSpan _playedSinceLoop;

    // The queue slot whose file the inner player holds. The current entry is not always loaded: a restored queue whose
    // current file is gone, or entries added to an empty queue when none of them could be opened. The player may then
    // still hold an earlier track, which must not be played, sought or reported as the current entry.
    private Slot? _loadedSlot;
    private int _rapidLoops;
    private RadioSession? _radio;

    /// <summary>Radio keeps this many tracks queued after the current one...</summary>
    public const int RadioLookahead = 20;

    /// <summary>...and tops up when fewer than this many are left.</summary>
    public const int RadioLowWater = 10;

    /// <summary>Played radio tracks kept before the current one (for prev); older ones are dropped.</summary>
    public const int RadioHistory = 100;

    public QueuePlayer(IPlayer player, OutputInfo? output = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        Output = output;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.StateChanged += OnStateChanged;
        _player.TrackLoaded += OnTrackLoaded;
        _player.Error += OnPlayerError;
        _player.TrackAdvanced += OnTrackAdvanced;
    }

    public OutputInfo? Output { get; }

    /// <summary>
    /// Called (under the queue lock) when a queued file is not found: returns the entry at its new location (e.g. a library
    /// track that was renamed or moved), or null. The entry is updated in the queue when the new file loads.
    /// </summary>
    public Func<QueueItem, QueueItem?>? ResolveMissing { get; set; }

    /// <summary>
    /// The tracks a radio station may play (usually <see cref="MusicLibrary.RadioPool"/>). Called when radio starts and on
    /// every top-up (so library changes are picked up); may throw <see cref="MusicSpinException"/>. Null: no radio.
    /// </summary>
    public Func<RadioFilter, IReadOnlyList<RadioCandidate>>? RadioPool { get; set; }

    /// <summary>Seeds radio picks (tests); default a random generator per station.</summary>
    public Func<Random>? RadioRandom { get; set; }

    /// <summary>True while a radio station plays.</summary>
    public bool IsRadio
    {
        get { lock (_gate) { return _radio is not null; } }
    }

    /// <summary>Raised on any thread. Handlers must not block or call back into the player.</summary>
    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;

    public event EventHandler<TrackLoadedEventArgs>? TrackLoaded;

    /// <summary>The current entry played to its end (before auto-advancing).</summary>
    public event EventHandler? TrackEnded;

    /// <summary>The last entry finished; nothing left to play.</summary>
    public event EventHandler? QueueEnded;

    public event EventHandler<TrackSkippedEventArgs>? TrackSkipped;

    public event EventHandler? QueueChanged;

    public event EventHandler<PlayerErrorEventArgs>? Error;

    public PlayerState State => _player.State;

    public RepeatMode Repeat
    {
        get { lock (_gate) { return _repeat; } }
    }

    public bool Shuffle
    {
        get { lock (_gate) { return _shuffle; } }
    }

    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    public PlayerStatus Status()
    {
        lock (_gate)
        {
            return StatusCore();
        }
    }

    public QueueListing List(int offset = 0, int limit = 100)
    {
        if (offset < 0)
        {
            throw Invalid("offset must be 0 or greater.");
        }

        if (limit is < 1 or > 10_000)
        {
            throw Invalid("limit must be between 1 and 10000.");
        }

        lock (_gate)
        {
            EnrichCurrent();
            var items = _items.Skip(offset).Take(limit).Select((slot, i) => new QueueEntry(offset + i, offset + i == _index, slot.Item)).ToList();
            return new QueueListing(_items.Count, offset, limit, _index >= 0 ? _index : null, items);
        }
    }

    /// <summary>
    /// Replaces the queue and loads the first playable entry from <paramref name="startIndex"/>. When no entry can be
    /// opened the current queue and playback are left untouched and FileNotFound is thrown.
    /// </summary>
    /// <param name="startIndex">Entry to start with (in the given order). Null: the first entry, or with shuffle a random one.</param>
    /// <param name="shuffle">Turns shuffle on/off for this and later queues; null keeps the current mode.</param>
    /// <param name="seed">Seed for the shuffle (reproducible order); only valid when shuffle ends up on.</param>
    public PlayerStatus SetQueue(IReadOnlyList<QueueItem> items, int? startIndex = null, bool play = true, TimeSpan? start = null,
        bool? shuffle = null, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateItems(items);
        if (items.Count == 0)
        {
            throw Invalid("The queue must contain at least one entry.");
        }

        if (startIndex is { } requested && (requested < 0 || requested >= items.Count))
        {
            throw Invalid($"start index {requested} is outside the queue (0-{items.Count - 1}).");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var shuffled = shuffle ?? _shuffle;
            if (seed is not null && !shuffled)
            {
                throw Invalid("A shuffle seed only applies when shuffle is on.");
            }

            long order = 0;
            var slots = items.Select(item => new Slot(item, order++)).ToList();
            var first = startIndex ?? 0;
            int? usedSeed = null;
            Random? random = null;
            if (shuffled)
            {
                usedSeed = seed ?? NewSeed();
                random = new Random(usedSeed.Value);
                if (startIndex is { } keep)
                {
                    var chosen = slots[keep];
                    slots.RemoveAt(keep);
                    slots.Insert(0, chosen);
                    ShuffleRange(slots, 1, random);
                }
                else
                {
                    ShuffleRange(slots, 0, random);
                }

                first = 0;
            }

            var skipped = new List<SkippedItem>();
            var loaded = -1;
            for (var i = first; i < slots.Count && loaded < 0; i++)
            {
                if (TryLoad(slots[i].Item, i, skipped, slots))
                {
                    loaded = i;
                }
            }

            if (loaded < 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.FileNotFound,
                    $"None of the {slots.Count - first} queued file(s) could be played: {Describe(skipped)}");
            }

            _items.Clear();
            _items.AddRange(slots);
            _radio = null;
            _nextOrder = order;
            _shuffle = shuffled;
            _seed = usedSeed;
            if (random is not null)
            {
                _random = random;
            }

            _skipped.Clear();
            _index = loaded;
            _ended = false;
            _lastError = null;
            ResetRapidLoops();
            foreach (var s in skipped)
            {
                AddSkipped(s);
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            if (start is { } offset && offset > TimeSpan.Zero)
            {
                SeekCore(offset);
            }

            if (play)
            {
                PlayCurrent();
            }

            return StatusCore();
        }
    }

    /// <summary>Replaces the queue with one file without starting playback (<c>serve</c>'s <c>load</c>).</summary>
    public PlayerStatus Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            ThrowIfDisposed();
            _player.Load(path); // throws before touching the queue when the file is bad
            _items.Clear();
            _items.Add(_loadedSlot = new Slot(new QueueItem(path), 0));
            _radio = null;
            _nextOrder = 1;
            _skipped.Clear();
            _index = 0;
            _ended = false;
            _lastError = null;
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    /// <summary>Appends entries. On an empty queue the first playable one is loaded (stopped).</summary>
    public PlayerStatus Add(IReadOnlyList<QueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateItems(items);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_items.Count + items.Count > MaxQueueLength)
            {
                throw Invalid($"The queue is limited to {MaxQueueLength} entries.");
            }

            var added = items.Select(item => new Slot(item, _nextOrder++)).ToList();
            var first = _items.Count;
            if (_radio is not null && _index >= 0)
            {
                // Radio: play these next (after earlier additions), then the station continues.
                var at = _index + 1;
                while (at < _items.Count && !_items[at].Radio)
                {
                    at++;
                }

                _items.InsertRange(at, added);
            }
            else if (_shuffle && _index >= 0)
            {
                MixIntoUpcoming(added);
            }
            else
            {
                _items.AddRange(added);
                if (_shuffle)
                {
                    // Nothing loaded yet: this is a fresh shuffle of everything queued.
                    _seed = NewSeed();
                    _random = new Random(_seed.Value);
                    ShuffleRange(_items, 0, _random);
                    first = 0;
                }
            }

            if (_index < 0)
            {
                for (var i = first; i < _items.Count; i++)
                {
                    if (TryLoad(_items[i].Item, i, null))
                    {
                        _index = i;
                        break;
                    }
                }
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    /// <summary>Stops playback and empties the queue.</summary>
    public PlayerStatus Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _stoppedByUser = true;
            _player.Stop();
            _items.Clear();
            _loadedSlot = null;
            _radio = null;
            _nextOrder = 0;
            _skipped.Clear();
            _index = -1;
            _ended = false;
            _lastError = null;
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    /// <summary>Plays entry <paramref name="index"/>, or resumes/starts the current entry when null.</summary>
    public PlayerStatus Play(int? index = null)
    {
        if (index is null)
        {
            return Resume();
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
            if (index < 0 || index >= _items.Count)
            {
                throw Invalid($"index {index} is outside the queue (0-{_items.Count - 1}).");
            }

            return MoveAndPlay(index.Value, +1, wrap: WrapAll);
        }
    }

    public PlayerStatus Resume()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            TopUpRadio();
            if (_items.Count > 0 && !_ended && !CurrentLoaded())
            {
                // Nothing playable is loaded for the current entry: start at it and skip forward like auto-advance.
                return MoveAndPlay(Math.Max(_index, 0), +1, wrap: WrapAll);
            }

            RequireQueue();
            if (_ended)
            {
                return MoveAndPlay(_index + 1 < _items.Count ? _index + 1 : 0, +1, wrap: WrapAll);
            }

            if (_player.State != PlayerState.Playing)
            {
                PlayCurrent();
            }

            return StatusCore();
        }
    }

    public PlayerStatus Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
            _player.Pause();
            return StatusCore();
        }
    }

    public PlayerStatus Toggle()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
            if (_player.State == PlayerState.Playing)
            {
                _player.Pause();
                return StatusCore();
            }
        }

        return Resume();
    }

    /// <summary>Stops and rewinds the current entry; the queue is kept. Allowed on an empty queue.</summary>
    public PlayerStatus Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _stoppedByUser = true;
            _player.Stop();
            return StatusCore();
        }
    }

    public PlayerStatus Next()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            TopUpRadio();
            RequireQueue();
            var wrap = WrapAll;
            if (_index + 1 >= _items.Count)
            {
                if (wrap)
                {
                    return MoveAndPlay(0, +1, wrap);
                }

                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"Already at the last track ({_index + 1} of {_items.Count}). Add more with 'music-spin queue add', or use 'music-spin repeat all'.");
            }

            return MoveAndPlay(_index + 1, +1, wrap);
        }
    }

    public PlayerStatus Previous()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
            var wrap = WrapAll;
            if (_index <= 0)
            {
                if (wrap)
                {
                    return MoveAndPlay(_items.Count - 1, -1, wrap);
                }

                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, _radio is not null
                    ? $"No earlier radio track (radio keeps the last {RadioHistory}). Use 'music-spin seek 0' to restart this one."
                    : "Already at the first track. Use 'music-spin seek 0' to restart it.");
            }

            return MoveAndPlay(_index - 1, -1, wrap);
        }
    }

    /// <summary>Seeks to an absolute position. Negative or beyond the end of the track is InvalidArgument.</summary>
    public PlayerStatus Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireLoaded();
            var duration = _player.Duration;
            if (position < TimeSpan.Zero)
            {
                throw Invalid($"Seek position must be 0 or greater (got {position.TotalSeconds:0.###}s).");
            }

            if (duration > TimeSpan.Zero && position > duration)
            {
                throw Invalid($"Seek position {position.TotalSeconds:0.###}s is beyond the end of the track ({duration.TotalSeconds:0.###}s).");
            }

            SeekCore(position);
            return StatusCore();
        }
    }

    /// <summary>Seeks relative to the current position, clamped to the track.</summary>
    public PlayerStatus SeekBy(TimeSpan delta)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireLoaded();
            var duration = _player.Duration;
            var target = _player.Position + delta;
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }

            if (duration > TimeSpan.Zero && target > duration)
            {
                target = duration;
            }

            SeekCore(target);
            return StatusCore();
        }
    }

    /// <summary>Sets the linear volume (0..1); anything else is InvalidArgument.</summary>
    public PlayerStatus SetVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
        {
            throw Invalid($"Volume must be between 0.0 and 1.0 (got {volume}).");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _player.Volume = (float)volume;
            return StatusCore();
        }
    }

    /// <summary>Changes the volume by <paramref name="delta"/>, clamped to 0..1.</summary>
    public PlayerStatus ChangeVolume(double delta)
    {
        if (!double.IsFinite(delta))
        {
            throw Invalid("Volume change must be a number.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _player.Volume = (float)Math.Clamp(Math.Round(_player.Volume + delta, 4), 0, 1);
            return StatusCore();
        }
    }

    /// <summary>Sets the repeat mode (off | one | all). Allowed on an empty queue (applies to the next one).</summary>
    public PlayerStatus SetRepeat(RepeatMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw Invalid($"Unknown repeat mode '{mode}'. Use {PlaybackModes.RepeatValues}.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _repeat = mode;
            ResetRapidLoops();
            return StatusCore();
        }
    }

    /// <summary>
    /// Turns shuffle on (shuffling the entries after the current one; again when already on) or off (restoring the original
    /// order at the current track). Allowed on an empty queue: the mode applies to the next queue.
    /// </summary>
    /// <param name="seed">Reproducible shuffle; only valid when turning shuffle on.</param>
    public PlayerStatus SetShuffle(bool enabled, int? seed = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_radio is not null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    "Radio is already random; shuffle applies to normal queues (the setting is kept for them). Start a queue with 'music-spin play' to use it.");
            }

            if (!enabled)
            {
                if (seed is not null)
                {
                    throw Invalid("A shuffle seed only applies when turning shuffle on.");
                }

                if (_shuffle)
                {
                    var current = _index >= 0 && _index < _items.Count ? _items[_index] : null;
                    _items.Sort(static (a, b) => a.Order.CompareTo(b.Order));
                    if (current is not null)
                    {
                        _index = _items.IndexOf(current);
                    }

                    _shuffle = false;
                    _seed = null;
                    QueueChanged?.Invoke(this, EventArgs.Empty);
                }

                return StatusCore();
            }

            _seed = seed ?? NewSeed();
            _random = new Random(_seed.Value);
            _shuffle = true;
            ShuffleRange(_items, _index >= 0 ? _index + 1 : 0, _random);
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    /// <summary>Shows a notice as <see cref="PlayerStatus.LastError"/> until the next queue is set (e.g. "saved queue ignored").</summary>
    public void SetNotice(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    /// <summary>The state to persist (entries in play order with their original-order keys, index, position, modes).</summary>
    public QueueSnapshot Snapshot(DateTimeOffset savedAt)
    {
        lock (_gate)
        {
            var has = _index >= 0 && _index < _items.Count;
            return new QueueSnapshot(QueueSnapshot.CurrentVersion, [.. _items.Select(s => s.Item)], [.. _items.Select(s => s.Order)],
                has ? _index : null, has && !_ended && CurrentLoaded() ? Math.Round(_player.Position.TotalSeconds, 3) : 0, _repeat, _shuffle, _shuffle ? _seed : null, savedAt, _radio?.ToState());
        }
    }

    /// <summary>
    /// Restores a saved queue without playing: the current entry is loaded and cued (Paused) at the saved position.
    /// If that file cannot be opened the queue is kept, stopped, and the entry is listed under skipped.
    /// </summary>
    /// <exception cref="MusicSpinException">InvalidArgument when the snapshot is inconsistent (nothing is changed).</exception>
    public PlayerStatus Restore(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Items is null || snapshot.Order is null)
        {
            throw Invalid("Saved queue: items and order are required.");
        }

        ValidateItems(snapshot.Items);
        if (snapshot.Order.Count != snapshot.Items.Count || snapshot.Order.Distinct().Count() != snapshot.Order.Count || snapshot.Order.Any(o => o < 0))
        {
            throw Invalid("Saved queue: the order keys do not match the items.");
        }

        if (snapshot.Index is { } i && (i < 0 || i >= snapshot.Items.Count))
        {
            throw Invalid($"Saved queue: index {i} is outside the queue (0-{snapshot.Items.Count - 1}).");
        }

        if (!Enum.IsDefined(snapshot.Repeat) || !double.IsFinite(snapshot.PositionSeconds) || snapshot.PositionSeconds < 0)
        {
            throw Invalid("Saved queue: invalid repeat mode or position.");
        }

        if (snapshot.Radio is { } radio && (radio.Filter is null || radio.Filter.Validate() is not null))
        {
            throw Invalid($"Saved queue: invalid radio filters ({radio.Filter?.Validate() ?? "missing"}).");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _stoppedByUser = true;
            _player.Stop();
            _items.Clear();
            for (var k = 0; k < snapshot.Items.Count; k++)
            {
                _items.Add(new Slot(snapshot.Items[k], snapshot.Order[k]));
            }

            _nextOrder = snapshot.Order.Count == 0 ? 0 : snapshot.Order.Max() + 1;
            _radio = snapshot.Radio is { } state ? RadioSession.From(state, NewRadioRandom()) : null;
            if (_radio is not null)
            {
                for (var k = (snapshot.Index ?? 0) + 1; k < _items.Count; k++)
                {
                    _items[k].Radio = true; // upcoming picks; the station tops up after them
                }
            }

            _repeat = snapshot.Repeat;
            _shuffle = snapshot.Shuffle;
            _seed = snapshot.Shuffle ? snapshot.Seed ?? NewSeed() : null;
            _random = new Random(_seed ?? NewSeed());
            _skipped.Clear();
            _ended = false;
            _lastError = null;
            ResetRapidLoops();
            _index = _items.Count == 0 ? -1 : snapshot.Index ?? 0;
            if (_index >= 0 && TryLoad(_items[_index].Item, _index, null))
            {
                var position = TimeSpan.FromSeconds(snapshot.PositionSeconds);
                _player.Cue(position < _player.Duration ? position : TimeSpan.Zero);
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stoppedByUser = true;
        }

        _player.PlaybackEnded -= OnPlaybackEnded;
        _player.StateChanged -= OnStateChanged;
        _player.TrackLoaded -= OnTrackLoaded;
        _player.Error -= OnPlayerError;
        _player.TrackAdvanced -= OnTrackAdvanced;
        try
        {
            _player.Stop();
        }
        catch (ObjectDisposedException)
        {
            // owner disposed the player first
        }
    }

    /// <summary>Plays the first playable entry from <paramref name="index"/> in <paramref name="direction"/>; with <paramref name="wrap"/> it continues past the ends (each entry tried once).</summary>
    private PlayerStatus MoveAndPlay(int index, int direction, bool wrap = false)
    {
        var count = _items.Count;
        for (var step = 0; step < count; step++)
        {
            var i = index + (step * direction);
            if (wrap)
            {
                i = ((i % count) + count) % count;
            }
            else if (i < 0 || i >= count)
            {
                break;
            }

            if (TryLoad(_items[i].Item, i, null))
            {
                _index = i;
                _ended = false;
                PlayCurrent();
                return StatusCore();
            }
        }

        _stoppedByUser = true;
        _player.Stop();
        throw new MusicSpinException(MusicSpinErrorCode.FileNotFound,
            $"No playable track {(direction > 0 ? "after" : "before")} entry {index + 1 - direction}: the remaining file(s) were skipped (see 'skipped' in 'music-spin status').");
    }

    private bool TryLoad(QueueItem item, int index, List<SkippedItem>? into, List<Slot>? slots = null)
    {
        try
        {
            _player.Load(item.Path);
            MarkLoaded(slots ?? _items, index);
            return true;
        }
        catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.FileNotFound && TryLoadMoved(item, index, slots ?? _items))
        {
            return true;
        }
        catch (MusicSpinException ex) when (ex.Code is MusicSpinErrorCode.FileNotFound or MusicSpinErrorCode.UnsupportedFormat or MusicSpinErrorCode.DecodeFailed)
        {
            var skipped = new SkippedItem(index, item.Path, ExitCodes.Name(ExitCodes.From(ex.Code)), ex.Message);
            if (into is not null)
            {
                into.Add(skipped);
            }
            else
            {
                AddSkipped(skipped);
            }

            return false;
        }
    }

    /// <summary>
    /// The file is gone: ask the library where the entry's track is now (by its id) and load that instead, updating
    /// the entry in <paramref name="slots"/> (the live queue, or one being staged by SetQueue).
    /// </summary>
    private bool TryLoadMoved(QueueItem item, int index, List<Slot> slots)
    {
        if (ResolveMissing?.Invoke(item) is not { } moved || string.Equals(moved.Path, item.Path, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            _player.Load(moved.Path);
        }
        catch (MusicSpinException)
        {
            return false;
        }

        MarkLoaded(slots, index);
        if (index >= 0 && index < slots.Count && ReferenceEquals(slots[index].Item, item))
        {
            slots[index].Item = moved;
            if (ReferenceEquals(slots, _items))
            {
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        return true;
    }

    /// <summary>
    /// Plain files carry no metadata until they are opened: copy what the decoder found into the current queue entry
    /// so queue listings show titles. Called under the lock.
    /// </summary>
    private void EnrichCurrent()
    {
        if (_index < 0 || _index >= _items.Count || _player.CurrentTrack is not { } track)
        {
            return;
        }

        var slot = _items[_index];
        var item = slot.Item;
        if (item.Title is not null || track.Title is null || !string.Equals(item.Path, track.Path, StringComparison.Ordinal))
        {
            return;
        }

        slot.Item = item with
        {
            Title = track.Title,
            Artist = item.Artist ?? track.Artist,
            Album = item.Album ?? track.Album,
            AlbumArtist = item.AlbumArtist ?? track.AlbumArtist,
            DurationSeconds = item.DurationSeconds ?? (track.Duration > TimeSpan.Zero ? track.Duration.TotalSeconds : null),
        };
    }

    private void AddSkipped(SkippedItem item)
    {
        _skipped.Add(item);
        if (_skipped.Count > MaxSkippedKept)
        {
            _skipped.RemoveAt(0);
        }

        TrackSkipped?.Invoke(this, new TrackSkippedEventArgs(item));
    }

    private void PlayCurrent()
    {
        try
        {
            _stoppedByUser = false;
            _player.Play();
            _lastError = null;
        }
        catch (MusicSpinException ex)
        {
            _lastError = ex.Message;
            Error?.Invoke(this, new PlayerErrorEventArgs(ex));
            throw;
        }
    }

    private void SeekCore(TimeSpan position)
    {
        _player.Seek(position);
        _ended = false;
    }

    private PlayerStatus StatusCore()
    {
        SyncNext();
        EnrichCurrent();
        var diagnostics = _player.Diagnostics;
        var has = _index >= 0 && _index < _items.Count;
        var loaded = CurrentLoaded();
        var track = loaded ? _player.CurrentTrack : null;
        return new PlayerStatus(
            loaded ? _player.State : PlayerState.Stopped,
            loaded ? Math.Round(_player.Position.TotalSeconds, 3) : 0,
            loaded ? Math.Round(_player.Duration.TotalSeconds, 3) : 0,
            _player.Volume,
            track is null ? null : TrackDto.From(track),
            has ? _index : null,
            _items.Count,
            has ? _items[_index].Item : null,
            Output,
            _ended,
            _lastError,
            _skipped.Count > 0 ? [.. _skipped] : null,
            _repeat,
            _shuffle,
            _shuffle ? _seed : null,
            diagnostics?.Gapless,
            diagnostics?.Underruns,
            _radio is null ? null : new RadioInfo(_radio.Filter, _radio.Filter.Describe(), _radio.PoolSize));
    }

    private const int MaxNextTracks = 16;

    /// <summary>Tells the player which entries follow the current one in play order (gapless decode-ahead).</summary>
    private void SyncNext()
    {
        if (_disposed)
        {
            return;
        }

        TopUpRadio();
        var next = new List<NextTrack>();
        if (!_ended && CurrentLoaded())
        {
            if (_repeat == RepeatMode.One)
            {
                next.AddRange(Enumerable.Repeat(new NextTrack(_items[_index].Item.Path, _items[_index]), MaxNextTracks));
            }
            else
            {
                // Repeat all: keep going round (a short queue may loop several times within the decode-ahead).
                var order = WrapAll
                    ? Enumerable.Range(1, MaxNextTracks).Select(k => (_index + k) % _items.Count)
                    : Enumerable.Range(_index + 1, _items.Count - _index - 1);

                foreach (var i in order.Take(MaxNextTracks))
                {
                    var slot = _items[i];

                    // A missing file may have moved: leave it to the normal path, where the resolver can find it.
                    if (ResolveMissing is not null && !File.Exists(slot.Item.Path))
                    {
                        break;
                    }

                    next.Add(new NextTrack(slot.Item.Path, slot));
                }
            }
        }

        _player.SetNextTracks(next);
    }

    private void OnTrackAdvanced(object? sender, TrackAdvancedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var slot = (Slot)e.Token;
            var index = _items.IndexOf(slot);
            _playedSinceLoop += e.PreviousPlayed;
            TrackEnded?.Invoke(this, EventArgs.Empty);
            foreach (var skipped in e.Skipped)
            {
                var ex = skipped.Error;
                AddSkipped(new SkippedItem(_items.IndexOf((Slot)skipped.Entry.Token), skipped.Entry.Path, ExitCodes.Name(ExitCodes.From(ex.Code)), ex.Message));
            }

            if (index < 0)
            {
                // The entry was removed while it was already playing out of the buffer: stop there.
                _player.Stop();
                _loadedSlot = null;
                _ended = true;
                QueueEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            var loop = _repeat == RepeatMode.One || index <= _index || _radio is not null;
            _index = index;
            _loadedSlot = slot;
            _ended = false;
            TrackLoaded?.Invoke(this, new TrackLoadedEventArgs(e.Track));
            if (loop && RapidLoop())
            {
                _player.Stop();
                _ended = true;
                QueueEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            SyncNext();
        }
    }

    /// <summary>Repeat all wraps a normal queue; radio never ends, so it has nothing to wrap.</summary>
    private bool WrapAll => _repeat == RepeatMode.All && _radio is null;

    /// <summary>
    /// Starts a radio station: endless random playback of the library tracks matching <paramref name="filter"/>, picked a
    /// few at a time (see <see cref="RadioPicker"/>). Replaces the queue. NotFound when no track matches; InvalidState
    /// when this player has no library (<see cref="RadioPool"/> is null).
    /// </summary>
    public PlayerStatus StartRadio(RadioFilter filter, bool play = true)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValid();
        var source = RadioPool ?? throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
            "Radio needs the music library; it is available in the background player ('music-spin radio').");
        var pool = source(filter);
        if (pool.Count == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.NotFound, filter.EmptyPoolMessage());
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var session = new RadioSession(filter, new RadioPicker.History(), NewRadioRandom()) { PoolSize = pool.Count };
            var slots = new List<Slot>();
            long order = 0;
            var skipped = new List<SkippedItem>();
            var loaded = -1;

            // Pick until one opens: a pool may contain files deleted since the last scan.
            for (var round = 0; round < 3 && loaded < 0; round++)
            {
                var first = slots.Count;
                foreach (var pick in RadioPicker.Pick(pool, RadioLookahead, session.History, session.Random))
                {
                    slots.Add(new Slot(ToItem(pick), order++) { Radio = true });
                }

                for (var i = first; i < slots.Count && loaded < 0; i++)
                {
                    if (TryLoad(slots[i].Item, i, skipped, slots))
                    {
                        loaded = i;
                    }
                }
            }

            if (loaded < 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.FileNotFound,
                    $"None of {slots.Count} radio picks could be played (run 'music-spin library scan'?): {Describe(skipped)}");
            }

            slots.RemoveRange(0, loaded); // unplayable picks before the first playable one are not history
            _items.Clear();
            _items.AddRange(slots);
            _nextOrder = order;
            _radio = session;
            _skipped.Clear();
            _index = 0;
            _ended = false;
            _lastError = null;
            ResetRapidLoops();
            foreach (var s in skipped)
            {
                AddSkipped(s with { Index = -1 });
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            if (play)
            {
                PlayCurrent();
            }

            return StatusCore();
        }
    }

    /// <summary>
    /// Radio: drops played tracks beyond <see cref="RadioHistory"/> and, when fewer than <see cref="RadioLowWater"/> are
    /// left after the current one, picks more from a fresh pool (library changes since the last top-up count). Under the lock.
    /// </summary>
    private void TopUpRadio()
    {
        if (_radio is not { } radio || _disposed)
        {
            return;
        }

        if (_index > RadioHistory)
        {
            var drop = _index - RadioHistory;
            _items.RemoveRange(0, drop);
            _index -= drop;
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }

        var upcoming = _items.Count - Math.Max(_index, 0) - 1;
        if (upcoming >= RadioLowWater || RadioPool is not { } source)
        {
            return;
        }

        IReadOnlyList<RadioCandidate> pool;
        try
        {
            pool = source(radio.Filter);
        }
        catch (MusicSpinException ex)
        {
            _lastError = $"Radio could not read the library ({ex.Message}); it keeps playing what is queued and retries.";
            return;
        }

        radio.PoolSize = pool.Count;
        if (pool.Count == 0)
        {
            _lastError = $"No library tracks match the radio filters any more ({radio.Filter.Describe()}); radio ends after the queued tracks.";
            return;
        }

        foreach (var pick in RadioPicker.Pick(pool, RadioLookahead - upcoming, radio.History, radio.Random))
        {
            _items.Add(new Slot(ToItem(pick), _nextOrder++) { Radio = true });
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static QueueItem ToItem(RadioCandidate c)
        => new(c.Path, c.Title, c.Artist, c.Album, c.AlbumArtist, c.TrackNumber, c.DurationSeconds, c.TrackId);

    private Random NewRadioRandom() => RadioRandom?.Invoke() ?? new Random(NewSeed());

    private void MarkLoaded(List<Slot> slots, int index)
        => _loadedSlot = index >= 0 && index < slots.Count ? slots[index] : null;

    private bool CurrentLoaded()
        => _index >= 0 && _index < _items.Count && ReferenceEquals(_items[_index], _loadedSlot) && _player.CurrentTrack is not null;

    private void RequireLoaded()
    {
        RequireQueue();
        if (!CurrentLoaded())
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                $"Entry {_index + 1} could not be loaded (see 'skipped' in 'music-spin status'). Use 'music-spin play' to continue with the next playable entry.");
        }
    }

    private void RequireQueue()
    {
        if (_items.Count == 0 || _index < 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                "The queue is empty. Start something with 'music-spin play <file> | --album <title> | --artist <name>'.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateItems(IReadOnlyList<QueueItem> items)
    {
        if (items.Count > MaxQueueLength)
        {
            throw Invalid($"The queue is limited to {MaxQueueLength} entries.");
        }

        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Path))
            {
                throw Invalid("Every queue entry needs a non-empty path.");
            }
        }
    }

    private static string Describe(List<SkippedItem> skipped)
        => string.Join("; ", skipped.Take(3).Select(s => s.Message)) + (skipped.Count > 3 ? $" (+{skipped.Count - 3} more)" : string.Empty);

    private static MusicSpinException Invalid(string message) => new(MusicSpinErrorCode.InvalidArgument, message);

#pragma warning disable CA5394 // shuffling music is not security sensitive
    private static int NewSeed() => Random.Shared.Next();

    /// <summary>Fisher-Yates over [<paramref name="from"/>, end). With from = 0 this is the same order as <c>MusicLibrary.Shuffle</c> for the same seed.</summary>
    private static void ShuffleRange(List<Slot> list, int from, Random random)
    {
        for (var i = list.Count - 1; i > from; i--)
        {
            var j = from + random.Next(i - from + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Inserts <paramref name="added"/> (shuffled) at random positions after the current entry, keeping the relative order
    /// of the upcoming entries: a random interleave, O(n).
    /// </summary>
    private void MixIntoUpcoming(List<Slot> added)
    {
        ShuffleRange(added, 0, _random);
        var upcoming = _items.GetRange(_index + 1, _items.Count - _index - 1);
        _items.RemoveRange(_index + 1, upcoming.Count);
        int a = 0, b = 0;
        while (a < upcoming.Count || b < added.Count)
        {
            var remainingA = upcoming.Count - a;
            var remainingB = added.Count - b;
            var takeAdded = remainingA == 0 || (remainingB > 0 && _random.Next(remainingA + remainingB) < remainingB);
            _items.Add(takeAdded ? added[b++] : upcoming[a++]);
        }
    }
#pragma warning restore CA5394

    /// <summary>True when repeat has looped too fast too often (tracks that end immediately); the caller then stops.</summary>
    private bool RapidLoop()
    {
        _rapidLoops = _playedSinceLoop < RapidLoopThreshold ? _rapidLoops + 1 : 0;
        _playedSinceLoop = TimeSpan.Zero;
        if (_rapidLoops < MaxRapidLoops)
        {
            return false;
        }

        ResetRapidLoops();
        _lastError = _radio is not null
            ? $"Radio stopped: {MaxRapidLoops} tracks in a row ended immediately (empty or broken files?)."
            : $"Repeat stopped: the queued track(s) ended immediately {MaxRapidLoops} times in a row (empty or broken files?).";
        return true;
    }

    private void ResetRapidLoops()
    {
        _rapidLoops = 0;
        _playedSinceLoop = TimeSpan.Zero;
    }

    // Raised on a thread-pool thread after the inner player stopped at end of stream.
    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            try
            {
                OnPlaybackEndedCore();
            }
            finally
            {
                SyncNext();
            }
        }
    }

    private void OnPlaybackEndedCore()
    {
        {
            if (_disposed || _stoppedByUser || _index < 0 || _player.State != PlayerState.Stopped)
            {
                return;
            }

            _playedSinceLoop += _player.Position; // where the track ended: how much of it was played
            TrackEnded?.Invoke(this, EventArgs.Empty);
            if (_repeat == RepeatMode.One)
            {
                if (!RapidLoop() && TryPlayFromEvent())
                {
                    return;
                }
            }
            else if (_radio is null || !RapidLoop())
            {
                TopUpRadio();
                for (var i = _index + 1; i < _items.Count; i++)
                {
                    if (TryLoad(_items[i].Item, i, null))
                    {
                        _index = i;
                        TryPlayFromEvent();
                        return;
                    }
                }

                if (WrapAll && !RapidLoop())
                {
                    for (var i = 0; i <= _index && i < _items.Count; i++)
                    {
                        if (TryLoad(_items[i].Item, i, null))
                        {
                            _index = i;
                            TryPlayFromEvent();
                            return;
                        }
                    }
                }
            }

            _ended = true;
            QueueEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryPlayFromEvent()
    {
        try
        {
            PlayCurrent();
            return true;
        }
        catch (MusicSpinException)
        {
            // recorded in LastError and raised as Error; nobody to throw to on this thread
            return false;
        }
    }

    private void OnStateChanged(object? sender, PlayerStateChangedEventArgs e) => StateChanged?.Invoke(this, e);

    private void OnTrackLoaded(object? sender, TrackLoadedEventArgs e) => TrackLoaded?.Invoke(this, e);

    private void OnPlayerError(object? sender, PlayerErrorEventArgs e) => Error?.Invoke(this, e);

    /// <summary>A queue entry plus its position in the original (unshuffled) order.</summary>
    private sealed class Slot(QueueItem item, long order)
    {
        public QueueItem Item { get; set; } = item;

        public long Order { get; } = order;

        /// <summary>Picked by the radio station (as opposed to added by the user while radio plays).</summary>
        public bool Radio { get; set; }
    }

    /// <summary>A playing radio station: filters, recent picks and its random generator.</summary>
    private sealed class RadioSession(RadioFilter filter, RadioPicker.History history, Random random)
    {
        public RadioFilter Filter { get; } = filter;

        public RadioPicker.History History { get; } = history;

        public Random Random { get; } = random;

        public int PoolSize { get; set; }

        public static RadioSession From(RadioState state, Random random)
            => new(state.Filter, new RadioPicker.History(state.Recent, state.LastArtist, state.LastAlbum), random);

        public RadioState ToState() => new(Filter, History.Recent, History.LastArtist, History.LastAlbum);
    }
}
