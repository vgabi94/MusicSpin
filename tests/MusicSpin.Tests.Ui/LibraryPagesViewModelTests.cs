using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Composition;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Sorting, grouping, search and the menus of the Albums (#9) and Tracks (#12) pages, without a window.</summary>
public sealed class LibraryPagesViewModelTests : IAsyncDisposable
{
    private readonly string _root = TempDir.Create("pagesvm");
    private readonly DataDirectory _data;
    private readonly ScriptedPlayer _player;
    private readonly QueueDispatcher _ui = new();
    private MainWindowViewModel? _vm;

    public LibraryPagesViewModelTests()
    {
        _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        _player = new ScriptedPlayer(_data);
    }

    private async Task<MainWindowViewModel> CreateAsync()
    {
        new SettingsStore(_data).Save(new AppSettings());
        _vm = new MainWindowViewModel(new AppServices(new SettingsService(_data), SampleLibrary.Create(), new StubCovers(), _player,
            new StubAppearance(), new FakeTimeProvider(), _ui, null));
        await _vm.InitializeAsync();
        await _ui.UntilAsync(() => _vm.Player.IsConnected && _vm.AlbumsPage.Albums.Count == 10, "the albums");
        return _vm;
    }

    private async Task<TracksPageViewModel> TracksAsync()
    {
        var vm = await CreateAsync();
        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Tracks);
        await _ui.UntilAsync(() => vm.TracksPage.Tracks.Count == 30, "the tracks");
        return vm.TracksPage;
    }

    private static string[] Titles(AlbumsPageViewModel page) => [.. page.Albums.Select(a => a.Title)];

    [Fact]
    public async Task Albums_sort_by_artist_then_year_by_title_or_by_year_with_unknown_years_last()
    {
        var page = (await CreateAsync()).AlbumsPage;
        page.Grouping = AlbumGrouping.None;

        Assert.Equal(LibrarySort.Artist, page.Sort);
        Assert.Equal(["Velvet Hours", "Copper Sun", "Cello Suites", "Partitas", "Northern Lines", "Glass Garden", "Night Transit", "Blue Hour",
            "Untitled Tape", "Field Notes"], Titles(page));

        page.Sort = LibrarySort.Title;
        Assert.Equal(["Blue Hour", "Cello Suites", "Copper Sun", "Field Notes", "Glass Garden", "Night Transit", "Northern Lines", "Partitas",
            "Untitled Tape", "Velvet Hours"], Titles(page));

        page.Sort = LibrarySort.Year;
        Assert.Equal(["Blue Hour", "Copper Sun", "Velvet Hours", "Field Notes", "Cello Suites", "Night Transit", "Glass Garden", "Partitas",
            "Northern Lines", "Untitled Tape"], Titles(page));
    }

    [Fact]
    public async Task Albums_group_by_genre_A_to_Z_with_untagged_albums_last_and_keep_the_sort_inside_groups()
    {
        var page = (await CreateAsync()).AlbumsPage;
        page.Columns = 5;

        Assert.Equal(AlbumGrouping.Genre, page.Grouping);
        Assert.Equal(["Ambient", "Classical", "Electronic", "Jazz", LibraryOrder.UnknownGenre], page.GroupNames);
        var headers = page.Rows.OfType<GroupHeaderViewModel>().ToList();
        Assert.Equal(["Ambient · 2", "Classical · 3", "Electronic · 1", "Jazz · 3", "Unknown genre · 1"], headers.Select(h => h.Text));
        Assert.Equal([true, false, false, false, false], headers.Select(h => h.IsFirst));
        var jazz = page.Rows.SkipWhile(r => r is not GroupHeaderViewModel { Key: "Jazz" }).Skip(1).First();
        Assert.Equal(["Velvet Hours", "Copper Sun", "Blue Hour"], ((AlbumCardRowViewModel)jazz).Albums.Select(a => a.Title));

        page.Grouping = AlbumGrouping.Artist;
        Assert.Equal(["Ana Cruz Quintet", "Delta Ruth", "Elena Varga", "Halvard Eik", "Ilse Morrow", "Kodo Arai", "Miles Ahead", "Nobody", "Tomas Lind"],
            page.GroupNames);
        Assert.Equal("Elena Varga · 2", page.Rows.OfType<GroupHeaderViewModel>().ElementAt(2).Text);
    }

    [Fact]
    public async Task Album_rows_hold_as_many_cards_as_the_view_has_columns()
    {
        var page = (await CreateAsync()).AlbumsPage;
        page.Grouping = AlbumGrouping.None;

        page.Columns = 4;
        Assert.Equal([4, 4, 2], page.Rows.Cast<AlbumCardRowViewModel>().Select(r => r.Albums.Count));
        Assert.Equal([true, false, false], page.Rows.Cast<AlbumCardRowViewModel>().Select(r => r.IsFirst));

        page.Columns = 3;
        Assert.Equal([3, 3, 3, 1], page.Rows.Cast<AlbumCardRowViewModel>().Select(r => r.Albums.Count));
        Assert.All(page.Rows.Cast<AlbumCardRowViewModel>(), r => Assert.Equal(3, r.Columns));
    }

    [Theory]
    [InlineData("partitas", new[] { "Partitas" })]
    [InlineData("elena", new[] { "Cello Suites", "Partitas" })]
    [InlineData("JAZZ", new[] { "Velvet Hours", "Copper Sun", "Blue Hour" })]
    [InlineData("1973", new[] { "Copper Sun" })]
    [InlineData("varga 2023", new[] { "Partitas" })]
    public async Task The_search_finds_albums_by_title_artist_genre_and_year(string search, string[] expected)
    {
        var page = (await CreateAsync()).AlbumsPage;
        page.Grouping = AlbumGrouping.None;

        page.SearchText = search;

        Assert.Equal(expected, Titles(page));
        Assert.Equal(expected.Length.ToString(System.Globalization.CultureInfo.CurrentCulture), page.CountText);
        Assert.Null(page.EmptyText);
    }

    [Fact]
    public async Task A_search_that_finds_nothing_says_so()
    {
        var page = (await CreateAsync()).AlbumsPage;

        page.SearchText = "  zither ";

        Assert.Empty(page.Albums);
        Assert.Empty(page.Rows);
        Assert.Equal("Nothing matches “zither”.", page.EmptyText);
    }

    [Fact]
    public async Task The_Sort_and_Group_menus_mark_the_current_choice_and_apply_the_one_chosen()
    {
        var vm = await CreateAsync();
        var page = vm.AlbumsPage;

        page.OpenSortMenuCommand.Execute(null);
        Assert.True(vm.Menu.IsOpen);
        Assert.Equal(["Artist", "Title", "Year"], vm.Menu.Entries.Select(e => e.Label));
        Assert.Equal([true, false, false], vm.Menu.Entries.Select(e => e.IsChosen));
        await vm.Menu.ChooseCommand.ExecuteAsync(vm.Menu.Entries[2]);
        Assert.Equal(LibrarySort.Year, page.Sort);
        Assert.False(vm.Menu.IsOpen);

        page.OpenGroupMenuCommand.Execute(null);
        Assert.Equal(["None", "Artist", "Genre"], vm.Menu.Entries.Select(e => e.Label));
        Assert.Equal([false, false, true], vm.Menu.Entries.Select(e => e.IsChosen));
        await vm.Menu.ChooseCommand.ExecuteAsync(vm.Menu.Entries[0]);
        Assert.Equal(AlbumGrouping.None, page.Grouping);
        Assert.Empty(page.GroupNames);

        page.OpenSortMenuCommand.Execute(null);
        vm.EscapeCommand.Execute(null);
        Assert.False(vm.Menu.IsOpen);
    }

    [Fact]
    public async Task Tracks_group_by_album_in_album_order_with_a_header_per_album()
    {
        var page = await TracksAsync();

        Assert.Equal(TrackGrouping.Album, page.Grouping);
        Assert.Equal(["Velvet Hours", "Copper Sun", "Cello Suites", "Partitas", "Northern Lines", "Glass Garden", "Night Transit", "Blue Hour",
            "Untitled Tape", "Field Notes"], page.GroupNames);
        var header = Assert.IsType<TrackAlbumHeaderViewModel>(page.Rows[0]);
        Assert.Equal("Ana Cruz Quintet · 2017 · FLAC 24/96", header.Album.Details);
        Assert.Equal(["The Hollow", "Glass Window", "Salt Bloom"], page.Rows.Skip(1).Take(3).Cast<NumberedTrackRow>().Select(r => r.Item.Title));
        Assert.Equal(["1", "2", "3"], page.Rows.Skip(1).Take(3).Cast<NumberedTrackRow>().Select(r => r.Item.Number));
        Assert.Equal(10, page.Rows.OfType<TrackAlbumHeaderViewModel>().Count());
        Assert.Equal(30, page.Rows.OfType<NumberedTrackRow>().Count());
    }

    [Fact]
    public async Task Tracks_group_by_genre_or_not_at_all_with_the_columns_that_go_with_it()
    {
        var page = await TracksAsync();

        page.Grouping = TrackGrouping.Genre;
        Assert.Equal(["Ambient", "Classical", "Electronic", "Jazz", LibraryOrder.UnknownGenre], page.GroupNames);
        Assert.Equal(["Ambient · 6 tracks", "Classical · 9 tracks", "Electronic · 3 tracks", "Jazz · 9 tracks", "Unknown genre · 3 tracks"],
            page.Rows.OfType<GroupHeaderViewModel>().Select(h => h.Text));
        Assert.Equal(30, page.Rows.OfType<TrackRowWithAlbum>().Count());

        page.Grouping = TrackGrouping.None;
        Assert.Empty(page.GroupNames);
        Assert.Equal(30, page.Rows.OfType<TrackRowWithAlbumAndGenre>().Count());
        Assert.Equal(30, page.Rows.Count);
    }

    [Fact]
    public async Task Title_sorts_the_tracks_themselves_unless_they_are_grouped_by_album()
    {
        var page = await TracksAsync();

        page.Sort = LibrarySort.Title;
        Assert.Equal(["Blue Hour", "Cello Suites", "Copper Sun"], page.GroupNames.Take(3));
        Assert.Equal(["So Blue", "Dusk", "Bell"], page.Tracks.Take(3).Select(t => t.Title));

        page.Grouping = TrackGrouping.None;
        Assert.Equal(["Allemande", "Aurora", "Bell", "Birch"], page.Tracks.Take(4).Select(t => t.Title));

        page.Grouping = TrackGrouping.Genre;
        var ambient = page.Rows.Skip(1).TakeWhile(r => r is TrackRow).Cast<TrackRow>().Select(r => r.Item.Title);
        Assert.Equal(["Aurora", "Birch", "Low Sun", "Meadow", "Polar Drift", "Snowfield"], ambient);

        page.Sort = LibrarySort.Year;
        page.Grouping = TrackGrouping.None;
        Assert.Equal(["So Blue", "Dusk", "Bell", "Lantern Paper"], page.Tracks.Take(4).Select(t => t.Title));
        Assert.Equal(["Side A", "Side B", "Hiss"], page.Tracks.TakeLast(3).Select(t => t.Title));
    }

    [Theory]
    [InlineData("salt", 1)]
    [InlineData("copper sun", 3)]
    [InlineData("varga", 6)]
    [InlineData("electronic", 3)]
    [InlineData("neon", 1)]
    public async Task The_search_finds_tracks_by_title_album_artist_and_genre(string search, int count)
    {
        var page = await TracksAsync();

        page.SearchText = search;

        Assert.Equal(count, page.Tracks.Count);
        Assert.Equal(count, page.Rows.OfType<TrackRow>().Count());
    }

    [Fact]
    public async Task Plus_offers_Play_next_and_Add_to_queue()
    {
        var vm = await CreateAsync();
        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Tracks);
        await _ui.UntilAsync(() => vm.TracksPage.Tracks.Count == 30, "the tracks");
        var track = vm.TracksPage.Tracks[4];

        vm.TracksPage.OpenTrackMenuCommand.Execute(track);
        Assert.Equal(["Play next", "Add to queue", "Add to playlist"], vm.Menu.Entries.Select(e => e.Label));
        Assert.All(vm.Menu.Entries, e => Assert.False(e.IsChosen));
        Assert.Equal([false, false, true], vm.Menu.Entries.Select(e => e.HasSubmenu));
        await vm.Menu.ChooseCommand.ExecuteAsync(vm.Menu.Entries[0]);
        vm.TracksPage.OpenTrackMenuCommand.Execute(track);
        await vm.Menu.ChooseCommand.ExecuteAsync(vm.Menu.Entries[1]);

        var sent = _player.Sent("queue.add");
        Assert.Equal(["next", "upnext"], sent.Select(s => s.GetProperty("position").GetString()));
        Assert.All(sent, s => Assert.Equal([track.Track.Path], Paths(s)));
    }

    [Fact]
    public async Task The_album_header_plays_or_queues_the_whole_album_whatever_the_search_shows()
    {
        var vm = await CreateAsync();
        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Tracks);
        await _ui.UntilAsync(() => vm.TracksPage.Tracks.Count == 30, "the tracks");
        vm.SearchText = "salt";
        var header = Assert.IsType<TrackAlbumHeaderViewModel>(vm.TracksPage.Rows[0]);
        var album = SampleLibrary.Create().Tracks.Where(t => t.AlbumId == SampleLibrary.AlbumId(7)).Select(t => t.Path).ToList();

        await vm.TracksPage.PlayAlbumCommand.ExecuteAsync(header);
        vm.TracksPage.OpenAlbumMenuCommand.Execute(header);
        await vm.Menu.ChooseCommand.ExecuteAsync(vm.Menu.Entries[1]);

        Assert.Equal(album, Paths(Assert.Single(_player.Sent("queue.set"))));
        Assert.Equal(album, Paths(Assert.Single(_player.Sent("queue.add"))));
    }

    [Fact]
    public async Task Playing_a_track_plays_the_list_as_shown_from_it()
    {
        var vm = await CreateAsync();
        vm.Sidebar.NavigateCommand.Execute(vm.Sidebar.Tracks);
        await _ui.UntilAsync(() => vm.TracksPage.Tracks.Count == 30, "the tracks");
        vm.TracksPage.Sort = LibrarySort.Title;
        vm.TracksPage.Grouping = TrackGrouping.None;

        await vm.TracksPage.PlayTrackCommand.ExecuteAsync(vm.TracksPage.Tracks[2]);

        var sent = Assert.Single(_player.Sent("queue.play"));
        Assert.Equal(2, sent.GetProperty("start").GetInt32());
        Assert.Equal(vm.TracksPage.Tracks.Select(t => t.Track.Path), Paths(sent));
    }

    private static List<string> Paths(JsonElement sent) => [.. sent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()!)];

    public async ValueTask DisposeAsync()
    {
        if (_vm is not null)
        {
            await _vm.DisposeAsync();
        }

        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }
}
