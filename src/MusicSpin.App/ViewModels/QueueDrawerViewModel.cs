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

/// <summary>One row of the queue drawer: any queue entry, or one of the radio station's next picks (dimmed).</summary>
public sealed partial class QueueRowViewModel(QueueItem item, int index) : ObservableObject
{
    private AlbumArt? _art;
    private int _artAsked;
    private Func<QueueRowViewModel, Task>? _loadArt;

    public QueueItem Item { get; } = item;

    /// <summary>The entry's place in the player's queue (0-based, in play order).</summary>
    public int Index { get; } = index;

    /// <summary>Asks for the album cover the first time something shows the row.</summary>
    internal void WatchArt(Func<QueueRowViewModel, Task> load) => _loadArt = load;

    public string Title => Item.Title ?? Path.GetFileNameWithoutExtension(Item.Path);

    public string Artist => Item.Artist ?? string.Empty;

    /// <summary>The thumbnail's placeholder names the album.</summary>
    public string Album => Item.Album ?? string.Empty;

    public string Duration => Item.DurationSeconds is { } seconds ? Formats.Time(seconds) : string.Empty;

    /// <summary>The cover, looked up the first time this is read. Rows that aren't shown never ask the library.</summary>
    public AlbumArt? Art
    {
        get
        {
            if (_loadArt is not null && Interlocked.Exchange(ref _artAsked, 1) == 0)
            {
                _ = _loadArt(this);
            }

            return _art;
        }
    }

    internal void SetArt(AlbumArt? art)
    {
        if (!ReferenceEquals(_art, art))
        {
            _art = art;
            OnPropertyChanged(nameof(Art));
        }
    }

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
/// The queue drawer: the whole queue, with the playing entry marked and the entries above it greyed. Up next (Play next
/// / Add to queue) still plays before the rest. On radio the station's next picks are listed under the queue. Every
/// action is one of the player's queue commands, and what the CLI changes shows up here.
/// </summary>
/// <remarks>
/// The list is read when the player says the queue changed, in pages. A track change only moves
/// <see cref="CurrentIndex"/>, so the rows are not rebuilt. A queue longer than <see cref="QueueWindow.MaxMaterialized"/>
/// shows a window (<see cref="ShownRows"/>) around the scroll position. Rows that were not there before flash, and the
/// view scrolls to the first of them.
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

    /// <summary>The notice when the row dragged has left the queue.</summary>
    public const string RowGone = "That track isn't in the queue any more.";

    /// <summary>How many entries one <c>queue</c> read asks for (the player's page cap).</summary>
    public const int PageSize = 10_000;

    private readonly PlayerViewModel _player;
    private readonly RadioViewModel _radio;
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly Notices _notices;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, string?> _albumOfTrack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlbumArt> _art = new(StringComparer.Ordinal);
    private readonly OwedReads _owed;
    private (int Length, string Mode)? _structure;
    private double _scrollY;
    private double _viewport;
    private double _rowHeight = QueueWindow.FallbackRowHeight;
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
        _owed = new OwedReads(time, ui);
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

    /// <summary>Every queue entry, in play order. Entries above <see cref="CurrentIndex"/> are played or skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(Summary))]
    [NotifyCanExecuteChangedFor(nameof(PlayTopCommand), nameof(StartSaveCommand), nameof(ClearCommand))]
    public partial IReadOnlyList<QueueRowViewModel> Rows { get; private set; } = [];

    /// <summary>The rows the list builds: every row, or a window of a very long queue.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<QueueRowViewModel> ShownRows { get; private set; } = [];

    /// <summary>Space above <see cref="ShownRows"/> when the queue is windowed.</summary>
    [ObservableProperty]
    public partial double TopSpacer { get; private set; }

    /// <summary>Space below <see cref="ShownRows"/> when the queue is windowed.</summary>
    [ObservableProperty]
    public partial double BottomSpacer { get; private set; }

