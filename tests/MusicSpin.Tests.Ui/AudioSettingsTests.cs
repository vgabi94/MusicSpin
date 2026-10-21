using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Settings › Audio (#32): the output device, exclusive mode, and the player restarting to take them.</summary>
public sealed class AudioSettingsTests
{
    private static readonly string[] TwoDevices = ["Speakers (Realtek)", "USB DAC"];

    private static Task<AppHarness> OpenAsync(string[]? devices = null, AppSettings? settings = null, AppTheme theme = AppTheme.Dark)
        => AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Settings = settings ?? new AppSettings(),
            Theme = theme,
            AudioDevices = new StubAudioDevices { Names = devices ?? TwoDevices },
        });

    private static AudioSettingsViewModel Audio(AppHarness app) => app.ViewModel.Audio;

    private static async Task OpenSettingsAsync(AppHarness app)
    {
        var lists = app.Options.AudioDevices.Lists;
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations();
        await Gui.UntilAsync(() => app.Options.AudioDevices.Lists > lists, "the devices listed");
        Gui.All<ScrollViewer>(Gui.Find<Border>(app.Window, "Dialog")).First().ScrollToEnd(); // Audio is the last section
        Gui.Settle();
    }

    private static void Click(AppHarness app, Control control)
    {
        control.BringIntoView();
        Gui.Settle();
        Gui.Click(app.Window, control);
    }

    /// <summary>
    /// Flips the Exclusive mode switch with the mouse. Not <see cref="Gui.Click"/>: the switch turns itself off as it
    /// toggles (the restart starts at once), so it raises no Click event; what it did is checked instead.
    /// </summary>
    private static void FlipExclusive(AppHarness app)
    {
        var exclusive = Gui.Find<ToggleButton>(app.Window, "ExclusiveMode");
        var was = exclusive.IsChecked;
        exclusive.BringIntoView();
        Gui.Settle();
        var point = exclusive.TranslatePoint(new Point(exclusive.Bounds.Width / 2, exclusive.Bounds.Height / 2), app.Window)!.Value;
        Assert.Same(exclusive, (app.Window.InputHitTest(point) as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true));
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        app.Window.MouseUp(point, MouseButton.Left);
        Gui.Settle();
        Assert.NotEqual(was, exclusive.IsChecked);
    }

    private static string[] MenuLabels(AppHarness app) => [.. app.ViewModel.Menu.Entries.Select(e => e.Label)];

    /// <summary>Opens the device menu and chooses <paramref name="label"/>.</summary>
    private static async Task ChooseDeviceAsync(AppHarness app, string label)
    {
        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        Assert.True(app.ViewModel.Menu.IsOpen);
        var entry = app.ViewModel.Menu.Entries.Single(e => e.Label == label);
        await app.ViewModel.Menu.ChooseCommand.ExecuteAsync(entry);
        Gui.Settle();
    }

    /// <summary>Waits for the restart that makes the app's <paramref name="starts"/>th start of the player.</summary>
    private static async Task RestartedAsync(AppHarness app, int starts)
    {
        await Gui.UntilAsync(() => app.Scripted!.AppStarts == starts && app.ViewModel.Player.IsConnected && !Audio(app).IsRestarting, "the player restarted");
        await Gui.UntilAsync(() => app.ViewModel.Player.Output is not null, "the new player's status");
    }

    private static async Task PlayingAsync(AppHarness app)
    {
        app.Scripted!.Status = ScriptedPlayer.Playing("Blue in Green") with { Output = app.Scripted.Status.Output };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.IsPlaying, "playing");
    }

    // ---- the device ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_device_menu_offers_System_default_first_then_the_devices_with_the_chosen_one_ticked()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        Assert.Equal(AudioSettingsViewModel.SystemDefault, Audio(app).DeviceLabel);

        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));

        Assert.Equal(["System default", "Speakers (Realtek)", "USB DAC"], MenuLabels(app));
        Assert.Equal([true, false, false], app.ViewModel.Menu.Entries.Select(e => e.IsChosen));
    }

    [AvaloniaFact]
    public async Task Choosing_a_device_saves_it_and_restarts_the_player_on_it_and_the_music_goes_on()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        Assert.Equal(PlayerOutput.Default, Assert.Single(app.Scripted!.StartOutputs));

        await ChooseDeviceAsync(app, "USB DAC");
        await RestartedAsync(app, 2);

        Assert.Equal("USB DAC", new SettingsStore(app.Data).Load().OutputDevice);
        Assert.Equal(1, app.Scripted.Count("quit"));
        Assert.Equal(new PlayerOutput("USB DAC", false), app.Scripted.StartOutputs.Last());
        await Gui.UntilAsync(() => app.Scripted.Count("resume") == 1, "the music resumed");
        Assert.Equal("USB DAC", Gui.All<TextBlock>(Gui.Find<Button>(app.Window, "OutputDevice")).First().Text);
        Assert.Equal("In use now: USB DAC, shared", Gui.Find<TextBlock>(app.Window, "InUse").Text);

        await ChooseDeviceAsync(app, "System default"); // and back: no device saved
        await RestartedAsync(app, 3);
        Assert.Null(new SettingsStore(app.Data).Load().OutputDevice);
        Assert.Equal(PlayerOutput.Default, app.Scripted.StartOutputs.Last());
        Assert.Equal("In use now: the system default, shared", Audio(app).InUse);
    }

    [AvaloniaFact]
    public async Task A_restart_starts_the_new_player_only_once_the_old_one_has_stopped_and_nothing_else_reaches_the_old_one()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app); // the poll runs while playing
        var disconnected = false;
        app.ViewModel.Player.PropertyChanged += (_, e) =>
            disconnected |= e.PropertyName == nameof(PlayerViewModel.IsConnected) && !app.ViewModel.Player.IsConnected;
        var teardown = app.Scripted!.QuitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        var choosing = app.ViewModel.Menu.ChooseCommand.ExecuteAsync(app.ViewModel.Menu.Entries.Single(e => e.Label == "USB DAC"));
        // The old player ended the app's subscription as it began stopping, and is still tearing down.
        await Gui.UntilAsync(() => app.Scripted.Count("quit") == 1 && app.Scripted.Subscribers == 0, "the old player stopping");
        Gui.Settle();

        // Meanwhile: polls come due, a media key sends next, and the volume moves (it isn't held: it waits).
        for (var i = 0; i < 4; i++)
        {
            app.Time.Advance(PlayerViewModel.PollInterval);
            Gui.Settle();
        }

        await app.ViewModel.Player.SendAsync("next");
        var volume = app.ViewModel.Player.SendAsync("volume", new { value = 0.4 });
        Gui.Settle();
        Assert.False(volume.IsCompleted);

        Assert.False(choosing.IsCompleted);
        Assert.True(Audio(app).IsRestarting);
        Assert.Equal(1, app.Scripted.AppStarts);
        string[] sinceQuit = [.. app.Scripted.Requests.Select(r => r.Cmd).SkipWhile(c => c != "quit").Skip(1)];
        Assert.Empty(sinceQuit); // no status, no next, no subscribe: the old player gets nothing after its quit
        Assert.False(disconnected);

        teardown.SetResult();
        await choosing;
        await volume;
        await RestartedAsync(app, 2);
        await Gui.UntilAsync(() => app.Scripted.Count("next") == 1, "the held next sent");

        Assert.Equal(new PlayerOutput("USB DAC", false), app.Scripted.StartOutputs.Last());
        string[] after = [.. app.Scripted.Requests.Select(r => r.Cmd).SkipWhile(c => c != "quit").Where(c => c is "subscribe" or "resume" or "next")];
        Assert.Equal(["subscribe", "resume", "next"], after); // to the new player, after the resume
        string[] volumeAfter = [.. app.Scripted.Requests.Select(r => r.Cmd).SkipWhile(c => c != "resume").Where(c => c == "volume")];
        Assert.Equal(["volume"], volumeAfter); // the waiting volume too, once the restart was done
        Assert.Equal(1, app.Scripted.Count("volume"));
        Assert.False(disconnected);
        Assert.True(app.ViewModel.Player.StartedPlayer);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_that_has_not_stopped_after_the_quit_is_not_started_over()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        // The old player's lock, still held after its quit connection closed (a teardown past the wait).
        using (new FileStream(app.Scripted!.Endpoint.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await ChooseDeviceAsync(app, "USB DAC");

            await Gui.UntilAsync(() => app.ViewModel.Banner == PlayerViewModel.NotStoppedMessage, "the notice");
            Assert.Equal(1, app.Scripted.AppStarts);
            Assert.False(Audio(app).IsRestarting);
            await Gui.UntilAsync(() => !app.ViewModel.Player.IsConnected, "disconnected");
        }

        // As the notice says: the next start, here by pressing play, uses the new output.
        await app.ViewModel.Player.ResumeAsync();

        Assert.Equal(2, app.Scripted.AppStarts);
        Assert.Equal(new PlayerOutput("USB DAC", false), app.Scripted.StartOutputs.Last());
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Input_sent_during_a_restart_that_fails_is_dropped_and_starts_no_player()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var teardown = app.Scripted!.QuitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        var choosing = app.ViewModel.Menu.ChooseCommand.ExecuteAsync(app.ViewModel.Menu.Entries.Single(e => e.Label == "USB DAC"));
        await Gui.UntilAsync(() => app.Scripted.Count("quit") == 1 && app.Scripted.Subscribers == 0, "the old player stopping");

        // While it stops: next (held) and the volume (waiting).
        await app.ViewModel.Player.SendAsync("next");
        var volume = app.ViewModel.Player.SendAsync("volume", new { value = 0.4 });
        Gui.Settle();

        // It stops, but its lock outlives the wait: the restart gives up.
        using (new FileStream(app.Scripted.Endpoint.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            teardown.SetResult();
            await choosing;
            await volume;
            await Gui.UntilAsync(() => app.ViewModel.Banner == PlayerViewModel.NotStoppedMessage, "the notice");
            await Gui.UntilAsync(() => !app.ViewModel.Player.IsConnected, "disconnected");
            Gui.Settle();
        }

        Assert.Equal(1, app.Scripted.AppStarts); // nothing started a player while the old one held the data folder
        Assert.Equal(0, app.Scripted.Count("next"));
        Assert.Equal(0, app.Scripted.Count("volume"));
        Assert.Equal(PlayerViewModel.NotStoppedMessage, app.ViewModel.Banner); // not replaced by a start or connection error
        Assert.False(app.ViewModel.Player.IsInputHeld);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_during_a_restart_quits_the_player_the_restart_started()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        var teardown = app.Scripted!.QuitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        var choosing = app.ViewModel.Menu.ChooseCommand.ExecuteAsync(app.ViewModel.Menu.Entries.Single(e => e.Label == "USB DAC"));
        await Gui.UntilAsync(() => app.Scripted.Count("quit") == 1 && app.Scripted.Subscribers == 0, "the old player stopping");

        var closing = app.ViewModel.Player.ShutdownAsync(); // the window closes now
        Gui.Settle();
        Assert.False(closing.IsCompleted); // it waits for the restart

        app.Scripted.QuitGate = null; // the new player stops at once when told
        teardown.SetResult();
        await choosing;
        await closing;

        Assert.Equal(2, app.Scripted.AppStarts);
        Assert.Equal(2, app.Scripted.Count("quit")); // the old player's, then the new one's
        await Gui.UntilAsync(() => !app.Scripted.IsRunning, "the new player quit");
    }

    [AvaloniaFact]
    public async Task A_player_the_CLI_started_stays_the_CLIs_after_a_restart()
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Initialize = false,
            AudioDevices = new StubAudioDevices { Names = TwoDevices },
        });
        await using var _ = app;
        app.Scripted!.Start(); // as music-spin play would
        await app.ViewModel.InitializeAsync();
        await Gui.UntilAsync(() => app.ViewModel.Player.IsConnected, "connected");
        Assert.False(app.ViewModel.Player.StartedPlayer);
        await OpenSettingsAsync(app);

        await ChooseDeviceAsync(app, "USB DAC");
        await RestartedAsync(app, 1); // the app's first start

        Assert.Equal(new PlayerOutput("USB DAC", false), app.Scripted.StartOutputs.Single());
        Assert.False(app.ViewModel.Player.StartedPlayer); // closing the window leaves it playing, as before
    }

    [AvaloniaFact]
    public async Task A_paused_player_restarts_paused()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);

        await ChooseDeviceAsync(app, "Speakers (Realtek)");
        await RestartedAsync(app, 2);

        Assert.Equal(new PlayerOutput("Speakers (Realtek)", false), app.Scripted!.StartOutputs.Last());
        Assert.Equal(0, app.Scripted.Count("resume"));
        Assert.False(app.ViewModel.Player.IsPlaying);
    }

    [AvaloniaFact]
    public async Task Choosing_the_device_already_chosen_restarts_nothing()
    {
        await using var app = await OpenAsync(settings: new AppSettings { OutputDevice = "USB DAC" });
        await OpenSettingsAsync(app);
        var before = File.ReadAllText(app.Data.SettingsFile);

        await ChooseDeviceAsync(app, "USB DAC");

        Assert.Equal(0, app.Scripted!.Count("quit"));
        Assert.Equal(1, app.Scripted.AppStarts);
        Assert.Equal(before, File.ReadAllText(app.Data.SettingsFile));
    }

    [AvaloniaFact]
    public async Task The_app_starts_the_player_with_the_saved_device_and_mode()
    {
        await using var app = await OpenAsync(settings: new AppSettings { OutputDevice = "USB DAC", ExclusiveMode = true });

        Assert.Equal(new PlayerOutput("USB DAC", true), Assert.Single(app.Scripted!.StartOutputs));
        await Gui.UntilAsync(() => Audio(app).InUse == "In use now: USB DAC, exclusive", "the output in use");
        Assert.True(Audio(app).Exclusive);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_saved_device_that_is_not_plugged_in_says_so_and_stays_in_the_menu()
    {
        await using var app = await OpenAsync(["Speakers (Realtek)"], new AppSettings { OutputDevice = "USB DAC" });
        await OpenSettingsAsync(app);

        Assert.Equal("USB DAC (not connected)", Audio(app).DeviceLabel);
        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        Assert.Equal(["System default", "Speakers (Realtek)", "USB DAC (not connected)"], MenuLabels(app));
        Assert.True(app.ViewModel.Menu.Entries[^1].IsChosen);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task When_the_devices_cannot_be_listed_a_notice_says_so_and_System_default_is_still_offered()
    {
        await using var app = await OpenAsync();
        app.Options.AudioDevices.Error = new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, "The audio devices couldn't be listed: no backend.");
        await OpenSettingsAsync(app);

        await Gui.UntilAsync(() => app.ViewModel.Banner == "The audio devices couldn't be listed: no backend.", "the notice");
        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        Assert.Equal(["System default"], MenuLabels(app));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task When_the_player_cannot_start_again_a_notice_says_so_and_the_controls_come_back()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        app.Scripted!.AllowStart = false;

        await ChooseDeviceAsync(app, "USB DAC");
        await Gui.UntilAsync(() => app.ViewModel.Banner?.Contains("may not be started", StringComparison.Ordinal) == true, "the notice");

        await Gui.UntilAsync(() => !Audio(app).IsRestarting, "the restart over");
        Assert.False(app.ViewModel.Player.IsConnected);
        Assert.Equal("USB DAC", new SettingsStore(app.Data).Load().OutputDevice); // saved: the next start uses it
        Assert.True(Gui.Find<ToggleButton>(app.Window, "ExclusiveMode").IsEnabled);
        Assert.True(Audio(app).OpenDeviceMenuCommand.CanExecute(null));
    }

    // ---- exclusive mode ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_Exclusive_mode_switch_is_saved_and_restarts_the_player_exclusive()
    {
        await using var app = await OpenAsync(settings: new AppSettings { OutputDevice = "USB DAC" });
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var exclusive = Gui.Find<ToggleButton>(app.Window, "ExclusiveMode");
        Assert.False(exclusive.IsChecked);

        FlipExclusive(app);
        await RestartedAsync(app, 2);

        Assert.True(new SettingsStore(app.Data).Load().ExclusiveMode);
        Assert.Equal(new PlayerOutput("USB DAC", true), app.Scripted!.StartOutputs.Last());
        Assert.Equal("In use now: USB DAC, exclusive", Gui.Find<TextBlock>(app.Window, "InUse").Text);
        await Gui.UntilAsync(() => app.Scripted.Count("resume") == 1, "the music resumed");

        FlipExclusive(app);
        await RestartedAsync(app, 3);
        Assert.False(new SettingsStore(app.Data).Load().ExclusiveMode);
        Assert.Equal(new PlayerOutput("USB DAC", false), app.Scripted.StartOutputs.Last());
    }

    [AvaloniaFact]
    public async Task While_the_player_restarts_the_audio_controls_wait()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        app.Scripted!.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Click(app, Gui.Find<Button>(app.Window, "OutputDevice"));
        var choosing = app.ViewModel.Menu.ChooseCommand.ExecuteAsync(app.ViewModel.Menu.Entries.Single(e => e.Label == "USB DAC"));
        await Gui.UntilAsync(() => app.Scripted.ConnectsWaiting == 1, "the restart connecting");

        Assert.True(Audio(app).IsRestarting);
        Assert.False(Gui.Find<ToggleButton>(app.Window, "ExclusiveMode").IsEnabled);
        Assert.False(Gui.Find<Button>(app.Window, "OutputDevice").IsEffectivelyEnabled);

        app.Scripted.ConnectGate.SetResult();
        await choosing;
        await RestartedAsync(app, 2);
        Assert.True(Gui.Find<ToggleButton>(app.Window, "ExclusiveMode").IsEnabled);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_without_an_audio_device_says_it_plays_silently()
    {
        await using var app = await OpenAsync();
        app.Scripted!.Status = app.Scripted.Status with { Output = new OutputInfo("null", null, false) };
        await app.Scripted.PushAsync("stateChanged");

        await Gui.UntilAsync(() => Audio(app).InUse == "In use now: no audio device (silent output)", "the silent output");
    }

    // ---- snapshots ----------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_Settings_Audio(AppTheme theme)
    {
        await using var app = await OpenAsync(settings: new AppSettings { OutputDevice = "USB DAC" }, theme: theme);
        await OpenSettingsAsync(app);
        await Gui.UntilAsync(() => Audio(app).InUse is not null, "the output in use");
        Gui.Settle();

        Snapshots.Match(app.Window, Gui.Find<Border>(app.Window, "Dialog"), $"settings-audio-{theme.ToString().ToLowerInvariant()}");
    }
}

