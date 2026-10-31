using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The Settings dialog's view model (#22) without a window.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _root = TempDir.Create("settingsvm");
    private readonly DataDirectory _data;
    private readonly StubAppearance _appearance = new();
    private readonly SpinClock _spinClock = new(new FakeTimeProvider(), new QueueDispatcher());
    private readonly Notices _notices = new();

    public SettingsViewModelTests() => _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);

    public void Dispose()
    {
        _spinClock.Dispose();
        TempDir.TryDelete(_root);
    }

    private SettingsViewModel Create(AppSettings? saved = null)
    {
        if (saved is not null)
        {
            new SettingsStore(_data).Save(saved);
        }

        return new SettingsViewModel(new SettingsService(_data), _appearance, _spinClock, _notices);
    }

    private AppSettings Saved() => new SettingsStore(_data).Load();

    [Fact]
    public void Without_a_settings_file_it_shows_the_defaults_and_writes_nothing()
    {
        var vm = Create();

        Assert.Equal((AppTheme.Dark, MotionMode.Smooth, 33.333), (vm.Theme, vm.Motion, vm.RecordSpeed));
        Assert.IsType<ClassicDeck>(vm.Design);
        Assert.Equal((58.0, 40.0, "58°", "40"), (vm.ShelfTilt, vm.ShelfDepth, vm.ShelfTiltText, vm.ShelfDepthText));
        Assert.Equal(["Dark", "Light"], vm.Themes.Select(c => c.Label));
        Assert.Equal(["Smooth", "Fast", "None"], vm.Motions.Select(c => c.Label));
        Assert.Equal(["33⅓", "45", "16⅔"], vm.Speeds.Select(c => c.Label));
        Assert.Equal(
            ["Classic", "Walnut & Brass", "Brushed Aluminium", "Carbon Signal", "Piano Black & Chrome", "Linear Tracking", "Smoked Acrylic"],
            vm.Designs.Select(d => d.Name));
        Assert.False(File.Exists(_data.SettingsFile));
    }

    [Fact]
    public void Saved_settings_apply_at_start()
    {
        var vm = Create(new AppSettings { Theme = "light", Motion = "fast", RecordSpeedRpm = 45, ShelfTilt = 30, ShelfDepth = 0 });

        Assert.Equal((AppTheme.Light, MotionMode.Fast, 45.0), (vm.Theme, vm.Motion, vm.RecordSpeed));
        Assert.Equal((AppTheme.Light, MotionMode.Fast), (_appearance.Theme, _appearance.Motion));
        Assert.Equal((45.0, 0.5), (_spinClock.Rpm, _spinClock.MotionMultiplier));
        Assert.Equal((30.0, 0.0), (vm.ShelfTilt, vm.ShelfDepth));
    }

    [Fact]
    public void Values_out_of_range_in_the_file_are_brought_into_range()
    {
        var vm = Create(new AppSettings { RecordSpeedRpm = 78, TurntableDesign = "gramophone", ShelfTilt = 95, ShelfDepth = -5 });

        Assert.Equal(33.333, vm.RecordSpeed);
        Assert.IsType<ClassicDeck>(vm.Design);
        Assert.Equal((80.0, 0.0), (vm.ShelfTilt, vm.ShelfDepth));
    }

    [Fact]
    public void Each_change_applies_at_once_and_is_saved()
    {
        var vm = Create(new AppSettings { Shuffle = true });

        vm.SetThemeCommand.Execute(AppTheme.Light);
        Assert.Equal((AppTheme.Light, "light"), (_appearance.Theme, Saved().Theme));

        vm.MotionChoice = vm.Motions[2];
        Assert.Equal((MotionMode.None, 0.0, "none"), (_appearance.Motion, _spinClock.MotionMultiplier, Saved().Motion));

        vm.SpeedChoice = vm.Speeds[2];
        Assert.Equal((16.667, 16.667), (_spinClock.Rpm, Saved().RecordSpeedRpm));

        vm.SetShelfTiltCommand.Execute(33.6);
        Assert.Equal(("34°", 34.0), (vm.ShelfTiltText, Saved().ShelfTilt));

        vm.SetShelfDepthCommand.Execute(140.0);
        Assert.Equal(("100", 100.0), (vm.ShelfDepthText, Saved().ShelfDepth));

        Assert.True(Saved().Shuffle); // what the CLI set stays
    }

    [Fact]
    public void A_new_instance_on_the_same_data_folder_starts_with_the_changes()
    {
        var first = Create();
        first.SetTheme(AppTheme.Light);
        first.SetMotion(MotionMode.Fast);
        first.SetRecordSpeed(45);
        first.SetShelfTilt(70);
        first.SetShelfDepth(0);

        var second = Create();

        Assert.Equal((AppTheme.Light, MotionMode.Fast, 45.0), (second.Theme, second.Motion, second.RecordSpeed));
        Assert.Equal((70.0, 0.0), (second.ShelfTilt, second.ShelfDepth));
        Assert.Equal("classic", Saved().TurntableDesign);
    }

    [Fact]
    public void A_cleared_selection_is_not_a_choice()
    {
        var vm = Create();

        vm.ThemeChoice = null!;
        vm.MotionChoice = null!;
        vm.SpeedChoice = null!;
        vm.Design = null!;

        Assert.Equal((AppTheme.Dark, MotionMode.Smooth, 33.333), (vm.Theme, vm.Motion, vm.RecordSpeed));
        Assert.IsType<ClassicDeck>(vm.Design);
        Assert.False(File.Exists(_data.SettingsFile));
    }

    [Fact]
    public void A_setting_that_cannot_be_saved_still_applies_and_says_so()
    {
        var vm = new SettingsViewModel(new UnwritableSettings(), _appearance, _spinClock, _notices);
        string? notice = null;
        _notices.Posted += (_, message) => notice = message;

        vm.SetTheme(AppTheme.Light);

        Assert.Equal(AppTheme.Light, _appearance.Theme);
        Assert.StartsWith("The setting applies now but wasn't saved", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vinyl_transition_is_on_by_default_and_a_flip_is_saved()
    {
        var vm = Create();
        Assert.True(vm.VinylTransition);
        Assert.False(File.Exists(_data.SettingsFile)); // nothing written until it changes

        vm.VinylTransition = false;
        Assert.False(Saved().VinylTransition);

        Assert.False(Create().VinylTransition); // read back as saved
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_vinyl_transition_that_cannot_be_saved_still_applies_and_says_so()
    {
        var vm = new SettingsViewModel(new UnwritableSettings(), _appearance, _spinClock, _notices);
        string? notice = null;
        _notices.Posted += (_, message) => notice = message;

        vm.VinylTransition = false;

        Assert.False(vm.VinylTransition);
        Assert.StartsWith("The setting applies now but wasn't saved", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_a_layer_above_the_queue_and_counts_its_openings()
    {
        var vm = Create();

        Assert.Equal(LayerOrder.Settings, vm.Order);
        vm.Open();
        vm.Close();
        vm.Toggle();

        Assert.True(vm.IsOpen);
        Assert.Equal(2, vm.Opens);
    }

    [Fact]
    public void A_drag_applies_every_step_and_saves_once_with_the_last_value_when_it_ends()
    {
        var settings = new CountingSettings(new SettingsService(_data));
        var vm = new SettingsViewModel(settings, _appearance, _spinClock, _notices);

        vm.TiltScrub = 30; // the bar is pressed
        for (var tilt = 30; tilt <= 60; tilt++)
        {
            vm.TiltScrub = tilt;
            vm.SetShelfTilt(tilt);
            Assert.Equal(tilt, vm.ShelfTilt); // applies at once
        }

        Assert.Equal(0, settings.Updates);
        vm.TiltScrub = null; // let go

        Assert.Equal(1, settings.Updates);
        Assert.Equal(60, Saved().ShelfTilt);
    }

    [Fact]
    public void Keys_and_clicks_are_single_steps_and_save_at_once()
    {
        var settings = new CountingSettings(new SettingsService(_data));
        var vm = new SettingsViewModel(settings, _appearance, _spinClock, _notices);

        vm.SetShelfDepth(41);
        vm.SetShelfDepth(42);

        Assert.Equal(2, settings.Updates);
        Assert.Equal(42, Saved().ShelfDepth);
    }

    [Fact]
    public void Letting_go_where_the_drag_began_writes_nothing()
    {
        var settings = new CountingSettings(new SettingsService(_data));
        var vm = new SettingsViewModel(settings, _appearance, _spinClock, _notices);

        vm.TiltScrub = vm.ShelfTilt;
        vm.SetShelfTilt(vm.ShelfTilt);
        vm.TiltScrub = null;

        Assert.Equal(0, settings.Updates);
        Assert.False(File.Exists(_data.SettingsFile));
    }

    [Fact]
    public void A_drag_saves_only_its_own_setting_so_a_change_from_the_CLI_meanwhile_stays()
    {
        var vm = Create(new AppSettings { ShelfTilt = 58, ShelfDepth = 40 });
        vm.DepthScrub = 40;
        vm.SetShelfDepth(70);

        new SettingsStore(_data).Save(Saved() with { ShelfTilt = 25 }); // music-spin changes the tilt mid-drag
        vm.DepthScrub = null;

        Assert.Equal((25.0, 70.0), (Saved().ShelfTilt, Saved().ShelfDepth));
    }

    [Fact]
    public void Closing_the_dialog_or_the_app_mid_drag_saves_the_last_value_once()
    {
        var settings = new CountingSettings(new SettingsService(_data));
        var vm = new SettingsViewModel(settings, _appearance, _spinClock, _notices);
        vm.Open();
        vm.TiltScrub = 70;
        vm.SetShelfTilt(70);

        vm.Close();
        Assert.Equal((1, 70.0), (settings.Updates, Saved().ShelfTilt));

        vm.TiltScrub = null; // the bar lets go afterwards: nothing more to save
        vm.SavePending(); // nor at shutdown
        Assert.Equal(1, settings.Updates);
    }

    [Fact]
    public void A_drag_whose_save_fails_says_so_once_and_still_applies()
    {
        var vm = new SettingsViewModel(new UnwritableSettings(), _appearance, _spinClock, _notices);
        var notices = new List<string>();
        _notices.Posted += (_, message) => notices.Add(message);

        vm.DepthScrub = 10;
        for (var depth = 10; depth <= 30; depth++)
        {
            vm.DepthScrub = depth;
            vm.SetShelfDepth(depth);
        }

        Assert.Empty(notices);
        vm.DepthScrub = null;

        Assert.Equal(30, vm.ShelfDepth);
        var notice = Assert.Single(notices);
        Assert.StartsWith("The setting applies now but wasn't saved", notice, StringComparison.Ordinal);
        vm.SavePending(); // the failed value isn't retried at shutdown either: it was reported
        Assert.Single(notices);
    }

    // ---- pages (#188) ------------------------------------------------------------------------------------------

    [Fact]
    public void Without_the_library_and_audio_the_pages_are_the_rest_and_the_first_is_chosen()
    {
        var vm = Create();

        Assert.Equal(["Appearance", "Turntable", "Shelf"], vm.Pages.Select(p => p.Title));
        Assert.Equal(["Appearance", "Turntable", "Shelf"], vm.Pages.Select(p => p.Icon));
        Assert.Same(vm.Pages[0], vm.SelectedPage);
        Assert.All(vm.Pages, p => Assert.Same(vm, Assert.IsAssignableFrom<SettingsPage>(p) switch
        {
            AppearancePage a => a.Settings,
            TurntablePage t => t.Settings,
            ShelfPage s => s.Settings,
            _ => null,
        }));
    }

    [Fact]
    public void The_page_chosen_stays_across_closing_and_opening_and_null_is_ignored()
    {
        var vm = Create();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.SelectedPage = vm.Pages[2];
        vm.Open();
        vm.Close();
        vm.Open();
        Assert.Same(vm.Pages[2], vm.SelectedPage);

        vm.SelectedPage = null!; // the sidebar's list box letting go as it leaves
        Assert.Same(vm.Pages[2], vm.SelectedPage);
        Assert.Single(changes, nameof(SettingsViewModel.SelectedPage));
        Assert.False(File.Exists(_data.SettingsFile)); // not saved
    }

    [Fact]
    public async Task A_page_that_fails_to_read_keeps_why_until_a_read_works_and_posts_no_notice()
    {
        var notices = new List<string>();
        _notices.Posted += (_, message) => notices.Add(message);
        var page = new ReadingPage(_notices) { Failure = new MusicSpinException(MusicSpinErrorCode.LibraryCorrupt, "The library database is corrupt.") };

        await page.LoadAsync();
        Assert.Equal(("The library database is corrupt.", true), (page.Problem, page.HasProblem));
        Assert.Equal("Reading couldn't load", page.ProblemTitle);

        await page.LoadCommand.ExecuteAsync(null); // Try again, still failing
        Assert.Equal((2, true), (page.Reads, page.HasProblem));

        page.Failure = null;
        await page.LoadCommand.ExecuteAsync(null);
        Assert.Equal((3, null, false), (page.Reads, page.Problem, page.HasProblem));
        Assert.Empty(notices);
    }

    [Fact]
    public async Task A_page_that_fails_unexpectedly_says_so_and_a_cancelled_read_reaches_the_caller()
    {
        var page = new ReadingPage(_notices) { Failure = new InvalidOperationException("No reader.") };
        await page.LoadAsync();
        Assert.Equal("Something went wrong: No reader.", page.Problem);

        page.Failure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(page.LoadAsync);
    }

    [Fact]
    public async Task A_page_with_nothing_to_read_never_has_a_problem()
    {
        var vm = Create();
        foreach (var page in vm.Pages)
        {
            await page.LoadAsync();
        }

        Assert.All(vm.Pages, p => Assert.False(p.HasProblem));
    }

    /// <summary>A page whose read fails with <see cref="Failure"/> while it is set.</summary>
    private sealed class ReadingPage(Notices notices) : SettingsPage("Reading", "Folder", notices)
    {
        public Exception? Failure { get; set; }

        public int Reads { get; private set; }

        protected override Task ReadAsync()
        {
            Reads++;
            return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    /// <summary>Counts the saves (each one re-reads and rewrites settings.json).</summary>
    private sealed class CountingSettings(ISettingsService inner) : ISettingsService
    {
        public int Updates { get; private set; }

        public AppSettings Current => inner.Current;

        public void Update(Func<AppSettings, AppSettings> change)
        {
            Updates++;
            inner.Update(change);
        }
    }

    private sealed class UnwritableSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public void Update(Func<AppSettings, AppSettings> change)
            => throw new MusicSpinException(MusicSpinErrorCode.DataDirectoryNotWritable, "the data folder is read-only.");
    }
}
