using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core;
using MusicSpin.Core.Equalization;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Settings › Audio › Equalizer (#141): the switch, the presets, the pre-amp and the ten bands. Each change is saved, so
/// the next player starts with it, and the running player is told (<c>eq</c>) without a restart. With a player running,
/// the change is saved once it has answered (as the CLI saves the player's answer), so the tests wait for the file.
/// </summary>
public sealed class EqualizerSettingsTests
{
    private static readonly double[] Rock = [.. EqualizerSettings.BuiltIn("rock")!.Gains];
    private static readonly double[] Jazz = [.. EqualizerSettings.BuiltIn("jazz")!.Gains];

    private static Task<AppHarness> OpenAsync(AppSettings? settings = null, AppTheme theme = AppTheme.Dark)
        => AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Settings = settings ?? new AppSettings(),
            Theme = theme,
            AudioDevices = new StubAudioDevices { Names = ["USB DAC"] },
        });

    private static EqualizerViewModel Eq(AppHarness app) => app.ViewModel.Audio.Equalizer;

    private static T Find<T>(AppHarness app, string name)
        where T : Control => Gui.Find<T>(app.Window, name);

    private static AppSettings Saved(AppHarness app) => new SettingsStore(app.Data).Load();

    /// <summary>The settings file once <paramref name="saved"/> holds for it.</summary>
    private static async Task<AppSettings> SavedAsync(AppHarness app, Func<AppSettings, bool> saved, string what)
    {
        await Gui.UntilAsync(() => saved(Saved(app)), what);
        return Saved(app);
    }

    /// <summary>The equalizer as a player started with <paramref name="settings"/> reports it.</summary>
    private static EqualizerStatus Reported(AppSettings settings)
        => ScriptedPlayer.EqualizerOf(new GraphicEqualizer(EqualizerSettings.From(settings), EqualizerSettings.UserPresets(settings)));

    private static async Task OpenSettingsAsync(AppHarness app)
    {
        var lists = app.Options.AudioDevices.Lists;
        app.ShowSettingsPage("Equalizer");
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations();
        await Gui.UntilAsync(() => app.Options.AudioDevices.Lists > lists, "the devices listed");
    }

    /// <summary>Playing, with the equalizer saved (as a player started now has it), applying <c>eq</c> as a player does.</summary>
    private static async Task PlayingAsync(AppHarness app)
    {
        app.Scripted!.FollowsCommands = true;
        app.Scripted.Status = ScriptedPlayer.Playing("Blue in Green") with { Output = app.Scripted.Status.Output, Equalizer = Reported(Saved(app)) };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.IsPlaying, "playing");
    }

    private static void Click(AppHarness app, Control control)
    {
        control.BringIntoView();
        Gui.Settle();
        Gui.Click(app.Window, control);
    }

    /// <summary>Clicks the middle of a switch (it may not raise Click; what it did is checked instead).</summary>
    private static bool Flip(AppHarness app, string name)
    {
        var toggle = Find<ToggleButton>(app, name);
        var was = toggle.IsChecked;
        toggle.BringIntoView();
        Gui.Settle();
        var point = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        app.Window.MouseUp(point, MouseButton.Left);
        Gui.Settle();
        Assert.NotEqual(was, toggle.IsChecked);
        return toggle.IsChecked == true;
    }

    /// <summary>A band's slider (0..9), or the pre-amp's (-1).</summary>
    private static ScrubBar Slider(AppHarness app, int band)
        => band < 0
            ? Gui.All<ScrubBar>(Find<ContentControl>(app, "EqualizerPreamp")).Single()
            : Gui.All<ScrubBar>(Find<ItemsControl>(app, "EqualizerBands")).ElementAt(band);

    /// <summary>The slider as it is on screen: upright (the bar itself is laid out across, then turned).</summary>
    private static Rect Upright(AppHarness app, ScrubBar slider) => Gui.BoundsIn(app.Window, (Control)slider.GetVisualParent()!);

    private static string[] Texts(AppHarness app, string name, string cls)
        => [.. Gui.All<TextBlock>(Find<Control>(app, name)).Where(t => t.Classes.Contains(cls)).Select(t => t.Text ?? string.Empty)];

    private static string? PresetShown(AppHarness app)
        => Gui.All<TextBlock>(Find<Button>(app, "EqualizerPreset")).Single(t => t.Classes.Contains("chosen")).Text;

    /// <summary>The <paramref name="count"/>th <c>eq</c> the player got.</summary>
    private static async Task<JsonElement> SentAsync(AppHarness app, int count)
    {
        await Gui.UntilAsync(() => app.Scripted!.Count("eq") >= count, $"eq #{count} sent");
        return app.Scripted!.Sent("eq")[count - 1];
    }

    private static EqualizerStatus Status(bool enabled, string? preset, double[] gains, double headroom = 0)
        => new(enabled, preset, 0, headroom, EqualizerSettings.Bands, gains,
            [.. EqualizerSettings.BuiltInPresets.Select(p => p.Name)], new Dictionary<string, IReadOnlyList<double>>());

    private static void Type(AppHarness app, string name, string text)
    {
        var box = Find<TextBox>(app, name);
        box.BringIntoView();
        Gui.Settle();
        box.Focus();
        app.Window.KeyTextInput(text);
        Gui.Settle();
    }

    // ---- showing what is saved ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_saved_equalizer_shows_its_switch_preset_bands_and_pre_amp_and_how_far_boosts_lower_it()
    {
        await using var app = await OpenAsync(new AppSettings { EqualizerEnabled = true, EqualizerGains = Rock, EqualizerPreset = "rock", EqualizerPreampDb = -2 });
        await OpenSettingsAsync(app);

        Assert.True(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        Assert.Equal("Rock", PresetShown(app));
        Assert.Equal(["31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"], Texts(app, "EqualizerBands", "eqBand"));
        Assert.Equal(["+5", "+4", "+2", "-1", "-2", "-1", "+1", "+3", "+4", "+5"], Texts(app, "EqualizerBands", "eqGain"));
        Assert.Equal(["-2"], Texts(app, "EqualizerPreamp", "eqGain"));
        Assert.Equal(["Pre"], Texts(app, "EqualizerPreamp", "eqBand"));
        Assert.StartsWith("Lowered ", Find<TextBlock>(app, "EqualizerNote").Text, StringComparison.Ordinal); // rock boosts
        Assert.False(Find<Button>(app, "EqualizerDeletePreset").IsVisible); // a built-in preset
        Assert.Equal("1kHz: -1 dB", Slider(app, 5).ValueText);
        Assert.Equal(-1, Slider(app, 5).Value);
    }

    [AvaloniaFact]
    public async Task Off_by_default_flat_and_its_note_says_what_it_is()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);

        Assert.False(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        Assert.Equal("Flat", PresetShown(app));
        Assert.All(Texts(app, "EqualizerBands", "eqGain"), t => Assert.Equal("0", t));
        Assert.Equal(EqualizerViewModel.Note, Find<TextBlock>(app, "EqualizerNote").Text);
    }

    // ---- changing it ----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_switch_turns_it_on_and_off_in_the_running_player_without_a_restart_and_saves_it()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);

        Assert.True(Flip(app, "EqualizerSwitch"));
        Assert.True((await SentAsync(app, 1)).GetProperty("enabled").GetBoolean());
        await SavedAsync(app, s => s.EqualizerEnabled, "on, saved");

        Assert.False(Flip(app, "EqualizerSwitch"));
        Assert.False((await SentAsync(app, 2)).GetProperty("enabled").GetBoolean());
        await SavedAsync(app, s => !s.EqualizerEnabled, "off, saved");
        Assert.Equal(1, app.Scripted!.AppStarts); // no restart
    }

    [AvaloniaFact]
    public async Task Keys_move_a_band_by_half_a_dB_turning_the_equalizer_on_and_its_preset_custom()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var gains = Eq(app).Bands[5]; // 1 kHz

        await KeyBandAsync(app, 5, Key.Up, PhysicalKey.ArrowUp, () => gains.Gain == 0.5, "0.5 dB");

        var sent = await SentAsync(app, 1);
        Assert.Equal((6, 0.5), (sent.GetProperty("band").GetInt32(), sent.GetProperty("gain").GetDouble()));
        Assert.True(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        Assert.Equal("Custom", PresetShown(app));
        Assert.Equal("+0.5", Texts(app, "EqualizerBands", "eqGain")[5]);
        var saved = await SavedAsync(app, s => s.EqualizerGains?[5] == 0.5, "the band saved");
        Assert.Equal((true, 0.5, null), (saved.EqualizerEnabled, saved.EqualizerGains![5], saved.EqualizerPreset));

        await KeyBandAsync(app, 5, Key.PageUp, PhysicalKey.PageUp, () => gains.Gain == 3, "3 dB"); // next multiple of 3
        await KeyBandAsync(app, 5, Key.End, PhysicalKey.End, () => gains.Gain == 12, "12 dB");
        await KeyBandAsync(app, 5, Key.Home, PhysicalKey.Home, () => gains.Gain == -12, "-12 dB");
        await KeyBandAsync(app, 5, Key.Down, PhysicalKey.ArrowDown, () => gains.Gain == -12, "still -12 dB"); // already at the bottom
        await SentAsync(app, 4);
        Assert.Equal(4, app.Scripted!.Count("eq"));
    }

    /// <summary>
    /// Focuses band <paramref name="band"/>, presses <paramref name="key"/>, waits until <paramref name="done"/>.
    /// Keys only reach the bar while it is focused; after a change the player's answer and the save can move focus
    /// (Windows CI: End after PageUp stayed at 3 dB), so each key focuses again and waits for the gain, not for time.
    /// </summary>
    private static async Task KeyBandAsync(AppHarness app, int band, Key key, PhysicalKey physical, Func<bool> done, string what)
    {
        var slider = Slider(app, band);
        slider.BringIntoView();
        Gui.Settle();
        slider.Focus(NavigationMethod.Tab);
        await Gui.UntilAsync(() => slider.IsFocused, $"band {band} focused before {what}");
        if (key == Key.End)
        {
            Assert.True(slider.EndKey); // style must arm End; otherwise the key is ignored
        }

        Gui.Press(app.Window, key, physical);
        await Gui.UntilAsync(done, what);
    }

    [AvaloniaFact]
    public async Task Dragging_a_band_up_raises_it_tells_the_player_every_step_and_saves_only_where_it_is_let_go()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var bar = Slider(app, 0);
        var upright = Upright(app, bar);
        Assert.True(upright.Height > upright.Width * 3, $"the slider is upright: {upright}");
        var before = File.ReadAllText(app.Data.SettingsFile);
        var middle = upright.Center;

        app.Window.MouseMove(middle);
        app.Window.MouseDown(middle, MouseButton.Left);
        Gui.Settle();
        Assert.Equal(0, Eq(app).Bands[0].Gain); // the middle is 0 dB
        var seen = new List<double>();
        for (var step = 1; step <= 6; step++)
        {
            app.Window.MouseMove(middle - new Point(0, step * upright.Height / 24)); // up, 1 dB a step
            Gui.Settle();
            seen.Add(Eq(app).Bands[0].Gain);
            Assert.Equal(before, File.ReadAllText(app.Data.SettingsFile)); // nothing written mid-drag
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], seen);
        app.Window.MouseUp(middle - new Point(0, upright.Height / 4), MouseButton.Left);
        Gui.Settle();

        await SentAsync(app, 7); // the press (0 dB, turning it on), then each step
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], app.Scripted!.Sent("eq").Select(e => e.GetProperty("gain").GetDouble()));
        await SavedAsync(app, s => s.EqualizerGains?[0] == 6, "where it was let go, saved");
        Assert.Equal("+6", Texts(app, "EqualizerBands", "eqGain")[0]);
    }

    [AvaloniaFact]
    public async Task The_pre_amp_moves_without_turning_the_equalizer_on()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var preamp = Slider(app, -1);
        preamp.BringIntoView();
        preamp.Focus();
        Gui.Settle();

        Gui.Press(app.Window, Key.Down, PhysicalKey.ArrowDown);

        var sent = await SentAsync(app, 1);
        Assert.Equal(-0.5, sent.GetProperty("preamp").GetDouble());
        Assert.False(sent.TryGetProperty("enabled", out _));
        Assert.False(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        await SavedAsync(app, s => s.EqualizerPreampDb == -0.5, "the pre-amp saved");
        Assert.False(Saved(app).EqualizerEnabled);
        Assert.Equal("Pre-amp: -0.5 dB", preamp.ValueText);
    }

    [AvaloniaFact]
    public async Task The_preset_menu_lists_every_preset_with_the_current_one_ticked_and_choosing_one_applies_it()
    {
        await using var app = await OpenAsync(new AppSettings { EqualizerPresets = new() { ["late-night"] = [1, 1, 1, 1, 1, 0, 0, 0, 0, 0] } });
        await OpenSettingsAsync(app);
        await PlayingAsync(app);

        Click(app, Find<Button>(app, "EqualizerPreset"));

        var menu = app.ViewModel.Menu;
        Assert.True(menu.IsOpen);
        Assert.Equal(
            ["Flat", "Bass boost", "Treble boost", "Rock", "Pop", "Jazz", "Classical", "Vocal", "Loudness", "Electronic", "Acoustic", "Late night"],
            menu.Entries.Select(e => e.Label));
        Assert.Equal("Flat", Assert.Single(menu.Entries, e => e.IsChosen).Label);

        await menu.Entries.Single(e => e.Label == "Rock").Run();
        Gui.Settle();

        Assert.Equal("rock", (await SentAsync(app, 1)).GetProperty("preset").GetString());
        Assert.Equal("Rock", PresetShown(app));
        Assert.True(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        Assert.Equal(["+5", "+4", "+2", "-1", "-2", "-1", "+1", "+3", "+4", "+5"], Texts(app, "EqualizerBands", "eqGain"));
        var saved = await SavedAsync(app, s => s.EqualizerPreset == "rock", "rock saved");
        Assert.Equal(("rock", true), (saved.EqualizerPreset, saved.EqualizerEnabled));
        Assert.Equal(Rock, saved.EqualizerGains);
        Assert.Equal([1, 1, 1, 1, 1, 0, 0, 0, 0, 0], saved.EqualizerPresets!["late-night"]); // the user's presets kept
    }

    [AvaloniaFact]
    public async Task Reset_puts_every_band_and_the_pre_amp_back_to_0_dB_and_leaves_the_switch_as_it_is()
    {
        await using var app = await OpenAsync(new AppSettings { EqualizerEnabled = true, EqualizerGains = Rock, EqualizerPreset = "rock", EqualizerPreampDb = -3 });
        await OpenSettingsAsync(app);
        await PlayingAsync(app);

        Click(app, Find<Button>(app, "EqualizerReset"));

        var sent = await SentAsync(app, 1);
        Assert.Equal(("flat", 0.0, true), (sent.GetProperty("preset").GetString(), sent.GetProperty("preamp").GetDouble(), sent.GetProperty("enabled").GetBoolean()));
        Assert.All(Texts(app, "EqualizerBands", "eqGain"), t => Assert.Equal("0", t));
        Assert.Equal(["0"], Texts(app, "EqualizerPreamp", "eqGain"));
        Assert.Equal("Flat", PresetShown(app));
        Assert.True(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        Assert.Equal(EqualizerViewModel.Note, Find<TextBlock>(app, "EqualizerNote").Text); // nothing boosted now
        var saved = await SavedAsync(app, s => s.EqualizerPreset == "flat", "flat saved");
        Assert.Equal(("flat", 0.0, true), (saved.EqualizerPreset, saved.EqualizerPreampDb, saved.EqualizerEnabled));
    }

    [AvaloniaFact]
    public async Task The_gains_are_saved_as_a_preset_under_the_name_typed_and_a_saved_preset_is_deleted_after_asking()
    {
        await using var app = await OpenAsync(new AppSettings { EqualizerEnabled = true, EqualizerGains = [2, 2, 1, 0, 0, 0, 0, 1, 2, 2], EqualizerPreset = null });
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        Assert.Equal("Custom", PresetShown(app));

        Type(app, "EqualizerPresetName", "Late night");
        Gui.Press(app.Window, Key.Enter, PhysicalKey.Enter);

        Assert.Equal("late-night", (await SentAsync(app, 1)).GetProperty("save").GetString());
        Assert.Equal("Late night", PresetShown(app));
        Assert.True(string.IsNullOrEmpty(Find<TextBox>(app, "EqualizerPresetName").Text)); // ready for the next
        var saved = await SavedAsync(app, s => s.EqualizerPresets?.ContainsKey("late-night") == true, "the preset saved");
        Assert.Equal([2, 2, 1, 0, 0, 0, 0, 1, 2, 2], saved.EqualizerPresets!["late-night"]);
        Assert.Equal("late-night", saved.EqualizerPreset);
        var delete = Find<Button>(app, "EqualizerDeletePreset");
        Assert.True(delete.IsVisible);

        Click(app, delete);
        var menu = app.ViewModel.Menu;
        Assert.Equal("Delete the preset “Late night”?", menu.Caption);
        Assert.Equal(1, app.Scripted!.Count("eq")); // only asked so far
        await menu.Entries.Single(e => e.Label == "Delete").Run();
        Gui.Settle();

        Assert.Equal("late-night", (await SentAsync(app, 2)).GetProperty("delete").GetString());
        Assert.Equal("Custom", PresetShown(app)); // the gains stay
        Assert.False(delete.IsVisible);
        saved = await SavedAsync(app, s => s.EqualizerPresets is { Count: 0 }, "the preset deleted");
        Assert.Equal([2, 2, 1, 0, 0, 0, 0, 1, 2, 2], saved.EqualizerGains!);
    }

    // ---- unhappy paths ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_built_in_name_is_refused_with_the_reason_and_an_empty_one_cant_be_saved_nothing_is_sent_or_saved()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var before = File.ReadAllText(app.Data.SettingsFile);
        Assert.False(Find<Button>(app, "EqualizerSavePreset").IsEffectivelyEnabled); // no name yet

        Type(app, "EqualizerPresetName", "Bass boost");
        Click(app, Find<Button>(app, "EqualizerSavePreset"));

        var problem = Find<TextBlock>(app, "EqualizerPresetProblem");
        Assert.True(problem.IsVisible);
        Assert.Equal("'bass-boost' is a built-in preset; save under another name.", problem.Text);
        Assert.Equal(0, app.Scripted!.Count("eq"));
        Assert.Equal(before, File.ReadAllText(app.Data.SettingsFile));

        Type(app, "EqualizerPresetName", "!"); // editing the name clears the reason
        Assert.False(problem.IsVisible);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_change_made_behind_the_apps_back_shows_while_Settings_is_open_and_nothing_is_saved_or_sent()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);

        // `music-spin eq preset jazz`
        app.Scripted!.Status = app.Scripted.Status with { Equalizer = Status(true, "jazz", Jazz, headroom: 3) };
        await app.Scripted.PushAsync("stateChanged");

        await Gui.UntilAsync(() => PresetShown(app) == "Jazz", "the preset following the player");
        Assert.True(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
        Assert.Equal("+3", Texts(app, "EqualizerBands", "eqGain")[0]);
        Assert.Equal("Lowered 3 dB so boosts can't clip", Find<TextBlock>(app, "EqualizerNote").Text); // as the player says
        Assert.Equal(0, app.Scripted.Count("eq"));
        Assert.Equal(("flat", false), (Saved(app).EqualizerPreset, Saved(app).EqualizerEnabled));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task The_answer_to_an_earlier_change_does_not_undo_a_later_one_here()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var held = app.Scripted!.Hold("eq");
        var band = Eq(app).Bands[0];
        var shown = new List<double>();
        band.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EqualizerSliderViewModel.Gain))
            {
                shown.Add(band.Gain);
            }
        };

        Assert.True(Flip(app, "EqualizerSwitch"));
        await SentAsync(app, 1);
        band.SetGainCommand.Execute(1.0); // on its way behind the first: the first's answer says 0 dB
        Assert.False(Saved(app).EqualizerEnabled); // saved once the player has answered
        held.SetResult();

        var saved = await SavedAsync(app, s => s.EqualizerGains?[0] == 1, "both saved once answered");
        Assert.True(saved.EqualizerEnabled);
        Assert.Equal(2, app.Scripted.Count("eq"));
        Assert.Equal([1], shown); // never back to 0 dB in between
        Assert.True(Find<ToggleButton>(app, "EqualizerSwitch").IsChecked);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_preset_the_CLI_saved_before_a_change_reached_the_player_is_kept_when_the_change_is_saved()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        // `music-spin eq save cli-one`, just before the app's change reached the player: the app hears of it in the answer
        var cliOne = new double[] { 1, 2, 3, 0, 0, 0, 0, 0, 0, 0 };
        app.Scripted!.Status = app.Scripted.Status with
        {
            Equalizer = app.Scripted.Status.Equalizer! with { UserPresets = new Dictionary<string, IReadOnlyList<double>> { ["cli-one"] = cliOne } },
        };

        Assert.True(Flip(app, "EqualizerSwitch"));

        var saved = await SavedAsync(app, s => s.EqualizerEnabled, "on, saved once answered");
        Assert.Equal(cliOne, saved.EqualizerPresets!["cli-one"]);
        Click(app, Find<Button>(app, "EqualizerPreset"));
        Assert.Contains("Cli one", app.ViewModel.Menu.Entries.Select(e => e.Label));
        app.ViewModel.Menu.Close();
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_change_from_the_CLI_mid_drag_shows_and_is_kept_when_the_drag_is_saved()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        var band = Eq(app).Bands[2];
        band.Scrub = 1; // mid-drag, as the slider reports it
        band.SetGainCommand.Execute(1.0);
        await Gui.UntilAsync(() => app.ViewModel.Player.Status?.Equalizer?.Gains[2] == 1, "the step answered");

        // `music-spin eq preamp -3`
        app.Scripted!.Status = app.Scripted.Status with { Equalizer = app.Scripted.Status.Equalizer! with { PreampDb = -3 } };
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.Status?.Equalizer?.PreampDb == -3, "the CLI's status");
        Gui.Settle();
        Assert.Equal(0, Eq(app).Preamp.Gain); // not mid-drag
        Assert.Null(Saved(app).EqualizerGains);

        band.Scrub = null;

        var saved = await SavedAsync(app, s => s.EqualizerGains?[2] == 1, "the drag saved");
        Assert.Equal((-3.0, true), (saved.EqualizerPreampDb, saved.EqualizerEnabled));
        Assert.Equal(-3, Eq(app).Preamp.Gain);
        Assert.Equal(1, band.Gain);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task While_the_players_input_is_held_a_drag_is_saved_as_shown_not_as_the_player_said_before()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        app.ViewModel.Player.HoldInput(); // the turntable scene: commands wait
        var band = Eq(app).Bands[2];
        band.Scrub = 2;
        band.SetGainCommand.Execute(2.0);
        var reads = app.Scripted!.Count("status");
        await app.Scripted.PushAsync("stateChanged"); // the player doesn't have it yet
        await Gui.UntilAsync(() => app.Scripted.Count("status") > reads, "the status read");
        Gui.Settle();

        band.Scrub = null;

        Assert.Equal(2, Saved(app).EqualizerGains![2]);
        Assert.Equal(2, band.Gain);
        Assert.Equal(0, app.Scripted.Count("eq"));
    }

    [AvaloniaFact]
    public async Task A_status_the_same_as_what_is_shown_changes_nothing()
    {
        await using var app = await OpenAsync(new AppSettings { EqualizerEnabled = true, EqualizerGains = Rock, EqualizerPreset = "rock" });
        await OpenSettingsAsync(app);
        await PlayingAsync(app);
        await Gui.UntilAsync(() => app.ViewModel.Player.Status?.Equalizer is not null, "the player's equalizer");
        Gui.Settle();
        var changed = new List<string?>();
        Eq(app).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Eq(app).Bands[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var reads = app.Scripted!.Count("status");

        for (var poll = 1; poll <= 3; poll++)
        {
            await app.Scripted.PushAsync("stateChanged");
            await Gui.UntilAsync(() => app.Scripted.Count("status") >= reads + poll, $"status #{poll}");
            Gui.Settle();
        }

        Assert.Empty(changed);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Closing_Settings_mid_drag_saves_the_gain_shown()
    {
        await using var app = await OpenAsync();
        await OpenSettingsAsync(app);
        var band = Eq(app).Bands[2];
        band.Scrub = 4; // mid-drag, as the slider reports it
        band.SetGainCommand.Execute(4.0);
        Assert.Null(Saved(app).EqualizerGains);

        app.ViewModel.Settings.Close();

        Assert.Equal(4, Saved(app).EqualizerGains![2]);
    }

    // ---- snapshots ----------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_Settings_Equalizer(AppTheme theme)
    {
        await using var app = await OpenAsync(
            new AppSettings
            {
                EqualizerEnabled = true,
                EqualizerGains = [4, 3, 1.5, 0, -1, -1.5, 0, 2, 3.5, 4],
                EqualizerPreset = "late-night",
                EqualizerPreampDb = -1,
                EqualizerPresets = new() { ["late-night"] = [4, 3, 1.5, 0, -1, -1.5, 0, 2, 3.5, 4] },
            },
            theme);
        await OpenSettingsAsync(app);

        Snapshots.Match(app.Window, Find<Border>(app, "Dialog"), $"settings-equalizer-{theme.ToString().ToLowerInvariant()}");
    }
}

/// <summary>The equalizer's view model on its own: no player running, a settings file that won't take it, bad input.</summary>
public sealed class EqualizerViewModelTests : IAsyncDisposable
{
    private readonly string _root = TempDir.Create("eq");
    private readonly ScriptedPlayer _player;
    private readonly PlayerViewModel _playerVm;
    private readonly Notices _notices = new();
    private readonly List<string> _posted = [];
    private readonly MenuViewModel _menu = new();

    public EqualizerViewModelTests()
    {
        _player = new ScriptedPlayer(new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override));
        _playerVm = new PlayerViewModel(_player, new FakeTimeProvider(), new QueueDispatcher());
        _notices.Posted += (_, message) => _posted.Add(message);
    }

    private EqualizerViewModel Create(ISettingsService settings) => new(settings, _playerVm, _menu, _notices);

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Without_a_running_player_a_change_is_only_saved_for_the_next_one()
    {
        var settings = new RecordingSettings();
        var vm = Create(settings);
        Assert.False(_playerVm.IsConnected);

        vm.Bands[0].SetGainCommand.Execute(3.0);

        Assert.Equal(3, settings.Current.EqualizerGains![0]);
        Assert.True(settings.Current.EqualizerEnabled);
        Assert.Equal(0, _player.Count("eq"));
        Assert.Equal(0, _player.AppStarts); // nothing started for it
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_settings_file_that_wont_take_it_leaves_the_change_shown_and_says_it_wasnt_saved()
    {
        var vm = Create(new UnwritableSettings());

        vm.Enabled = true;

        Assert.True(vm.Enabled);
        Assert.StartsWith("The equalizer applies now but wasn't saved", Assert.Single(_posted), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Corrupt_saved_presets_are_left_out_of_the_menu()
    {
        var vm = Create(new RecordingSettings(new AppSettings
        {
            EqualizerPresets = new()
            {
                ["rock"] = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1], // a built-in name
                ["short"] = [1, 2],
                ["loud"] = [99, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                ["fine"] = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            },
        }));

        vm.OpenPresetMenuCommand.Execute(null);

        Assert.Equal([.. EqualizerSettings.BuiltInPresets.Select(p => EqualizerViewModel.Display(p.Name)), "Fine"], _menu.Entries.Select(e => e.Label));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Out_of_range_gains_are_brought_into_range_and_rounded_to_the_step_and_not_a_number_is_ignored()
    {
        var settings = new RecordingSettings();
        var vm = Create(settings);

        vm.Bands[1].SetGainCommand.Execute(double.NaN);
        Assert.Null(settings.Current.EqualizerGains);

        vm.Bands[1].SetGainCommand.Execute(40.0);
        Assert.Equal(12, vm.Bands[1].Gain);
        vm.Preamp.SetGainCommand.Execute(-0.3);
        Assert.Equal(-0.5, vm.Preamp.Gain);
        vm.Preamp.SetGainCommand.Execute(double.NegativeInfinity);
        Assert.Equal(-0.5, vm.Preamp.Gain);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void With_64_presets_saved_a_new_name_is_refused_but_one_already_saved_is_replaced()
    {
        var presets = Enumerable.Range(0, EqualizerSettings.MaxUserPresets).ToDictionary(i => $"p{i}", _ => new double[10]);
        var settings = new RecordingSettings(new AppSettings { EqualizerPresets = presets, EqualizerGains = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0] });
        var vm = Create(settings);

        vm.NewPresetName = "another";
        vm.SavePresetCommand.Execute(null);
        Assert.Equal("At most 64 presets can be saved; delete one first.", vm.PresetProblem);
        Assert.Equal("another", vm.NewPresetName); // kept, to try another name

        vm.NewPresetName = "p3";
        vm.SavePresetCommand.Execute(null);
        Assert.Null(vm.PresetProblem);
        Assert.Equal(1, settings.Current.EqualizerPresets!["p3"][0]);
        Assert.Equal(EqualizerSettings.MaxUserPresets, settings.Current.EqualizerPresets.Count);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_preset_gone_meanwhile_is_neither_applied_nor_deleted_and_a_built_in_one_is_never_deleted()
    {
        var settings = new RecordingSettings(new AppSettings { EqualizerPreset = "rock", EqualizerGains = [.. EqualizerSettings.BuiltIn("rock")!.Gains] });
        var vm = Create(settings);
        Assert.False(vm.IsUserPreset);
        Assert.False(vm.AskDeletePresetCommand.CanExecute(null));

        vm.ChoosePreset("gone");
        vm.DeletePreset("gone");
        vm.DeletePreset("rock");

        Assert.Equal("rock", vm.Preset);
        Assert.Null(settings.Current.EqualizerPresets); // nothing written
    }

    [Fact]
    public void Reset_with_the_equalizer_off_leaves_it_off()
    {
        var settings = new RecordingSettings(new AppSettings { EqualizerEnabled = false, EqualizerGains = [.. EqualizerSettings.BuiltIn("rock")!.Gains], EqualizerPreset = "rock", EqualizerPreampDb = 2 });
        var vm = Create(settings);

        vm.ResetCommand.Execute(null);

        Assert.False(vm.Enabled);
        Assert.Equal(("flat", 0.0, false), (settings.Current.EqualizerPreset, settings.Current.EqualizerPreampDb, settings.Current.EqualizerEnabled));
        Assert.All(settings.Current.EqualizerGains!, g => Assert.Equal(0, g));
    }

    [Fact]
    public void A_drag_saves_once_when_it_ends()
    {
        var settings = new RecordingSettings();
        var vm = Create(settings);

        vm.Bands[3].Scrub = 1;
        vm.Bands[3].SetGainCommand.Execute(1.0);
        vm.Bands[3].SetGainCommand.Execute(2.0);
        Assert.Equal(0, settings.Updates);

        vm.Bands[3].Scrub = null;

        Assert.Equal(1, settings.Updates);
        Assert.Equal(2, settings.Current.EqualizerGains![3]);
    }

    public async ValueTask DisposeAsync()
    {
        await _playerVm.DisposeAsync();
        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }

    private sealed class RecordingSettings(AppSettings? initial = null) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initial ?? new();

        public int Updates { get; private set; }

        public void Update(Func<AppSettings, AppSettings> change)
        {
            Current = change(Current);
            Updates++;
        }
    }

    private sealed class UnwritableSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public void Update(Func<AppSettings, AppSettings> change)
            => throw new MusicSpinException(MusicSpinErrorCode.DataDirectoryNotWritable, "the data folder is read-only.");
    }
}
