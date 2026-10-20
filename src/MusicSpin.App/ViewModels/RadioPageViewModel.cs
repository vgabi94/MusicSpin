using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>Where a tag stands in the station being composed.</summary>
public enum RadioTagState
{
    Off,
    Included,
    LeftOut,
}

/// <summary>
/// A tag on the Radio page (mockup: .tag): its name, how many tracks a station of it alone plays, and whether the draft
/// includes it (+, the accent) or leaves it out (−, dashed red, struck through).
/// </summary>
public sealed partial class RadioChipViewModel(RadioPageViewModel page, RadioTag tag, string label, string? detail, int? tracks, RadioTagState state)
{
    /// <summary>The tag's value (as included).</summary>
    public RadioTag Tag { get; } = tag;

    public string Label { get; } = label;

    /// <summary>What tells it from a tag of the same name: an album's artist (its tooltip, "Greatest Hits — Queen").</summary>
    public string? ToolTip { get; } = detail is null ? null : $"{label} — {detail}";

    /// <summary>"12"; null while not known (words just typed).</summary>
    public string? Count { get; } = tracks?.ToString("N0", CultureInfo.CurrentCulture);

    public RadioTagState State { get; } = state;

    public bool IsIncluded => State == RadioTagState.Included;

    public bool IsLeftOut => State == RadioTagState.LeftOut;

    /// <summary>"+" when included, "−" when left out (mockup: .tag.inc::before, .tag.exc::before).</summary>
    public string? Mark => State switch
    {
        RadioTagState.Included => "+",
        RadioTagState.LeftOut => "−",
        _ => null,
    };

    /// <summary>"Jazz, 12 tracks, included", for screen readers.</summary>
    public string Name => string.Join(", ", new[]
    {
        ToolTip ?? Label, Count is null ? null : $"{Count} {(tracks == 1 ? "track" : "tracks")}", State == RadioTagState.Off ? null : State == RadioTagState.Included ? "included" : "left out",
    }.OfType<string>());

    /// <summary>A click: off → included → left out → off (Hi-Res: off ↔ included).</summary>
    [RelayCommand]
    private void Cycle() => page.Cycle(Tag);

    /// <summary>A right-click: straight to left out (again: off). Hi-Res can't be left out.</summary>
    [RelayCommand]
    private void LeaveOut() => page.LeaveOut(Tag);
}

/// <summary>A line after a row's tags: "+12 more · search to find them", or how to find what the row holds.</summary>
public sealed record RadioNote(string Text);

/// <summary>One row of tags: one CLI radio filter (mockup: .facet), its tags (<see cref="RadioChipViewModel"/>) then maybe a <see cref="RadioNote"/>.</summary>
public sealed record RadioFacetViewModel(string Title, IReadOnlyList<object> Items);

/// <summary>
/// Radio (#24): compose a station from tags, one row per CLI radio filter, and start it. Values in one row mix (OR), each
/// row narrows the station (AND), and a value left out always wins, as in <c>music-spin radio</c>. The title bar's search
/// filters the tags (chosen ones stay first). The station card on the right describes the draft, says what it would
/// play, and starts it, switches to it, or, while it is the station on air, ends it and shows its next picks. Counts come
/// from the library; the pool's size is read again a moment after the tags or the search last changed.
/// </summary>
public sealed partial class RadioPageViewModel : PageViewModel, IDisposable
{
    public const string Hint = "Click a tag to add it, click again to leave it out, once more to clear it. Tags in one row mix; each row narrows the station.";

    public const string NothingMatches = "Nothing matches. Remove a tag or leave less out.";

    /// <summary>The size line while the draft's size isn't known yet (it just changed, or the library is busy).</summary>
    public const string Counting = "Counting…";

    public const string EmptyLibrary = NoMusic;

    /// <summary>The Artists row shows this many (the most tracks first) until a search finds the others.</summary>
    public const int TopArtists = 10;

    /// <summary>The station card shows this many of the station's next picks while it is on air.</summary>
    public const int NextPickCount = 3;

    /// <summary>How long the tags and the search stay still before the pool's size is read again.</summary>
    public static readonly TimeSpan CountDelay = TimeSpan.FromMilliseconds(250);

