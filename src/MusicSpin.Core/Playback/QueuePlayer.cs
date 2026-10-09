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
/// background player, <c>musicspin serve</c> and (later) the desktop app.
/// <para>Semantics (documented for agents): <c>next</c>/<c>prev</c> always start playback; <c>next</c> on the last entry and
/// <c>prev</c> on the first are <see cref="MusicSpinErrorCode.InvalidState"/>; after the last entry ends the queue is
/// <see cref="PlayerStatus.Ended"/> and play/resume continue with entries added since, or restart from the first.</para>
/// </summary>
public sealed class QueuePlayer : IDisposable
{
    public const int MaxQueueLength = 100_000;
    private const int MaxSkippedKept = 50;

    private readonly IPlayer _player;
    private readonly Lock _gate = new();
    private readonly List<QueueItem> _items = [];
    private readonly List<SkippedItem> _skipped = [];
    private int _index = -1;
    private bool _ended;
    private bool _stoppedByUser;
    private string? _lastError;
    private bool _disposed;

    public QueuePlayer(IPlayer player, OutputInfo? output = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        Output = output;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.StateChanged += OnStateChanged;
        _player.TrackLoaded += OnTrackLoaded;
        _player.Error += OnPlayerError;
    }

    public OutputInfo? Output { get; }

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
            var items = _items.Skip(offset).Take(limit).Select((item, i) => new QueueEntry(offset + i, offset + i == _index, item)).ToList();
            return new QueueListing(_items.Count, offset, limit, _index >= 0 ? _index : null, items);
        }
    }

    /// <summary>
    /// Replaces the queue and loads the first playable entry from <paramref name="startIndex"/>. When no entry can be
    /// opened the current queue and playback are left untouched and FileNotFound is thrown.
    /// </summary>
    public PlayerStatus SetQueue(IReadOnlyList<QueueItem> items, int startIndex = 0, bool play = true, TimeSpan? start = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateItems(items);
        if (items.Count == 0)
        {
            throw Invalid("The queue must contain at least one entry.");
        }

        if (startIndex < 0 || startIndex >= items.Count)
        {
            throw Invalid($"start index {startIndex} is outside the queue (0-{items.Count - 1}).");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var skipped = new List<SkippedItem>();
            var loaded = -1;
            for (var i = startIndex; i < items.Count && loaded < 0; i++)
            {
                if (TryLoad(items[i], i, skipped))
                {
                    loaded = i;
                }
            }

            if (loaded < 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.FileNotFound,
                    $"None of the {items.Count - startIndex} queued file(s) could be played: {Describe(skipped)}");
            }

            _items.Clear();
            _items.AddRange(items);
            _skipped.Clear();
            _index = loaded;
            _ended = false;
            _lastError = null;
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
            _items.Add(new QueueItem(path));
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

            var first = _items.Count;
            _items.AddRange(items);
            if (_index < 0)
            {
                for (var i = first; i < _items.Count; i++)
                {
                    if (TryLoad(_items[i], i, null))
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

            return MoveAndPlay(index.Value, +1);
        }
    }

    public PlayerStatus Resume()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
            if (_ended)
            {
                return MoveAndPlay(_index + 1 < _items.Count ? _index + 1 : 0, +1);
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
            RequireQueue();
            if (_index + 1 >= _items.Count)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"Already at the last track ({_index + 1} of {_items.Count}). Add more with 'musicspin queue add'.");
            }

            return MoveAndPlay(_index + 1, +1);
        }
    }

    public PlayerStatus Previous()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
            if (_index <= 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    "Already at the first track. Use 'musicspin seek 0' to restart it.");
            }

            return MoveAndPlay(_index - 1, -1);
        }
    }

    /// <summary>Seeks to an absolute position. Negative or beyond the end of the track is InvalidArgument.</summary>
    public PlayerStatus Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireQueue();
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
            RequireQueue();
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
        try
        {
            _player.Stop();
        }
        catch (ObjectDisposedException)
        {
            // owner disposed the player first
        }
    }

    private PlayerStatus MoveAndPlay(int index, int direction)
    {
        for (var i = index; i >= 0 && i < _items.Count; i += direction)
        {
            if (TryLoad(_items[i], i, null))
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
            $"No playable track {(direction > 0 ? "after" : "before")} entry {index + 1 - direction}: the remaining file(s) were skipped (see 'skipped' in 'musicspin status').");
    }

    private bool TryLoad(QueueItem item, int index, List<SkippedItem>? into)
    {
        try
        {
            _player.Load(item.Path);
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
    /// Plain files carry no metadata until they are opened: copy what the decoder found into the current queue entry
    /// so queue listings show titles. Called under the lock.
    /// </summary>
    private void EnrichCurrent()
    {
        if (_index < 0 || _index >= _items.Count || _player.CurrentTrack is not { } track)
        {
            return;
        }

        var item = _items[_index];
        if (item.Title is not null || track.Title is null || !string.Equals(item.Path, track.Path, StringComparison.Ordinal))
        {
            return;
        }

        _items[_index] = item with
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
        EnrichCurrent();
        var has = _index >= 0 && _index < _items.Count;
        var track = has ? _player.CurrentTrack : null;
        return new PlayerStatus(
            _player.State,
            has ? Math.Round(_player.Position.TotalSeconds, 3) : 0,
            has ? Math.Round(_player.Duration.TotalSeconds, 3) : 0,
            _player.Volume,
            track is null ? null : TrackDto.From(track),
            has ? _index : null,
            _items.Count,
            has ? _items[_index] : null,
            Output,
            _ended,
            _lastError,
            _skipped.Count > 0 ? [.. _skipped] : null);
    }

    private void RequireQueue()
    {
        if (_items.Count == 0 || _index < 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                "The queue is empty. Start something with 'musicspin play <file> | --album <title> | --artist <name>'.");
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

    // Raised on a thread-pool thread after the inner player stopped at end of stream.
    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || _stoppedByUser || _index < 0 || _player.State != PlayerState.Stopped)
            {
                return;
            }

            TrackEnded?.Invoke(this, EventArgs.Empty);
            for (var i = _index + 1; i < _items.Count; i++)
            {
                if (TryLoad(_items[i], i, null))
                {
                    _index = i;
                    try
                    {
                        PlayCurrent();
                    }
                    catch (MusicSpinException)
                    {
                        // recorded in LastError and raised as Error; nobody to throw to on this thread
                    }

                    return;
                }
            }

            _ended = true;
            QueueEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStateChanged(object? sender, PlayerStateChangedEventArgs e) => StateChanged?.Invoke(this, e);

    private void OnTrackLoaded(object? sender, TrackLoadedEventArgs e) => TrackLoaded?.Invoke(this, e);

    private void OnPlayerError(object? sender, PlayerErrorEventArgs e) => Error?.Invoke(this, e);
}
