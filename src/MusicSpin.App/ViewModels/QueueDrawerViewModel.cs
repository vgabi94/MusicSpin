using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.ViewModels;

/// <summary>One row of the queue drawer: an up-next entry, or one of the radio station's next picks (dimmed).</summary>
public sealed partial class QueueRowViewModel(QueueItem item, int index) : ObservableObject
{
    public QueueItem Item { get; } = item;

    /// <summary>The entry's place in the player's queue (0-based, in play order).</summary>
    public int Index { get; } = index;

    public string Title => Item.Title ?? Path.GetFileNameWithoutExtension(Item.Path);

    public string Artist => Item.Artist ?? string.Empty;

    /// <summary>The thumbnail's placeholder names the album.</summary>
    public string Album => Item.Album ?? string.Empty;

    public string Duration => Item.DurationSeconds is { } seconds ? Formats.Time(seconds) : string.Empty;

    /// <summary>The cover of the entry's album (once the library said which album; none for a plain file).</summary>
    [ObservableProperty]
    public partial AlbumArt? Art { get; set; }

    /// <summary>Just added: the row flashes in the accent (mockup: .qi.fresh).</summary>
    [ObservableProperty]
    public partial bool IsFresh { get; set; }

    /// <summary>Being dragged to another place (mockup: .qi.dragging).</summary>
    [ObservableProperty]
    public partial bool IsDragged { get; set; }
}

/// <summary>
/// The queue drawer's Save as playlist (mockup: #qForm): the name, "New playlist" by default. A layer of its own, so Esc
/// cancels it before it closes the drawer.
/// </summary>
public sealed partial class PlaylistNameViewModel : LayerViewModel
{
    public const string DefaultName = "New playlist";

    /// <summary>The longest name the field takes (mockup: maxlength 40).</summary>
    public const int MaxLength = 40;

    public override int Order => LayerOrder.QueueSave;

    [ObservableProperty]
    public partial string Name { get; set; } = DefaultName;

    /// <summary>The name to save under: what was typed, trimmed, or the default.</summary>
    public string Chosen => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name.Trim();

    /// <summary>"New playlist", or "New playlist 2" and so on: the first that isn't <paramref name="taken"/> (in any case).</summary>
    public static string Unused(IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var name = DefaultName;
        for (var n = 2; names.Contains(name); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{DefaultName} {n}");
        }

        return name;
    }
}

/// <summary>
/// The queue drawer (mockup: "Queue drawer"): Up next, the tracks added to play after the current one, before the rest
/// of the album or playlist, or before the radio station's next picks (the player's up-next entries). Every action is
/// one of the player's queue commands, and what the CLI changes shows up here (the player reports queue changes).
/// </summary>
/// <remarks>
/// Rows are read from the player whenever it says the queue changed, and when the current entry, the counts or the
/// mode change. Rows that were not there before are fresh: they flash, and the view scrolls to the first of them.
/// Dragging an album or a track opens the drawer by itself; then it closes again at once if nothing was dropped, or
/// <see cref="CloseAfterDrop"/> after a drop, once the pointer has left it.
/// </remarks>
public sealed partial class QueueDrawerViewModel : LayerViewModel, IDisposable
{
    /// <summary>The radio station's picks shown under the rows.</summary>
    public const int RadioPicks = 6;

    /// <summary>How long a drawer that opened for a drag stays after the drop (mockup: 1400 ms).</summary>
    public static readonly TimeSpan CloseAfterDrop = TimeSpan.FromMilliseconds(1400);

    /// <summary>While the pointer is still over it then, it looks again this much later (mockup: 400 ms).</summary>
    public static readonly TimeSpan CloseRetry = TimeSpan.FromMilliseconds(400);

    /// <summary>The notice when tracks dropped between rows can't be put there: the queue changed under the drop.</summary>
    public const string AddedLast = "The queue changed during the drop, so the tracks went to the end of Up next.";

    /// <summary>The notice when the row dragged has left the queue (it played, or the CLI removed it).</summary>
    public const string RowGone = "That track isn't up next any more.";