/// <summary>Settings › Audio's view model on its own: a change the settings file won't take.</summary>
public sealed class AudioSettingsViewModelTests : IAsyncDisposable
{
    private readonly string _root = TempDir.Create("audio");
    private readonly ScriptedPlayer _player;
    private readonly PlayerViewModel _playerVm;
    private readonly Notices _notices = new();
    private readonly List<string> _posted = [];

    public AudioSettingsViewModelTests()
    {
        _player = new ScriptedPlayer(new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override));
        _playerVm = new PlayerViewModel(_player, new FakeTimeProvider(), new QueueDispatcher());
        _notices.Posted += (_, message) => _posted.Add(message);
    }

    private AudioSettingsViewModel Create(ISettingsService settings)
        => new(new StubAudioDevices { Names = ["USB DAC"] }, settings, _playerVm, new MenuViewModel(), _notices);

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_device_the_settings_file_wont_take_is_not_chosen_and_the_player_is_not_restarted()
    {
        var vm = Create(new UnwritableSettings());
        await vm.LoadAsync();

        await vm.ChooseAsync("USB DAC");

        Assert.Null(vm.Device);
        Assert.Equal(AudioSettingsViewModel.SystemDefault, vm.DeviceLabel);
        Assert.Equal(0, _player.AppStarts);
        Assert.StartsWith("The setting wasn't saved, so the player keeps its output", Assert.Single(_posted), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_Exclusive_mode_the_settings_file_wont_take_turns_the_switch_back()
    {
        var vm = Create(new UnwritableSettings());

        vm.Exclusive = true;

        Assert.False(vm.Exclusive);
        Assert.False(vm.IsRestarting);
        Assert.Equal(0, _player.AppStarts);
        Assert.Single(_posted);
    }

    public async ValueTask DisposeAsync()
    {
        await _playerVm.DisposeAsync();
        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }

    private sealed class UnwritableSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public void Update(Func<AppSettings, AppSettings> change)
            => throw new MusicSpinException(MusicSpinErrorCode.DataDirectoryNotWritable, "the data folder is read-only.");
    }
}
