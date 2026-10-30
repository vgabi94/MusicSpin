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
