using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;

namespace MusicSpin.Core.Playback;

/// <summary>Where <see cref="QueuePlayer.Add"/> puts new entries.</summary>
public enum QueuePosition
{
    /// <summary>At the end of the queue (with shuffle on: mixed into the upcoming entries).</summary>
    End,

    /// <summary>Up next, right after the current entry (before earlier up-next entries): "Play next".</summary>
    Next,

    /// <summary>Up next, after the up-next entries already there: "Add to queue" in the app.</summary>
    UpNext,
}

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
    private float? _mutedLevel; // muted: the level to go back to (the player plays at 0 meanwhile)
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

    /// <summary>A radio station started (<see cref="StartRadio"/> succeeded); raised outside the queue lock.</summary>
    public event EventHandler<RadioFilter>? RadioStarted;

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

    public bool Muted
    {
        get { lock (_gate) { return _mutedLevel is not null; } }
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
            var upNextEnd = _index >= 0 ? UpNextEnd() : 0;
            var items = _items.Skip(offset).Take(limit)
                .Select((slot, i) => new QueueEntry(offset + i, offset + i == _index, slot.Item, offset + i > _index && offset + i < upNextEnd)).ToList();
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

    /// <summary>
    /// Adds entries. <see cref="QueuePosition.End"/> appends them (with shuffle on, mixed into the upcoming entries);
    /// <see cref="QueuePosition.Next"/> and <see cref="QueuePosition.UpNext"/> insert them as <i>up next</i>, right after the
    /// current entry or after the up-next entries already there. While radio plays every addition is up next. On an empty
    /// queue the first playable entry is loaded (stopped), and nothing is up next.
    /// </summary>
    public PlayerStatus Add(IReadOnlyList<QueueItem> items, QueuePosition position = QueuePosition.End)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateItems(items);
        if (!Enum.IsDefined(position))
        {
            throw Invalid($"Unknown queue position '{position}'.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_items.Count + items.Count > MaxQueueLength)
            {
                throw Invalid($"The queue is limited to {MaxQueueLength} entries.");
            }

            var added = items.Select(item => new Slot(item, _nextOrder++)).ToList();
            var first = _items.Count;
            if (_index >= 0 && (position != QueuePosition.End || _radio is not null))
            {
                // Up next: these play after the current entry (and, for UpNext, after earlier additions); then the queue
                // or the radio station continues.
                var at = position == QueuePosition.Next ? _index + 1 : UpNextEnd();
                foreach (var slot in added)
                {
                    slot.UpNext = true;
                }

                PlaceInOriginalOrder(_items[at - 1], added);
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

    /// <summary>
    /// Removes the entry at <paramref name="index"/> (play order). The current entry can't be removed (InvalidState):
    /// skip it with <see cref="Next"/> first.
    /// </summary>
    public PlayerStatus Remove(int index)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireIndex(index);
            if (index == _index)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"Entry {index + 1} is the current track. Skip it with 'music-spin next' first, or 'music-spin queue clear' to empty the queue.");
            }

            _items.RemoveAt(index);
            if (index < _index)
            {
                _index--;
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    /// <summary>
    /// Moves the entry at <paramref name="from"/> so that it ends up at <paramref name="to"/> (both in play order); the
    /// current entry keeps playing wherever it moves. An entry moved into the up-next entries, or right after them, becomes
    /// up next; one moved past other upcoming entries, or before the current one, doesn't. With shuffle off this is also the
    /// new original order; with shuffle on, turning shuffle off restores the original order.
    /// </summary>
    public PlayerStatus Move(int from, int to)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireIndex(from);
            RequireIndex(to);
            if (from == to)
            {
                return StatusCore();
            }

            // No current entry (everything queued was unplayable): a plain reorder; NormalizeUpNext clears any up-next mark.
            var current = _index >= 0 ? _items[_index] : null;
            var slot = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, slot);
            _index = current is null ? -1 : _items.IndexOf(current);
            if (current is not null && !ReferenceEquals(slot, current))
            {
                slot.UpNext = to > _index && _items.Skip(_index + 1).Take(to - _index - 1).All(s => s.UpNext);
            }

            NormalizeUpNext();
            if (!_shuffle)
            {
                RenumberOrder();
            }

            QueueChanged?.Invoke(this, EventArgs.Empty);
            return StatusCore();
        }
    }

    /// <summary>Removes the up-next entries; the current track keeps playing and the rest of the queue follows.</summary>
    public PlayerStatus ClearUpNext()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_index >= 0 && UpNextEnd() is var end && end > _index + 1)
            {
                _items.RemoveRange(_index + 1, end - _index - 1);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }

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

    /// <summary>
    /// Plays entry <paramref name="index"/>, or resumes/starts the current entry when null. Skipping ahead drops the up-next
    /// entries it jumps over: they were never played.
    /// </summary>
    public PlayerStatus Play(int? index = null)
    {
        if (index is null)
        {
            return Resume();
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            RequireEntries();
            if (index < 0 || index >= _items.Count)
            {
                throw Invalid($"index {index} is outside the queue (0-{_items.Count - 1}).");
            }

            var target = index.Value;
            var dropped = false;
            for (var i = target - 1; i > _index; i--)
            {
                if (_items[i].UpNext)
                {
                    _items.RemoveAt(i);
                    target--;
                    dropped = true;
                }
            }

            if (dropped)
            {
                QueueChanged?.Invoke(this, EventArgs.Empty);
            }

            return MoveAndPlay(target, +1, wrap: WrapAll);
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
            RequireEntries();
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
            RequireEntries();
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

    /// <summary>Sets the linear volume (0..1), unmuting; anything else is InvalidArgument.</summary>
    public PlayerStatus SetVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
        {
            throw Invalid($"Volume must be between 0.0 and 1.0 (got {volume}).");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _mutedLevel = null;
            _player.Volume = (float)volume;
            return StatusCore();
        }
    }

    /// <summary>Changes the volume by <paramref name="delta"/>, clamped to 0..1, unmuting (from the level kept while muted).</summary>
    public PlayerStatus ChangeVolume(double delta)
    {
        if (!double.IsFinite(delta))
        {
            throw Invalid("Volume change must be a number.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var level = LevelCore();
            _mutedLevel = null;
            _player.Volume = (float)Math.Clamp(Math.Round(level + delta, 4), 0, 1);
            return StatusCore();
        }
    }

    /// <summary>
    /// Mutes (<paramref name="muted"/> true), unmutes (false) or toggles (null). Muted, the player plays at 0 and keeps
    /// the level, which <c>status</c> still reports (with <c>muted</c>); unmuting, or setting a volume, goes back to it.
    /// The mute lasts while this player runs: a new player starts unmuted.
    /// </summary>
    public PlayerStatus SetMuted(bool? muted)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var mute = muted ?? _mutedLevel is null;
            if (mute && _mutedLevel is null)
            {
                _mutedLevel = _player.Volume;
                _player.Volume = 0;
            }
            else if (!mute && _mutedLevel is { } level)
            {
                _mutedLevel = null;
                _player.Volume = level;
            }

            return StatusCore();
        }
    }

    /// <summary>The volume level: the player's, or the one kept while muted.</summary>
    private float LevelCore() => _mutedLevel ?? _player.Volume;

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
                    var upNext = current is null ? [] : _items.GetRange(_index + 1, UpNextEnd() - _index - 1);
                    _items.RemoveRange(_index + 1, upNext.Count);
                    _items.Sort(static (a, b) => a.Order.CompareTo(b.Order));
                    if (current is not null)
                    {
                        _index = _items.IndexOf(current);
                        _items.InsertRange(_index + 1, upNext); // up next stays next
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
            ShuffleRange(_items, _index >= 0 ? UpNextEnd() : 0, _random);
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
            var upNext = has ? Enumerable.Range(_index + 1, UpNextEnd() - _index - 1).ToList() : [];
            return new QueueSnapshot(QueueSnapshot.CurrentVersion, [.. _items.Select(s => s.Item)], [.. _items.Select(s => s.Order)],
                has ? _index : null, has && !_ended && CurrentLoaded() ? Math.Round(_player.Position.TotalSeconds, 3) : 0, _repeat, _shuffle, _shuffle ? _seed : null, savedAt, _radio?.ToState(),
                upNext.Count > 0 ? upNext : null);
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

        if (snapshot.UpNext is { } upNext && (upNext.Any(k => k < 0 || k >= snapshot.Items.Count) || upNext.Distinct().Count() != upNext.Count))
        {
            throw Invalid("Saved queue: the up-next indexes do not match the items.");
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

            foreach (var k in snapshot.UpNext ?? [])
            {
                _items[k].UpNext = true;
            }

            _nextOrder = snapshot.Order.Count == 0 ? 0 : snapshot.Order.Max() + 1;
            _radio = snapshot.Radio is { } state ? RadioSession.From(state, NewRadioRandom()) : null;
            _repeat = snapshot.Repeat;
            _shuffle = snapshot.Shuffle;
            _seed = snapshot.Shuffle ? snapshot.Seed ?? NewSeed() : null;
            _random = new Random(_seed ?? NewSeed());
            _skipped.Clear();
            _ended = false;
            _lastError = null;
            ResetRapidLoops();
            _index = _items.Count == 0 ? -1 : snapshot.Index ?? 0;
            NormalizeUpNext();
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
        var from = index + 1 - direction; // 1-based entry the search started next to
        throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, wrap || from < 1 || from > count
            ? $"No playable track in the queue: all {count} entr{(count == 1 ? "y was" : "ies were")} skipped (see 'skipped' in 'music-spin status')."
            : $"No playable track {(direction > 0 ? "after" : "before")} entry {from}: the remaining file(s) were skipped (see 'skipped' in 'music-spin status').");
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
            LevelCore(),
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
            _radio is null ? null : new RadioInfo(_radio.Filter, _radio.Filter.Describe(), _radio.PoolSize),
            has ? UpNextEnd() - _index - 1 : 0,
            _mutedLevel is not null);
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

        var status = StartRadioCore(filter, pool, play);
        RadioStarted?.Invoke(this, filter);
        return status;
    }

    private PlayerStatus StartRadioCore(RadioFilter filter, IReadOnlyList<RadioCandidate> pool, bool play)
    {
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
                    slots.Add(new Slot(ToItem(pick), order++));
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
            _items.Add(new Slot(ToItem(pick), _nextOrder++));
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

    /// <summary>The queue has entries and a current one (index &gt;= 0).</summary>
    private void RequireQueue()
    {
        RequireEntries();
        if (_index < 0)
        {
            // Entries but none loaded: everything queued so far was unplayable (#66).
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                $"No queue entry is loaded: none of the {_items.Count} queued entr{(_items.Count == 1 ? "y" : "ies")} could be played (see 'skipped' in 'music-spin status'). " +
                "Use 'music-spin play' to retry, 'music-spin queue remove/move' to edit the queue, or 'music-spin queue clear'.");
        }
    }

    /// <summary>The queue has entries; there may be no current entry (all unplayable).</summary>
    private void RequireEntries()
    {
        if (_items.Count == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                "The queue is empty. Start something with 'music-spin play <file> | --album <title> | --artist <name>'.");
        }
    }

    private void RequireIndex(int index)
    {
        RequireEntries();
        if (index < 0 || index >= _items.Count)
        {
            throw Invalid($"index {index} is outside the queue (0-{_items.Count - 1}).");
        }
    }

    /// <summary>The index after the up-next entries (the run of them right after the current entry). Needs a current entry.</summary>
    private int UpNextEnd()
    {
        var end = _index + 1;
        while (end < _items.Count && _items[end].UpNext)
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// Keeps "up next" meaning the run right after the current entry: marks on entries already played, or after an entry
    /// that isn't up next, are cleared (they stay in the queue as ordinary entries).
    /// </summary>
    private void NormalizeUpNext()
    {
        var end = _index >= 0 ? UpNextEnd() : 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (i <= _index || i >= end)
            {
                _items[i].UpNext = false;
            }
        }
    }

    /// <summary>
    /// Gives <paramref name="added"/> original-order keys right after <paramref name="after"/>'s, so turning shuffle off later
    /// keeps them where they were inserted. Shifts the later keys; O(n).
    /// </summary>
    private void PlaceInOriginalOrder(Slot after, List<Slot> added)
    {
        foreach (var slot in _items)
        {
            if (slot.Order > after.Order)
            {
                slot.Order += added.Count;
            }
        }

        for (var k = 0; k < added.Count; k++)
        {
            added[k].Order = after.Order + 1 + k;
        }

        _nextOrder = _items.Concat(added).Max(s => s.Order) + 1;
    }

    /// <summary>With shuffle off the play order is the original order: renumbers the keys to match it after a move.</summary>
    private void RenumberOrder()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Order = i;
        }

        _nextOrder = _items.Count;
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
    /// Inserts <paramref name="added"/> (shuffled) at random positions after the current and up-next entries, keeping the relative order
    /// of the upcoming entries: a random interleave, O(n).
    /// </summary>
    private void MixIntoUpcoming(List<Slot> added)
    {
        ShuffleRange(added, 0, _random);
        var from = UpNextEnd(); // up-next entries stay next
        var upcoming = _items.GetRange(from, _items.Count - from);
        _items.RemoveRange(from, upcoming.Count);
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

        public long Order { get; set; } = order;

        /// <summary>Added by the user to play next (only meaningful in the run right after the current entry).</summary>
        public bool UpNext { get; set; }
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
