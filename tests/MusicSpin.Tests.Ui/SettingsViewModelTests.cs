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
        Assert.Equal(["Classic"], vm.Designs.Select(d => d.Name));
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

    private sealed class UnwritableSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public void Update(Func<AppSettings, AppSettings> change)
            => throw new MusicSpinException(MusicSpinErrorCode.DataDirectoryNotWritable, "the data folder is read-only.");
    }
}