    private readonly ILibraryService _library;
    private readonly RadioViewModel _radio;
    private readonly QueueDrawerViewModel _queue;
    private readonly Notices _notices;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private RadioTagCounts? _counts;
    private Dictionary<string, string?> _albumArtists = new(StringComparer.Ordinal);
    private IReadOnlyList<RadioTag> _followed = [];
    private (string Words, int Tracks)? _words;
    private readonly OwedReads _owed;
    private ITimer? _countTimer;
    private int _counting;

    public RadioPageViewModel(ILibraryService library, RadioViewModel radio, QueueDrawerViewModel queue, Notices notices, TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(queue);
        _library = library;
        _radio = radio;
        _queue = queue;
        _notices = notices;
        _time = time;
        _ui = ui;
        _owed = new OwedReads(time, ui);
        _radio.PropertyChanged += OnRadioChanged;
        _queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueDrawerViewModel.Picks))
            {
                OnPropertyChanged(nameof(NextPicks));
            }
        };
        FollowStation();
    }

    public override string Title => "Radio";

    /// <summary>The station being composed: its tags, in the order they were chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(CanClear))]
    public partial IReadOnlyList<RadioTag> Draft { get; private set; } = [];

    /// <summary>The rows of tags.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RadioFacetViewModel> Rows { get; private set; } = [];

    /// <summary>What the draft plays, once read (null while it isn't known: never a size of another draft).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PoolText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial RadioPoolSize? Pool { get; private set; }

    /// <summary>The draft is the station on air (mockup: .station.live).</summary>
    public bool IsLive => _radio.Plays(Draft);

    /// <summary>"On air", or "New station".</summary>
    public string Kicker => IsLive ? "On air" : "New station";

    /// <summary>The draft's description, its names marked (shown strong).</summary>
    public IReadOnlyList<DescriptionPart> Description => _radio.Describe(Draft);

    /// <summary>"73 tracks · 6 artists · 5 h 10 min", <see cref="NothingMatches"/>, or <see cref="Counting"/> until it is known.</summary>
    public string PoolText => Pool switch
    {
        null => Counting,
        { Tracks: 0 } => NothingMatches,
        var p => string.Create(CultureInfo.CurrentCulture,
            $"{p.Tracks:N0} {(p.Tracks == 1 ? "track" : "tracks")} · {p.Artists:N0} {(p.Artists == 1 ? "artist" : "artists")} · {Duration(p.Seconds)}"),
    };

    /// <summary>▶ Start radio, or, while another station is on air, ▶ Switch to this station.</summary>
    public string StartLabel => _radio.IsOnAir ? "Switch to this station" : "Start radio";

    public bool ShowsStart => !IsLive;

    /// <summary>Another station is on air: Undo changes puts its tags back.</summary>
    public bool ShowsUndo => _radio.IsOnAir && !IsLive;

    public bool ShowsEnd => IsLive;

    public bool CanClear => Draft.Count > 0;

    /// <summary>While the draft is on air: the station's next picks (the queue drawer's).</summary>
    public IReadOnlyList<QueueRowViewModel> NextPicks => IsLive ? [.. _queue.Picks.Take(NextPickCount)] : [];

    /// <summary>The library's tags and their counts; the rows and the pool follow.</summary>
    public override async Task LoadAsync()
    {
        var counts = await _library.RadioTagsAsync().ConfigureAwait(true);
        _counts = counts;
        _albumArtists = counts.Albums.ToDictionary(a => a.Value, a => a.Detail, StringComparer.Ordinal);
        _radio.KnowAlbums(counts.Albums.Select(a => (a.Value, a.Name)));
        IsLibraryEmpty = counts.Formats.Count == 0; // every track has a format
        EmptyText = IsLibraryEmpty ? EmptyLibrary : null;
        OnPropertyChanged(nameof(Description)); // albums by their titles now
        Filter();
        CountSoon();
    }

    /// <summary>A click on a tag: off → included → left out → off; Hi-Res (and Favorites) only on and off.</summary>
    public void Cycle(RadioTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var found = RadioStation.Find(Draft, tag);
        Draft = found is null ? [.. Draft, tag.WithExcluded(false)]
            : !found.Excluded && CanLeaveOut(tag) ? Replace(found, found.WithExcluded(true))
            : [.. Draft.Where(t => t != found)];
    }

    /// <summary>A right-click on a tag: straight to left out, or off when it already is. Hi-Res can't be left out.</summary>
    public void LeaveOut(RadioTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (!CanLeaveOut(tag))
        {
            return;
        }

        var found = RadioStation.Find(Draft, tag);
        Draft = found is null ? [.. Draft, tag.WithExcluded(true)]
            : found.Excluded ? [.. Draft.Where(t => t != found)]
            : Replace(found, found.WithExcluded(true));
    }

    public void Dispose()
    {
        _countTimer?.Dispose();
        _countTimer = null;
        _owed.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanStart() => !IsLive && Pool is { Tracks: > 0 };

    /// <summary>▶ Start radio, or Switch to this station.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Start() => _radio.StartAsync(Draft);

    /// <summary>Undo changes: back to the station on air.</summary>
    [RelayCommand]
    private void Undo() => Draft = _radio.OnAir;

    /// <summary>End radio: the current track plays on, then the up-next entries; the station's picks go.</summary>
    [RelayCommand]
    private Task End() => _radio.EndAsync();

    /// <summary>Clear tags.</summary>
    [RelayCommand]
    private void Clear() => Draft = [];

    partial void OnDraftChanged(IReadOnlyList<RadioTag> value)
    {
        Pool = null; // the old draft's size says nothing about this one (and Start waits for this one's)
        NotifyStation();
        Filter();
        CountSoon();
    }

    protected override void Filter()
    {
        if (_counts is not { } counts)
        {
            return;
        }

        var words = SearchText.Trim();
        bool Hit(RadioTag tag) => Matches(_radio.Label(tag)); // each word anywhere, as on the other pages
        RadioChipViewModel Chip(RadioTag tag, int? tracks)
        {
            var chosen = RadioStation.Find(Draft, tag);
            return new RadioChipViewModel(this, tag, _radio.Label(tag), tag.Kind == RadioTagKind.Album ? _albumArtists.GetValueOrDefault(tag.Value) : null, tracks,
                chosen is null ? RadioTagState.Off : chosen.Excluded ? RadioTagState.LeftOut : RadioTagState.Included);
        }

        // A row: the tags in the station first, then the others the search lets through (mockup: renderRadioPage, row).
        RadioFacetViewModel? Row(string title, RadioTagKind kind, IReadOnlyList<(RadioTag Tag, int? Tracks)> values, int limit = 0, bool searchOnly = false,
            string? hint = null, RadioChipViewModel? extra = null)
        {
            var chosen = Draft.Where(t => t.Kind == kind).Select(t => t.WithExcluded(false)).ToList();
            int? TracksOf(RadioTag tag) => values.FirstOrDefault(v => RadioStation.Find([v.Tag], tag) is not null).Tracks;
            var rest = searchOnly && words.Length == 0 ? []
                : values.Where(v => RadioStation.Find(chosen, v.Tag) is null && Hit(v.Tag)).ToList();
            var shown = limit > 0 && words.Length == 0 ? [.. rest.Take(Math.Max(0, limit - chosen.Count))] : rest;
            if (words.Length > 0 && chosen.Count == 0 && rest.Count == 0 && extra is null)
            {
                return null;
            }

            var more = rest.Count - shown.Count;
            var note = more > 0 ? string.Create(CultureInfo.CurrentCulture, $"+{more:N0} more · search to find them")
                : chosen.Count == 0 && shown.Count == 0 && extra is null ? hint : null;
            List<object> items = [.. chosen.Select(t => Chip(t, TracksOf(t))), .. shown.Select(v => Chip(v.Tag, v.Tracks))];
            if (extra is not null)
            {
                items.Add(extra);
            }

            if (note is not null)
            {
                items.Add(new RadioNote(note));
            }

            return new RadioFacetViewModel(title, items);
        }

        static List<(RadioTag, int?)> Values(RadioTagKind kind, IEnumerable<RadioTagCount> counts)
            => [.. counts.Select(c => (new RadioTag(kind, c.Value), (int?)c.Tracks))];

        var hiRes = new RadioTag(RadioTagKind.HiRes);
        List<(RadioTag, int?)> typed = words.Length == 0 ? []
            : [(new RadioTag(RadioTagKind.Words, words), _words is { } w && w.Words == words ? w.Tracks : null)];
        Rows = [.. new[]
        {
            Row("Genres", RadioTagKind.Genre, Values(RadioTagKind.Genre, counts.Genres)),
            Row("Decades", RadioTagKind.Years, Values(RadioTagKind.Years, counts.Decades)),
            Row("Formats", RadioTagKind.Format, Values(RadioTagKind.Format, counts.Formats),
                extra: Hit(hiRes) || RadioStation.Find(Draft, hiRes) is not null ? Chip(hiRes, counts.HiRes) : null),
            Row("Artists", RadioTagKind.Artist, Values(RadioTagKind.Artist, counts.Artists), limit: TopArtists),
            Row("Albums", RadioTagKind.Album, Values(RadioTagKind.Album, counts.Albums), searchOnly: true, hint: "Search to add an album"),
            Row("Words", RadioTagKind.Words, typed, hint: "Search to match words in titles, like “live”"),
        }.OfType<RadioFacetViewModel>()];
        if (words.Length > 0 && _words?.Words != words)
        {
            CountSoon();
        }
    }

    private static bool CanLeaveOut(RadioTag tag) => tag.Kind is not (RadioTagKind.HiRes or RadioTagKind.Favorites);

    private List<RadioTag> Replace(RadioTag old, RadioTag replacement) => [.. Draft.Select(t => t == old ? replacement : t)];

    /// <summary>"5 h 10 min", "42 min".</summary>
    private static string Duration(double seconds)
        => seconds >= 3600
            ? string.Create(CultureInfo.CurrentCulture, $"{Math.Floor(seconds / 3600):N0} h {Math.Round(seconds % 3600 / 60):N0} min")
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Round(seconds / 60):N0} min");

    /// <summary>Reads the pool's size (and the typed words' count) once the tags and the search have been still for <see cref="CountDelay"/>.</summary>
    private void CountSoon()
    {
        if (!IsShown)
        {
            return; // read when the page is opened
        }

        _countTimer?.Dispose();
        _countTimer = _time.CreateTimer(_ => _ui.Post(() => _ = CountAsync()), null, CountDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The latest count started wins (a slower earlier one is dropped). It runs in the background, so a busy library
    /// (a scan from the CLI, say) says nothing: the count is owed and tried again at the next library check.
    /// </summary>
    private async Task CountAsync()
    {
        try
        {
            await _notices.GuardAsync(CountOnceAsync).ConfigureAwait(true);
        }
        finally
        {
            CountsFinished++;
        }
    }

    /// <summary>Counts that have finished, applied or dropped (tests wait on it).</summary>
    internal int CountsFinished { get; private set; }

    private async Task CountOnceAsync()
    {
        var count = ++_counting;
        var draft = Draft;
        var words = SearchText.Trim();
        RadioPoolSize pool;
        RadioPoolSize? typed;
        try
        {
            pool = await _library.RadioPoolAsync(RadioStation.FilterOf(draft)).ConfigureAwait(true);
            typed = words.Length == 0 ? null : await _library.RadioPoolAsync(RadioStation.FilterOf([new RadioTag(RadioTagKind.Words, words)])).ConfigureAwait(true);
        }
        catch (MusicSpinException ex) when (OwedReads.IsBusy(ex))
        {
            _owed.Owe(() =>
            {
                if (IsShown && count == _counting)
                {
                    _ = CountAsync(); // still the latest count wanted
                }
            });
            return;
        }

        if (count != _counting)
        {
            return;
        }

        Pool = pool;
        if (typed is { } t && _words?.Words != words)
        {
            _words = (words, t.Tracks);
            Filter();
        }
    }

    private void OnRadioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioViewModel.OnAir) or nameof(RadioViewModel.IsOnAir))
        {
            FollowStation();
            NotifyStation();
        }
    }

    /// <summary>A new station on air (started here or from the CLI) becomes the draft (mockup: startRadio).</summary>
    private void FollowStation()
    {
        if (_radio.IsOnAir && !RadioStation.Same(_radio.OnAir, _followed))
        {
            _followed = _radio.OnAir;
            Draft = _radio.OnAir;
        }
        else if (!_radio.IsOnAir)
        {
            _followed = [];
        }
    }

    private void NotifyStation()
    {
        foreach (var name in new[] { nameof(IsLive), nameof(Kicker), nameof(StartLabel), nameof(ShowsStart), nameof(ShowsUndo), nameof(ShowsEnd), nameof(NextPicks) })
        {
            OnPropertyChanged(name);
        }

        StartCommand.NotifyCanExecuteChanged();
    }
}
