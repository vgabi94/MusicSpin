using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Settings › Library and the empty library (#31): music folders, scanning, the folder watch.</summary>
public sealed class LibrarySettingsTests
{
    private static Task<AppHarness> OpenAsync(Action<string>? music = null, AppTheme theme = AppTheme.Dark, TimeSpan? lockTimeout = null)
        => AppHarness.OpenAsync(new AppOptions { Scripted = true, Music = music, Theme = theme, LockTimeout = lockTimeout });

    private static void TwoAlbums(string music)
    {
        TestImages.Album(music, "Kind of Blue", "Miles Davis", tracks: 3, year: 1959);
        TestImages.Album(music, "Blue Train", "John Coltrane", tracks: 2, year: 1957);
    }

    private static LibrarySettingsViewModel Library(AppHarness app) => app.ViewModel.Library;

    private static async Task OpenSettingsAsync(AppHarness app)
    {
        app.ShowSettingsPage("Library");
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations();
        await Gui.UntilAsync(() => Library(app).Folders.Count == app.Read(l => l.Folders()).Count, "the folders read");
    }

    private static Button Find(AppHarness app, string name) => Gui.Find<Button>(app.Window, name);

    private static void Click(AppHarness app, Control control)
    {
        control.BringIntoView();
        Gui.Settle();
        Gui.Click(app.Window, control);
    }

    /// <summary>Chooses <paramref name="label"/> in the open menu.</summary>
    private static async Task ChooseAsync(AppHarness app, string label)
    {
        var entry = app.ViewModel.Menu.Entries.Single(e => e.Label == label);
        await app.ViewModel.Menu.ChooseCommand.ExecuteAsync(entry);
        Gui.Settle();
    }

    /// <summary>
    /// The quiet time passes until no change waits: the watch reports, and the scan it asks for (posted to the UI thread)
    /// runs. A late event of the same write re-arms the wait, so the time passes again then.
    /// </summary>
    private static async Task LetTheWatchReportAsync(AppHarness app)
    {
        await Gui.UntilAsync(() =>
        {
            if (Library(app).WatchHasPending)
            {
                app.Time.Advance(LibrarySettingsViewModel.WatchDebounce);
            }

            return !Library(app).WatchHasPending;
        }, "the watch reporting");
        Gui.Settle();
    }

    // ---- the empty library -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task An_empty_library_offers_Add_a_music_folder_which_scans_it_until_its_albums_appear()
    {
        await using var app = await OpenAsync();
        Assert.True(app.ViewModel.AlbumsPage.IsLibraryEmpty);
        Assert.Equal(PageViewModel.NoMusic, Gui.Find<TextBlock>(app.Window, "EmptyText").Text);
        var add = Find(app, "AddMusicButton");
        Assert.True(add.IsEffectivelyVisible);

        TwoAlbums(app.Music);
        app.Options.FolderPicker.Answer = app.Music;
        Click(app, add);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the albums after the scan");

        Assert.Equal(1, app.Options.FolderPicker.Opened);
        Assert.False(app.ViewModel.AlbumsPage.IsLibraryEmpty);
        Assert.False(Gui.Find<StackPanel>(app.Window, "AddMusic").IsEffectivelyVisible);
        Assert.Equal(["Blue Train", "Kind of Blue"], app.ViewModel.AlbumsPage.Albums.Select(a => a.Title).Order(StringComparer.Ordinal));
        Assert.Equal([app.Music], app.Read(l => l.Folders()).Select(f => f.Path));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Cancelling_the_picker_adds_nothing()
    {
        await using var app = await OpenAsync();
        app.Options.FolderPicker.Answer = null;

        Click(app, Find(app, "AddMusicButton"));

        Assert.Equal(1, app.Options.FolderPicker.Opened);
        Assert.Empty(app.Read(l => l.Folders()));
        Assert.Null(Library(app).Outcome);
        Assert.True(app.ViewModel.AlbumsPage.IsLibraryEmpty);
    }

    // ---- folders -----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_folders_show_their_tracks_a_missing_one_says_so_and_the_last_scan_shows()
    {
        await using var app = await OpenAsync(TwoAlbums);
        var gone = Directory.CreateDirectory(Path.Combine(app.Root, "unplugged")).FullName;
        app.Edit(l => l.AddFolder(gone));
        Directory.Delete(gone);
        await OpenSettingsAsync(app);

        var folders = Library(app).Folders;
        Assert.Equal(new[] { app.Music, gone }.Order(StringComparer.Ordinal), folders.Select(f => f.Path));
        var music = folders.Single(f => f.Path == app.Music);
        Assert.True(music.IsReachable);
        Assert.Equal("5 tracks", music.Detail);
        var missing = folders.Single(f => f.Path == gone);
        Assert.False(missing.IsReachable);
        Assert.Equal(LibraryFolderViewModel.NotFound, missing.Detail);
        var rows = Gui.Find<ItemsControl>(app.Window, "LibraryFolders").GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("missing")).ToList();
        Assert.Single(rows, t => t.IsEffectivelyVisible);

        var last = app.Read(l => l.Folders()).Single(f => f.Path == app.Music).LastScan!.Value;
        // The app's clock is on UTC in the tests.
        Assert.Equal("Last scanned " + last.ToUniversalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture), Library(app).LastScanText);
        Assert.Equal(Library(app).LastScanText, Gui.Find<TextBlock>(app.Window, "ScanNote").Text);
    }

    [AvaloniaFact]
    public async Task Adding_a_folder_inside_a_library_folder_says_it_is_covered_and_a_parent_takes_in_its_children()
    {
        await using var app = await OpenAsync(TwoAlbums);
        await OpenSettingsAsync(app);
        var inner = Path.Combine(app.Music, "Miles Davis");

        app.Options.FolderPicker.Answer = inner;
        Click(app, Find(app, "AddFolder"));
        await Gui.UntilAsync(() => Library(app).Outcome is not null && !Library(app).AddFolderCommand.IsRunning, "the outcome");
        Assert.Equal($"{inner} is already covered: it's inside {app.Music}, a library folder.", Library(app).Outcome);
        Assert.Equal(Library(app).Outcome, Gui.Find<TextBlock>(app.Window, "AddOutcome").Text);

        app.Options.FolderPicker.Answer = app.Root;
        Click(app, Find(app, "AddFolder"));
        // Adding scans the new folder, and Add waits for the scan.
        await Gui.UntilAsync(() => Library(app).Outcome!.StartsWith("Added", StringComparison.Ordinal) && !Library(app).AddFolderCommand.IsRunning,
            "the parent added and scanned");
        Assert.Equal($"Added {app.Root}. It takes in {app.Music}, which was a library folder inside it.", Library(app).Outcome);
        Assert.Equal([app.Root], Library(app).Folders.Select(f => f.Path));

        app.Options.FolderPicker.Answer = app.Root;
        Click(app, Find(app, "AddFolder"));
        await Gui.UntilAsync(() => Library(app).Outcome == $"{app.Root} is already a library folder.", "already there");
    }

    [AvaloniaFact]
    public async Task Remove_asks_first_then_the_folders_albums_leave()
    {
        await using var app = await OpenAsync(TwoAlbums);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the albums");
        await OpenSettingsAsync(app);
        var remove = Gui.Find<ItemsControl>(app.Window, "LibraryFolders").GetVisualDescendants().OfType<Button>().Single();

        Click(app, remove);
        Assert.True(app.ViewModel.Menu.IsOpen);
        Assert.Equal($"Remove “{app.Music}” from the library? Its tracks leave the library; the files stay on disk.", app.ViewModel.Menu.Caption);
        Assert.Single(app.Read(l => l.Folders())); // nothing yet

        await ChooseAsync(app, "Cancel");
        Assert.Single(app.Read(l => l.Folders()));

        Click(app, remove);
        await ChooseAsync(app, "Remove");
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 0, "the albums gone");

        Assert.Empty(app.Read(l => l.Folders()));
        Assert.Empty(Library(app).Folders);
        Assert.True(File.Exists(app.TrackPaths[0])); // the files stay
        Assert.True(app.ViewModel.AlbumsPage.IsLibraryEmpty);
    }

    // ---- scanning ----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Rescan_shows_its_progress_and_Cancel_stops_it()
    {
        var library = SampleLibrary.Create();
        library.Folders.Add(new FolderInfo("/music", true, 30, DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero)));
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers() });
        await OpenSettingsAsync2(app);
        var hold = library.ScanHold = new TaskCompletionSource();
        library.ScanReports = [new ScanProgress("walk", 40, 0, 0, 0, null), new ScanProgress("read", 40, 12, 5, 2, "/music/x.flac")];

        Click(app, Find(app, "Rescan"));
        await Gui.UntilAsync(() => app.ViewModel.LibraryScan.Progress?.Phase == "read", "the progress");

        Assert.Equal("Reading 5 of 12 files · 2 couldn't be read", Gui.Find<TextBlock>(app.Window, "ScanNote").Text);
        Assert.True(Find(app, "CancelScan").IsEffectivelyVisible);
        Assert.False(Find(app, "Rescan").IsEffectivelyVisible);
        Assert.False(Find(app, "AddFolder").IsEffectivelyEnabled); // the scan holds the library

        Click(app, Find(app, "CancelScan"));
        await Gui.UntilAsync(() => !app.ViewModel.LibraryScan.IsScanning, "the scan stopped");

        Assert.False(hold.Task.IsCompleted); // it didn't finish: it was cancelled
        Assert.Equal("Last scanned 1 Oct 2026, 09:30", Gui.Find<TextBlock>(app.Window, "ScanNote").Text);
        Assert.True(Find(app, "Rescan").IsEffectivelyVisible);
        Assert.True(Find(app, "AddFolder").IsEffectivelyEnabled);
        Assert.Null(app.ViewModel.Banner);
    }

    [AvaloniaFact]
    public async Task A_scan_asked_for_while_one_runs_runs_once_after_it()
    {
        var library = SampleLibrary.Create();
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers() });
        var hold = library.ScanHold = new TaskCompletionSource();

        var first = app.ViewModel.LibraryScan.ScanAsync();
        _ = app.ViewModel.LibraryScan.ScanAsync();
        _ = app.ViewModel.LibraryScan.ScanAsync();
        Assert.Equal(1, library.Scans);

        library.ScanHold = null;
        hold.SetResult();
        await first;

        Assert.Equal(2, library.Scans);
        Assert.False(app.ViewModel.LibraryScan.IsScanning);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task While_the_CLI_holds_the_library_adding_and_scanning_say_it_is_busy()
    {
        await using var app = await OpenAsync(TwoAlbums, lockTimeout: TimeSpan.FromMilliseconds(200));
        await OpenSettingsAsync(app);

        using (app.OpenLibrary()) // the CLI, mid-scan
        {
            Click(app, Find(app, "Rescan"));
            await Gui.UntilAsync(() => app.ViewModel.Banner == LibraryService.BusyMessage, "the busy notice on Rescan");

            app.ViewModel.DismissBannerCommand.Execute(null);
            app.Options.FolderPicker.Answer = app.Root;
            Click(app, Find(app, "AddFolder"));
            await Gui.UntilAsync(() => app.ViewModel.Banner == LibraryService.BusyMessage, "the busy notice on Add");
        }

        Assert.Equal([app.Music], app.Read(l => l.Folders()).Select(f => f.Path)); // nothing added
        Assert.False(app.ViewModel.LibraryScan.IsScanning);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Files_the_scan_couldnt_read_are_counted_and_listed()
    {
        await using var app = await OpenAsync(music =>
        {
            TwoAlbums(music);
            var broken = Directory.CreateDirectory(Path.Combine(music, "Broken")).FullName;
            File.WriteAllText(Path.Combine(broken, "01.flac"), "not audio");
        });
        await OpenSettingsAsync(app);
        var issues = app.Read(l => l.Issues());
        Assert.NotEmpty(issues);

        Assert.True(Gui.Find<StackPanel>(app.Window, "ScanIssues").IsEffectivelyVisible);
        var link = Find(app, "IssuesLink");
        Assert.Equal($"{issues.Count} {(issues.Count == 1 ? "file" : "files")} couldn't be read", link.Content);
        Assert.False(Gui.Find<ItemsControl>(app.Window, "IssueList").IsEffectivelyVisible);

        Click(app, link);
        Assert.True(Gui.Find<ItemsControl>(app.Window, "IssueList").IsEffectivelyVisible);
        Assert.Equal(issues.Select(i => i.Path), Library(app).Issues.Select(i => i.Path));
        Assert.Contains(Library(app).Issues, i => i.Path.EndsWith(Path.Combine("Broken", "01.flac"), StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task No_issues_no_link()
    {
        await using var app = await OpenAsync(TwoAlbums);
        await OpenSettingsAsync(app);

        Assert.False(Library(app).HasIssues);
        Assert.False(Gui.Find<StackPanel>(app.Window, "ScanIssues").IsEffectivelyVisible);
    }

    // ---- the watch ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_Watch_folders_switch_is_saved_and_watches_the_folders()
    {
        await using var app = await OpenAsync(TwoAlbums);
        await OpenSettingsAsync(app);
        var watch = Gui.Find<ToggleButton>(app.Window, "WatchFolders");
        Assert.False(watch.IsChecked);
        Assert.Equal(0, Library(app).WatchedFolders);

        Click(app, watch);

        Assert.True(Library(app).WatchFolders);
        Assert.True(new SettingsStore(app.Data).Load().WatchLibrary);
        Assert.Equal(1, Library(app).WatchedFolders);

        Click(app, watch);
        Assert.False(new SettingsStore(app.Data).Load().WatchLibrary);
        Assert.Equal(0, Library(app).WatchedFolders);
    }

    [AvaloniaFact]
    public async Task With_the_watch_on_an_album_added_to_a_folder_is_scanned_in_once_the_folder_goes_quiet()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Music = TwoAlbums, Settings = new AppSettings { WatchLibrary = true } });
        await Gui.UntilAsync(() => Library(app).WatchedFolders == 1, "the watch");
        Assert.Equal(2, app.ViewModel.AlbumsPage.Albums.Count);

        TestImages.Album(app.Music, "Giant Steps", "John Coltrane", tracks: 2, year: 1960);
        await Gui.UntilAsync(() => Library(app).WatchHasPending, "the watch seeing the new files");
        Assert.False(app.ViewModel.LibraryScan.IsScanning); // nothing until the folder is quiet

        await LetTheWatchReportAsync(app);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 3, "the new album after the scan");

        Assert.Contains("Giant Steps", app.ViewModel.AlbumsPage.Albums.Select(a => a.Title));
    }

    [AvaloniaFact]
    public async Task Changes_seen_while_a_scan_runs_make_one_more_scan_after_it_not_one_each()
    {
        var watched = Directory.CreateDirectory(Path.Combine(TempDir.Create("watch"), "Music")).FullName;
        var library = SampleLibrary.Create();
        library.Folders.Add(new FolderInfo(watched, true, 0, DateTimeOffset.UnixEpoch, null));
        await using var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = library, Covers = SampleLibrary.Covers(), Settings = new AppSettings { WatchLibrary = true },
        });
        await Gui.UntilAsync(() => Library(app).WatchedFolders == 1, "the watch");
        var hold = library.ScanHold = new TaskCompletionSource();
        var first = app.ViewModel.LibraryScan.ScanAsync();
        Assert.Equal(1, library.Scans);

        foreach (var name in new[] { "a.flac", "b.flac" })
        {
            await File.WriteAllBytesAsync(Path.Combine(watched, name), [1, 2, 3]);
            await Gui.UntilAsync(() => Library(app).WatchHasPending, $"the watch seeing {name}");
            await LetTheWatchReportAsync(app);
        }

        Assert.Equal(1, library.Scans); // both wait for the running scan
        library.ScanHold = null;
        hold.SetResult();
        await first;
        await Gui.UntilAsync(() => !app.ViewModel.LibraryScan.IsScanning, "the scans done");

        Assert.Equal(2, library.Scans);
    }

    [AvaloniaFact]
    public async Task With_Watch_folders_saved_the_app_watches_from_the_start()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Music = TwoAlbums, Settings = new AppSettings { WatchLibrary = true } });

        await Gui.UntilAsync(() => Library(app).WatchedFolders == 1, "the watch");
        Assert.True(Library(app).WatchFolders);
    }

    // ---- snapshots ---------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_Settings_Library(AppTheme theme)
    {
        var library = SampleLibrary.Create();
        library.Folders.Add(new FolderInfo("/home/ana/Music", true, 1204, DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero)));
        library.Folders.Add(new FolderInfo("/media/usb/Concerts", false, 88, DateTimeOffset.UnixEpoch, null));
        library.Issues.Add(new ScanIssue("/home/ana/Music/Broken/01.flac", "unreadable", "Not an audio file."));
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = SampleLibrary.Covers(), Theme = theme });
        await OpenSettingsAsync2(app);

        Snapshots.Match(app.Window, Gui.Find<Border>(app.Window, "Dialog"), $"settings-library-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_the_empty_library(AppTheme theme)
    {
        await using var app = await OpenAsync(theme: theme);

        Snapshots.Match(app.Window, $"empty-library-{theme.ToString().ToLowerInvariant()}");
    }

    /// <summary>Opens Settings over a stub library and waits for its folders.</summary>
    private static async Task OpenSettingsAsync2(AppHarness app)
    {
        var library = (StubLibrary)app.Services.Library;
        app.ShowSettingsPage("Library");
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations();
        await Gui.UntilAsync(() => Library(app).Folders.Count == library.Folders.Count && Library(app).Issues.Count == library.Issues.Count, "the folders read");
    }
}