    private readonly PlayerViewModel _player;
    private readonly RadioViewModel _radio;
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly Notices _notices;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, string?> _albumOfTrack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlbumArt> _art = new(StringComparer.Ordinal);
    private (int? Index, int Length, int UpNext, string Mode, string? Path)? _readFor;
    private QueueItem? _current;
    private int _reads;
    private bool _hasRead;
    private bool _openedForDrag;
    private ITimer? _closeTimer;

    public QueueDrawerViewModel(PlayerViewModel player, RadioViewModel radio, ILibraryService library, ICoverService covers, Notices notices, TimeProvider time,
        IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(radio);
        _player = player;
        _radio = radio;
        _library = library;
        _covers = covers;
        _notices = notices;
        _time = time;
        _ui = ui;
        _player.QueueChanged += OnQueueChanged;
        _player.StatusApplied += OnStatusApplied;
        _player.PropertyChanged += OnPlayerChanged;
        _radio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RadioViewModel.Description))
            {
                OnPropertyChanged(nameof(RadioDescription));
            }
        };
        SaveForm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsOpen))
            {
                OnPropertyChanged(nameof(IsSaving));
            }
        };
    }

    public override int Order => LayerOrder.QueueDrawer;

    /// <summary>The up-next entries, in the order they play.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(Summary))]
    [NotifyCanExecuteChangedFor(nameof(PlayTopCommand), nameof(StartSaveCommand), nameof(ClearCommand))]
    public partial IReadOnlyList<QueueRowViewModel> Rows { get; private set; } = [];

    /// <summary>While a radio station is on air: its next picks after the rows (mockup: #qRadioItems).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<QueueRowViewModel> Picks { get; private set; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>"3 tracks · 12 min"; null when empty (mockup: #qSum).</summary>
    public string? Summary => Rows.Count == 0 ? null : string.Create(CultureInfo.CurrentCulture,
        $"{Rows.Count:N0} {(Rows.Count == 1 ? "track" : "tracks")} · {Math.Round(Rows.Sum(r => r.Item.DurationSeconds ?? 0) / 60):N0} min");

    public bool IsRadio => _player.IsRadio;

    /// <summary>The station on air: "Jazz or Ambient · 1960s" (#23).</summary>
    public string? RadioDescription => _radio.Description;

    /// <summary>Save as playlist: the name field instead of the footer's buttons.</summary>
    public PlaylistNameViewModel SaveForm { get; } = new();

    public bool IsSaving => SaveForm.IsOpen;

    /// <summary>Something dragged is over the drawer (mockup: .queue.over): the empty box lights up, the drop line shows.</summary>
    [ObservableProperty]
    public partial bool IsDragOver { get; set; }

    /// <summary>The pointer is over the drawer (the window reports it, also while dragging).</summary>
    public bool IsPointerOver { get; set; }

    /// <summary>A playlist was saved (the sidebar shows it).</summary>
    public event EventHandler<PlaylistSummaryItem>? PlaylistSaved;

    /// <summary>Rows were added; the argument is the first, which the view scrolls into view.</summary>
    public event EventHandler<QueueRowViewModel>? RowsAdded;

    public void Dispose()
    {
        _player.QueueChanged -= OnQueueChanged;
        _player.StatusApplied -= OnStatusApplied;
        _player.PropertyChanged -= OnPlayerChanged;
        _closeTimer?.Dispose();
    }

    // ---- actions --------------------------------------------------------------------------------------------------

    /// <summary>A row's ✕.</summary>
    [RelayCommand]
    private Task Remove(QueueRowViewModel row) => _notices.GuardAsync(() => _player.RemoveFromQueueAsync(row.Index));

    /// <summary>A click on a row skips to it: the rows above it are dropped (they were never played).</summary>
    [RelayCommand]
    private Task SkipTo(QueueRowViewModel row) => _notices.GuardAsync(() => _player.PlayIndexAsync(row.Index));

    /// <summary>A click on a radio pick skips ahead to it.</summary>
    [RelayCommand]
    private Task PlayPick(QueueRowViewModel row) => _notices.GuardAsync(() => _player.PlayIndexAsync(row.Index));

    private bool HasRows() => Rows.Count > 0;

    /// <summary>Clear: the up-next entries go; the album (or the station) plays on.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task Clear() => _notices.GuardAsync(() => _player.ClearQueueAsync(upNextOnly: true));

    /// <summary>▶ Play: the top row plays now, the rest follow.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task PlayTop() => _notices.GuardAsync(() => _player.PlayIndexAsync(Rows[0].Index));

    /// <summary>End radio: the current track plays on, then the rows; the station's picks go.</summary>
    [RelayCommand]
    private Task EndRadio() => _notices.GuardAsync(() => _player.EndRadioAsync([.. new[] { _current }.OfType<QueueItem>(), .. Rows.Select(r => r.Item)]));

    /// <summary>Save as playlist: the name field, with a name no playlist has yet.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private async Task StartSave()
    {
        var taken = new List<string>();
        await _notices.GuardAsync(async () => taken.AddRange((await _library.PlaylistsAsync().ConfigureAwait(true)).Select(p => p.Name))).ConfigureAwait(true);
        SaveForm.Name = PlaylistNameViewModel.Unused(taken);
        SaveForm.Open();
    }

    /// <summary>Saves the rows as a library playlist. A name already taken leaves the field open, with a notice.</summary>
    [RelayCommand]
    private Task Save() => _notices.GuardAsync(async () =>
    {
        var playlist = await _library.CreatePlaylistAsync(SaveForm.Chosen, [.. Rows.Select(r => r.Item.Path)]).ConfigureAwait(true);
        SaveForm.Close();
        PlaylistSaved?.Invoke(this, playlist);
    });

    /// <summary>
    /// Drops library tracks at <paramref name="at"/> (0: first). The player adds up next at either end of the rows, so in
    /// between they are added last and moved up into place. The moves come from the queue read again after the add: a
    /// track may have ended, or the CLI changed the queue, since the rows were read.
    /// </summary>
    public Task InsertAsync(IReadOnlyList<TrackSummary> tracks, int at)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return _notices.GuardAsync(async () =>
        {
            var rows = Rows;
            if (tracks.Count == 0)
            {
                return;
            }

            if (at <= 0 || rows.Count == 0)
            {
                await _player.AddToQueueAsync(tracks, QueuePosition.Next).ConfigureAwait(true);
                return;
            }

            if (!await _player.AddToQueueAsync(tracks, QueuePosition.UpNext).ConfigureAwait(true) || at >= rows.Count)
            {
                return;
            }

            var upNext = await UpNextAsync(rows.Count + tracks.Count).ConfigureAwait(true);
            var added = upNext.Count >= tracks.Count ? upNext.Skip(upNext.Count - tracks.Count).ToList() : [];
            var target = Nearest(upNext.Take(upNext.Count - added.Count), rows[at], e => e.Item.Path, e => e.Index);
            if (target is null || !added.Select(e => e.Item.Path).SequenceEqual(tracks.Select(t => t.Path), StringComparer.Ordinal))
            {
                _notices.Post(AddedLast);
                return;
            }

            for (var i = 0; i < added.Count; i++)
            {
                if (!await _player.MoveInQueueAsync(added[i].Index, target.Index + i).ConfigureAwait(true))
                {
                    return; // the rest stay at the end of the rows
                }
            }
        });
    }

    /// <summary>
    /// Drops a row at <paramref name="at"/> (the row above which it goes; the count: last). The rows may have been read
    /// again during the drag (a track ended, say): then it's the same entry among the new rows.
    /// </summary>
    public Task MoveAsync(QueueRowViewModel row, int at)
    {
        ArgumentNullException.ThrowIfNull(row);
        var rows = Rows;
        var from = rows.Contains(row) ? row : Nearest(rows, row, r => r.Item.Path, r => r.Index);
        if (from is null)
        {
            _notices.Post(RowGone);
            return Task.CompletedTask;
        }

        var fromAt = IndexOf(rows, from);
        var to = Math.Clamp(at > fromAt ? at - 1 : at, 0, rows.Count - 1);
        return to == fromAt ? Task.CompletedTask : _notices.GuardAsync(() => _player.MoveInQueueAsync(from.Index, rows[to].Index));
    }

    // ---- dragging -------------------------------------------------------------------------------------------------

    /// <summary>A drag started: one from the library opens the drawer if it's closed (and it closes again after).</summary>
    public void BeginDrag(bool fromLibrary)
    {
        if (fromLibrary && !IsOpen)
        {
            Open();
            _openedForDrag = true;
        }
    }

    /// <summary>The drag ended, with a drop on the drawer or not.</summary>
    public void EndDrag(bool dropped)
    {
        IsDragOver = false;
        if (!_openedForDrag)
        {
            return;
        }

        _openedForDrag = false;
        if (dropped)
        {
            CloseLater(CloseAfterDrop);
        }
        else
        {
            Close();
        }
    }

    protected override void OnOpenChanged(bool isOpen)
    {
        _closeTimer?.Dispose();
        _closeTimer = null;
        _openedForDrag = false;
        if (!isOpen)
        {
            SaveForm.Close();
        }
    }

    private void CloseLater(TimeSpan after)
    {
        _closeTimer?.Dispose();
        _closeTimer = _time.CreateTimer(_ => _ui.Post(CloseOnceLeft), null, after, Timeout.InfiniteTimeSpan);
    }

    private void CloseOnceLeft()
    {
        if (!IsOpen || _closeTimer is null)
        {
            return;
        }

        if (IsPointerOver)
        {
            CloseLater(CloseRetry);
        }
        else
        {
            Close();
        }
    }

    // ---- reading the queue ----------------------------------------------------------------------------------------

    private (int? Index, int Length, int UpNext, string Mode, string? Path) Key()
        => (_player.QueueIndex, _player.QueueLength, _player.UpNext, _player.QueueMode, _player.Path);

    /// <summary>Reads started (tests wait on these, not on time).</summary>
    internal int ReadsStarted => _reads;

    /// <summary>Reads finished: applied, superseded by a later one, or failed.</summary>
    internal int ReadsFinished { get; private set; }

    /// <summary>Reads the current entry, the up-next entries and (on radio) the picks after them; the latest read wins.</summary>
    private async Task ReadAsync()
    {
        var read = ++_reads;
        try
        {
            await ReadOnceAsync(read).ConfigureAwait(true);
        }
        finally
        {
            ReadsFinished++;
        }
    }

    private async Task ReadOnceAsync(int read)
    {
        var key = Key();
        QueueListing? listing = null;
        if (key.Index is { } current)
        {
            listing = await ReadPastUpNextAsync(current, key.UpNext).ConfigureAwait(true);
            if (listing is null)
            {
                return;
            }
        }

        if (read != _reads)
        {
            return;
        }

        _readFor = key;
        var entries = listing?.Items ?? [];
        _current = entries.FirstOrDefault(e => e.Current)?.Item;
        // The listing's own current entry: the track may have moved on during the read, ahead of the status that says so.
        var at = listing?.CurrentIndex ?? key.Index;
        var after = entries.Where(e => at is { } i && e.Index > i).ToList();

        // An entry still where it was keeps its row (a flash under way, its cover); a read that changes nothing leaves
        // the lists as they are, so the view keeps its rows too.
        var known = Rows.Concat(Picks).ToDictionary(r => (r.Index, r.Item.Path));
        var made = new List<QueueRowViewModel>();
        QueueRowViewModel RowFor(QueueEntry e)
        {
            if (known.TryGetValue((e.Index, e.Item.Path), out var row))
            {
                return row;
            }

            row = new QueueRowViewModel(e.Item, e.Index);
            made.Add(row);
            return row;
        }

        var rows = after.TakeWhile(e => e.UpNext).Select(RowFor).ToList();
        var picks = _player.IsRadio ? after.SkipWhile(e => e.UpNext).Take(RadioPicks).Select(RowFor).ToList() : [];
        var changed = !rows.SequenceEqual(Rows);
        if (changed)
        {
            // Only the rows this change brought are fresh (a row shown again would flash again).
            foreach (var row in rows.Except(made))
            {
                row.IsFresh = false;
            }
        }

        var fresh = _hasRead && changed ? MarkFresh(Rows, rows) : null;
        _hasRead = true;
        foreach (var row in made)
        {
            _ = FindArtAsync(row);
        }

        if (changed)
        {
            Rows = rows;
        }

        if (!picks.SequenceEqual(Picks))
        {
            Picks = picks;
        }
        if (rows.Count == 0)
        {
            SaveForm.Close();
        }

        if (fresh is not null)
        {
            RowsAdded?.Invoke(this, fresh);
        }
    }

    /// <summary>
    /// The queue from <paramref name="current"/> until past the up-next entries (and on radio, the picks after them), or
    /// to its end. The status's count only sets the first page: a queueChanged can arrive before the status that counts
    /// the new entries, and a page cut short there would make the rows it missed look new on the next read.
    /// </summary>
    private async Task<QueueListing?> ReadPastUpNextAsync(int current, int upNext)
    {
        var after = _player.IsRadio ? RadioPicks : 1;
        var limit = 1 + upNext + after;
        while (true)
        {
            var listing = await _player.QueueAsync(current, limit).ConfigureAwait(true);
            if (listing is null)
            {
                return null;
            }

            var past = listing.Items.Where(e => e.Index > (listing.CurrentIndex ?? current)).ToList();
            // Fewer than asked for: the end of the queue (or as much as the player gives at once).
            if (listing.Items.Count < limit || past.Count >= past.TakeWhile(e => e.UpNext).Count() + after)
            {
                return listing;
            }

            limit *= 2;
        }
    }

    /// <summary>Marks the rows that weren't there before (by path, each old row matching one new); returns the first.</summary>
    private static QueueRowViewModel? MarkFresh(IReadOnlyList<QueueRowViewModel> before, IReadOnlyList<QueueRowViewModel> now)
    {
        var left = before.GroupBy(r => r.Item.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        QueueRowViewModel? first = null;
        foreach (var row in now)
        {
            if (left.TryGetValue(row.Item.Path, out var n) && n > 0)
            {
                left[row.Item.Path] = n - 1;
                continue;
            }

            row.IsFresh = true;
            first ??= row;
        }

        return first;
    }

    private async Task FindArtAsync(QueueRowViewModel row)
    {
        if (row.Item.TrackId is not { } trackId)
        {
            return;
        }

        if (!_albumOfTrack.TryGetValue(trackId, out var albumId))
        {
            try
            {
                albumId = await _library.AlbumIdOfTrackAsync(trackId).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
            {
                return; // no thumbnail: the placeholder stays
            }

            _albumOfTrack[trackId] = albumId;
        }

        if (albumId is not null)
        {
            row.Art = _art.TryGetValue(albumId, out var art) ? art : _art[albumId] = new AlbumArt(albumId, CoverSize.Small, _covers);
        }
    }

    private static int IndexOf(IReadOnlyList<QueueRowViewModel> rows, QueueRowViewModel row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The entry among <paramref name="entries"/> that is <paramref name="row"/>: the same file, nearest where it was.</summary>
    private static T? Nearest<T>(IEnumerable<T> entries, QueueRowViewModel row, Func<T, string> path, Func<T, int> index)
        where T : class
        => entries.Where(e => string.Equals(path(e), row.Item.Path, StringComparison.Ordinal))
            .MinBy(e => Math.Abs(index(e) - row.Index));

    /// <summary>The up-next entries as the player has them now (none when it can't say).</summary>
    private async Task<IReadOnlyList<QueueEntry>> UpNextAsync(int expected)
    {
        if (_player.QueueIndex is not { } current)
        {
            return [];
        }

        // From the current entry as last read, with room for a track that ended since.
        var listing = await _player.QueueAsync(current, expected + 2).ConfigureAwait(true);
        return listing?.CurrentIndex is { } now ? [.. listing.Items.Where(e => e.Index > now).TakeWhile(e => e.UpNext)] : [];
    }

    private void OnQueueChanged(object? sender, EventArgs e) => _ = ReadAsync();

    /// <summary>The status says the queue moved on (a track ended, say) without a queueChanged event: read it again.</summary>
    private void OnStatusApplied(object? sender, EventArgs e)
    {
        if (_readFor != Key())
        {
            _ = ReadAsync();
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsRadio):
                OnPropertyChanged(nameof(IsRadio));
                break;
        }
    }
}
