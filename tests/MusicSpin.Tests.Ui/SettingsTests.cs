using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The Settings dialog in the window (#22).</summary>
public sealed class SettingsTests
{
    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark, AppSettings? settings = null)
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, 24).Select(StubLibrary.Album));
        return await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Theme = theme,
            Library = library,
            Covers = new StubCovers(),
            Settings = settings ?? new AppSettings(),
        });
    }

    private static T Find<T>(AppHarness app, string name)
        where T : Control => Gui.Find<T>(app.Window, name);

    private static void OpenSettings(AppHarness app)
    {
        Gui.Click(app.Window, Find<Button>(app, "SettingsButton"));
        Assert.True(app.ViewModel.Settings.IsOpen);
    }

    /// <summary>The segment or card showing <paramref name="label"/> in the list <paramref name="list"/>.</summary>
    private static ListBoxItem Item(AppHarness app, string list, string label)
        => Gui.All<ListBoxItem>(Find<ListBox>(app, list)).Single(i => i.DataContext switch
        {
            Choice c => c.Label == label,
            DeckDesign d => d.Name == label,
            _ => false,
        });

    private static void PressAt(AppHarness app, Point point)
    {
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        app.Window.MouseUp(point, MouseButton.Left);
        Gui.Settle();
    }

    private static void Key(AppHarness app, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        app.Window.KeyPress(key, modifiers, physical, null);
        app.Window.KeyRelease(key, modifiers, physical, null);
        Gui.Settle();
    }

    private static IInputElement? Focused(AppHarness app) => app.Window.FocusManager!.GetFocusedElement();

    [AvaloniaFact]
    public async Task The_cog_opens_Settings_and_shows_as_pressed_while_it_is_open()
    {
        await using var app = await OpenAsync();
        var cog = Find<Button>(app, "SettingsButton");
        Assert.False(Find<Panel>(app, "SettingsLayer").IsVisible);

        OpenSettings(app);

        Assert.True(Find<Panel>(app, "SettingsLayer").IsVisible);
        Assert.Contains("on", cog.Classes);
        Assert.Same(Find<Button>(app, "CloseSettings"), Focused(app));

        Gui.Click(app.Window, Find<Button>(app, "CloseSettings"));

        Assert.False(app.ViewModel.Settings.IsOpen);
        Assert.False(Find<Panel>(app, "SettingsLayer").IsVisible);
        Assert.DoesNotContain("on", cog.Classes);
        Assert.Same(cog, Focused(app)); // back where it was
    }

    [AvaloniaFact]
    public async Task Esc_and_a_click_outside_close_it_but_a_click_inside_does_not()
    {
        await using var app = await OpenAsync();
        OpenSettings(app);
        var dialog = Gui.BoundsIn(app.Window, Find<Border>(app, "Dialog"));

        PressAt(app, dialog.TopLeft + new Point(6, 6));
        Assert.True(app.ViewModel.Settings.IsOpen);

        PressAt(app, new Point(dialog.Left / 2, dialog.Center.Y));
        Assert.False(app.ViewModel.Settings.IsOpen);

        OpenSettings(app);
        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.Settings.IsOpen);
    }

    [AvaloniaFact]
    public async Task Shortcuts_behind_it_are_off_while_it_is_open()
    {
        await using var app = await OpenAsync();
        app.Scripted!.Status = ScriptedPlayer.Playing("Blue in Green", position: 60);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.IsPlaying, "playing");
        OpenSettings(app);
        Find<ScrubBar>(app, "TiltBar").Focus();
        Gui.Settle();

        Key(app, Avalonia.Input.Key.Space, PhysicalKey.Space);
        Key(app, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.Control);

        Assert.Equal((0, 0, 0), (app.Scripted.Count("toggle"), app.Scripted.Count("seek"), app.Scripted.Count("volume")));
        Assert.True(app.ViewModel.Settings.IsOpen);

        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Key(app, Avalonia.Input.Key.Space, PhysicalKey.Space);
        await Gui.UntilAsync(() => app.Scripted.Count("toggle") == 1, "Space toggles again");
    }

    [AvaloniaFact]
    public async Task Each_setting_applies_at_once_and_is_saved()
    {
        await using var app = await OpenAsync(settings: new AppSettings { Volume = 0.7 });
        OpenSettings(app);
        try
        {
            Gui.Click(app.Window, Item(app, "ThemeChoice", "Light"));
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

            Gui.Click(app.Window, Item(app, "MotionChoice", "Fast"));
            Assert.Equal(0.5, MotionScale.Current.Multiplier);
            Assert.Equal(0.5, app.ViewModel.SpinClock.MotionMultiplier);

            Gui.Click(app.Window, Item(app, "SpeedChoice", "45"));
            Assert.Equal(45, app.ViewModel.SpinClock.Rpm);

            var classic = Item(app, "DesignChoice", "Classic");
            Gui.Click(app.Window, classic);
            Assert.True(classic.IsSelected);
            Assert.IsType<ClassicDeck>(app.ViewModel.Settings.Design);
            Assert.Single(Gui.All<Panel>(classic), p => p.Classes.Contains("deckPreview"));

            var tilt = Find<ScrubBar>(app, "TiltBar");
            tilt.Focus();
            Key(app, Avalonia.Input.Key.Home, PhysicalKey.Home);
            Key(app, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal("21°", Find<TextBlock>(app, "TiltText").Text);
            Key(app, Avalonia.Input.Key.End, PhysicalKey.End);
            Key(app, Avalonia.Input.Key.PageDown, PhysicalKey.PageDown);
            Assert.Equal("70°", Find<TextBlock>(app, "TiltText").Text);

            var depth = Find<ScrubBar>(app, "DepthBar");
            var bounds = Gui.BoundsIn(app.Window, depth);
            PressAt(app, new Point(bounds.X + (bounds.Width / 4), bounds.Center.Y));
            Assert.InRange(app.ViewModel.Settings.ShelfDepth, 24, 26);
            Assert.Equal(app.ViewModel.Settings.ShelfDepthText, Find<TextBlock>(app, "DepthText").Text);

            var saved = new SettingsStore(app.Data).Load();
            Assert.Equal(("light", "fast", 45.0, "classic"), (saved.Theme, saved.Motion, saved.RecordSpeedRpm, saved.TurntableDesign));
            Assert.Equal((70.0, app.ViewModel.Settings.ShelfDepth, 0.7), (saved.ShelfTilt, saved.ShelfDepth, saved.Volume));
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public async Task A_new_window_shows_the_saved_settings()
    {
        await using var app = await OpenAsync(AppTheme.Light, new AppSettings { RecordSpeedRpm = 16.667, ShelfTilt = 33, ShelfDepth = 0 });
        OpenSettings(app);

        Assert.True(Item(app, "ThemeChoice", "Light").IsSelected);
        Assert.True(Item(app, "MotionChoice", "Smooth").IsSelected);
        Assert.True(Item(app, "SpeedChoice", "16⅔").IsSelected);
        Assert.True(Item(app, "DesignChoice", "Classic").IsSelected);
        Assert.Equal(("33°", "0"), (Find<TextBlock>(app, "TiltText").Text, Find<TextBlock>(app, "DepthText").Text));
        Assert.Equal((33, 0), (Find<ScrubBar>(app, "TiltBar").Value, Find<ScrubBar>(app, "DepthBar").Value));
    }

    [AvaloniaFact]
    public async Task Opening_plays_the_dialog_in_unless_Motion_is_None()
    {
        await using var app = await OpenAsync();
        try
        {
            OpenSettings(app);
            var dialog = Find<Border>(app, "Dialog");
            Assert.Equal(1, Animate.GetPlayed(dialog));

            app.ViewModel.Settings.SetMotion(MotionMode.None);
            app.ViewModel.Settings.Close();
            app.ViewModel.Settings.Open();
            Gui.Settle();
            Assert.Equal(1, Animate.GetPlayed(dialog));
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_Settings_over_the_albums(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        app.ViewModel.Settings.Open();
        Gui.Settle();

        Snapshots.Match(app.Window, $"settings-{theme.ToString().ToLowerInvariant()}");
    }
}