    /// <summary>The playing entry's index, or -1 when nothing is queued. Moving it does not rebuild the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int CurrentIndex { get; private set; } = -1;

    /// <summary>While a radio station is on air: its next picks after the rows (mockup: #qRadioItems).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<QueueRowViewModel> Picks { get; private set; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The playing entry's place, "4 of 11"; null when the queue is empty.</summary>
    public string? Summary => QueuePositionText.Format(CurrentIndex >= 0 ? CurrentIndex : null, Rows.Count);

    /// <summary>The marker moved. The view scrolls that row into sight.</summary>
    public event EventHandler? MarkerMoved;

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
        _disposed = true;
        _player.QueueChanged -= OnQueueChanged;
        _player.StatusApplied -= OnStatusApplied;
        _player.PropertyChanged -= OnPlayerChanged;
        _closeTimer?.Dispose();
        _owed.Dispose();
    }

    // ---- actions --------------------------------------------------------------------------------------------------

    /// <summary>A row's ✕.</summary>
    [RelayCommand]
    private Task Remove(QueueRowViewModel row) => _notices.GuardAsync(() => _player.RemoveFromQueueAsync(row.Index));

    /// <summary>A click plays that entry. The list stays: the marker moves, and nothing is removed or reordered.</summary>
    [RelayCommand]
    private Task SkipTo(QueueRowViewModel row) => _notices.GuardAsync(() => _player.GotoQueueAsync(row.Index));

    /// <summary>A click on a radio pick plays it the same way.</summary>
    [RelayCommand]
    private Task PlayPick(QueueRowViewModel row) => _notices.GuardAsync(() => _player.GotoQueueAsync(row.Index));

    private bool HasRows() => Rows.Count > 0;

    /// <summary>
    /// Clear empties the whole queue and stops playback. The drawer shows the whole list, so Clear clears that list.
    /// Removing only the up-next run remains the player's <c>queue clear</c> with <c>upNext</c> (the CLI's <c>--up-next</c>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task Clear() => _notices.GuardAsync(() => _player.ClearQueueAsync(upNextOnly: false));

    /// <summary>▶ Play starts the queue from its first entry. The list stays and the marker moves there.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task PlayTop() => _notices.GuardAsync(() => _player.GotoQueueAsync(Rows[0].Index));

    /// <summary>End radio: the current track plays on, then the rows; the station's picks go.</summary>
    [RelayCommand]
    private Task EndRadio() => _notices.GuardAsync(() => _player.EndRadioAsync());

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
    /// Drops library tracks at <paramref name="at"/> (0: before the first row; the count: after the last). An empty queue
    /// starts with Play next. Right after the playing entry is Play next; at the end of the up-next run is Add to queue.
    /// Anywhere else they are added to that run and moved to the row. Past the last row they are appended.
    /// </summary>
    public Task InsertAsync(IReadOnlyList<TrackSummary> tracks, int at)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return _notices.GuardAsync(async () =>
        {
            if (tracks.Count == 0)
            {
                return;
            }

            var rows = Rows;
            // Nothing queued, or nothing playing: the drop starts the queue (Play next). On an empty queue that appends.
            if (rows.Count == 0 || _player.QueueIndex is not { } current)
            {
                await _player.AddToQueueAsync(tracks, QueuePosition.Next).ConfigureAwait(true);
                return;
            }

            var upNextEnd = current + 1 + _player.UpNext;
            var target = at >= rows.Count ? _player.QueueLength : rows[at].Index;
            if (target == current + 1)
            {
                await _player.AddToQueueAsync(tracks, QueuePosition.Next).ConfigureAwait(true);
                return;
            }

            if (target == upNextEnd)
            {
                await _player.AddToQueueAsync(tracks, QueuePosition.UpNext).ConfigureAwait(true);
                return;
            }

            if (at >= rows.Count)
            {
                await _player.AddToQueueAsync(tracks, QueuePosition.End).ConfigureAwait(true);
                return;
            }

            var anchor = rows[at];
            if (!await _player.AddToQueueAsync(tracks, QueuePosition.UpNext).ConfigureAwait(true))
            {
                return;
            }

            var guess = anchor.Index >= upNextEnd ? anchor.Index + tracks.Count : anchor.Index;
            for (var i = 0; i < tracks.Count; i++)
            {
                var pending = tracks.Skip(i).ToList();
                var run = await UpNextAsync(Math.Max(_player.UpNext, pending.Count) + 2).ConfigureAwait(true);
                var tail = run.Count >= pending.Count ? run.Skip(run.Count - pending.Count).ToList() : [];
                var place = await FindNearAsync(anchor, guess, pending.Count + 16).ConfigureAwait(true);
                if (place is null || !tail.Select(e => e.Item.Path).SequenceEqual(pending.Select(t => t.Path), StringComparer.Ordinal))
                {
                    if (i == 0)
                    {
                        _notices.Post(AddedLast);
                    }

                    return;
                }

                var added = tail[0];
                var to = place.Index > added.Index ? place.Index - 1 : place.Index;
                if (to != added.Index && !await _player.MoveInQueueAsync(added.Index, to).ConfigureAwait(true))
                {
                    return;
                }

                guess = place.Index > added.Index ? place.Index : place.Index + 1;
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

    /// <summary>Reads started (tests wait on these, not on time).</summary>
    internal int ReadsStarted => _reads;

    private bool _disposed;

    /// <summary>Reads finished: applied, superseded by a later one, or failed.</summary>
    internal int ReadsFinished { get; private set; }

    /// <summary>The queue's shape changed, so the rows have to be read. A new index alone does not.</summary>
    private bool StructureChanged()
        => _structure is not { } read || read.Length != _player.QueueLength || read.Mode != _player.QueueMode;

    /// <summary>
    /// Where the list is scrolled, so a long queue keeps a window there. A row height of 0 keeps the last measured one.
    /// </summary>
    public void NoteScroll(double offsetY, double viewportHeight, double rowHeight)
    {
        if (rowHeight > 0)
        {
            _rowHeight = rowHeight;
        }

        _scrollY = offsetY;
        _viewport = viewportHeight;
        PublishShown();
    }

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
        var indexAtStart = _player.QueueIndex;
        var listing = await ReadAllAsync().ConfigureAwait(true);
        if (listing is null || read != _reads || _disposed)
        {
            return;
        }

        var entries = listing.Items;
        var at = listing.CurrentIndex ?? -1;
        var known = Rows.Concat(Picks).ToDictionary(r => (r.Index, r.Item.Path));
        var made = new List<QueueRowViewModel>();
        QueueRowViewModel RowFor(QueueEntry e)
        {
            if (known.TryGetValue((e.Index, e.Item.Path), out var row))
            {
                return row;
            }

            row = new QueueRowViewModel(e.Item, e.Index);
            row.WatchArt(FindArtAsync);
            made.Add(row);
            return row;
        }

        var rows = entries.Select(RowFor).ToList();
        var picks = _player.IsRadio
            ? entries.Where(e => e.Index > at && !e.UpNext).Take(RadioPicks).Select(RowFor).ToList()
            : [];
        var changed = !rows.SequenceEqual(Rows);
        if (changed)
        {
            foreach (var row in rows.Except(made))
            {
                row.IsFresh = false;
            }
        }

        var fresh = _hasRead && changed ? MarkFresh(Rows, rows) : null;
        _hasRead = true;
        _structure = (listing.Total, _player.QueueMode);
        if (changed)
        {
            Rows = rows;
        }

        if (!picks.SequenceEqual(Picks))
        {
            Picks = picks;
        }

        PublishShown();
        var marker = _player.QueueIndex != indexAtStart && _player.QueueLength == listing.Total ? _player.QueueIndex ?? at : at;
        MoveMarker(marker);
        if (rows.Count == 0)
        {
            SaveForm.Close();
        }

        if (fresh is not null)
        {
            RowsAdded?.Invoke(this, fresh);
        }
    }

    private void MoveMarker(int index)
    {
        if (index == CurrentIndex)
        {
            return;
        }

        CurrentIndex = index;
        MarkerMoved?.Invoke(this, EventArgs.Empty);
    }

    private void PublishShown()
    {
        var slice = QueueWindow.Of(Rows.Count, _scrollY, _viewport, _rowHeight);
        IReadOnlyList<QueueRowViewModel> shown = slice.Count == Rows.Count ? Rows : Rows.Skip(slice.Start).Take(slice.Count).ToArray();
        if (TopSpacer != slice.Top)
        {
            TopSpacer = slice.Top;
        }

        if (BottomSpacer != slice.Bottom)
        {
            BottomSpacer = slice.Bottom;
        }

        if (ShownRows.Count == shown.Count)
        {
            var same = true;
            for (var i = 0; i < shown.Count; i++)
            {
                if (!ReferenceEquals(ShownRows[i], shown[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        ShownRows = shown;
    }

    /// <summary>The whole queue, <see cref="PageSize"/> entries at a time. A page whose total changed under the read is dropped.</summary>
    private async Task<QueueListing?> ReadAllAsync()
    {
        var first = await _player.QueueAsync(0, PageSize).ConfigureAwait(true);
        if (first is null || first.Items.Count >= first.Total || first.Items.Count < PageSize)
        {
            return first;
        }

        var items = new List<QueueEntry>(first.Total);
        items.AddRange(first.Items);
        var current = first.CurrentIndex;
        var offset = first.Items.Count;
        while (offset < first.Total)
        {
            var page = await _player.QueueAsync(offset, PageSize).ConfigureAwait(true);
            if (page is null || page.Total != first.Total)
            {
                return null;
            }

            if (page.Items.Count == 0)
            {
                break;
            }

            items.AddRange(page.Items);
            current = page.CurrentIndex ?? current;
            offset += page.Items.Count;
        }

        return new QueueListing(first.Total, 0, items.Count, current, items);
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
            catch (Exception ex) when (OwedReads.IsBusy(ex))
            {
                // Another process holds the library: the placeholder stays until the next check finds the album, as long
                // as the row is still shown then (a row read again later keeps this object, so it would never ask again).
                _owed.Owe(() =>
                {
                    if (row.Art is null && (IndexOf(Rows, row) >= 0 || IndexOf(Picks, row) >= 0))
                    {
                        _ = FindArtAsync(row);
                    }
                });
                return;
            }
            catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
            {
                return; // no thumbnail: the placeholder stays
            }

            _albumOfTrack[trackId] = albumId;
        }

        if (albumId is not null)
        {
            row.SetArt(_art.TryGetValue(albumId, out var art) ? art : _art[albumId] = new AlbumArt(albumId, CoverSize.Small, _covers));
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

    /// <summary>The entry that is <paramref name="row"/>, nearest <paramref name="guess"/>.</summary>
    private async Task<QueueEntry?> FindNearAsync(QueueRowViewModel row, int guess, int span)
    {
        var start = Math.Max(0, guess - 2);
        var listing = await _player.QueueAsync(start, Math.Clamp(span, 1, PageSize)).ConfigureAwait(true);
        return listing is null ? null : Nearest(listing.Items, row, e => e.Item.Path, e => e.Index);
    }

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

    /// <summary>A track change moves the marker. The queue is read again only when its length or radio mode changed.</summary>
    private void OnStatusApplied(object? sender, EventArgs e)
    {
        if (!_hasRead || StructureChanged())
        {
            _ = ReadAsync();
        }
        else
        {
            MoveMarker(_player.QueueIndex ?? -1);
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
