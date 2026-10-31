using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The Settings dialog in the window (#22), a sidebar of pages and the page chosen (#188).</summary>
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

    private static readonly string[] AllPages = ["Appearance", "Turntable", "Shelf", "Library", "Audio", "Equalizer"];

    /// <summary>The sidebar entry of the page titled <paramref name="title"/>.</summary>
    private static ListBoxItem Entry(AppHarness app, string title)
        => Gui.All<ListBoxItem>(Find<ListBox>(app, "SettingsPages")).Single(i => i.DataContext is SettingsPage p && p.Title == title);

    /// <summary>Clicks the sidebar entry of <paramref name="title"/>, as the user chooses a page.</summary>
    private static void ShowPage(AppHarness app, string title)
    {
        Gui.Click(app.Window, Entry(app, title));
        Assert.Equal(title, app.ViewModel.Settings.SelectedPage.Title);
    }

    /// <summary>The title of the page shown (its heading).</summary>
    private static string? Shown(AppHarness app) => Find<TextBlock>(app, "SettingsPageTitle").Text;

    /// <summary>True when the control named <paramref name="name"/> is in the window now (only the page shown is).</summary>
    private static bool Has(AppHarness app, string name)
        => Gui.All<Control>(app.Window).Any(c => c.Name == name && c.IsEffectivelyVisible);

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
        Assert.Equal("Appearance", Shown(app)); // the first page, in a new window
        Assert.Same(Entry(app, "Appearance"), Focused(app)); // so ↑ ↓ move through the pages at once

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

        // from inside a page too: a slider has the focus
        OpenSettings(app);
        ShowPage(app, "Shelf");
        Find<ScrubBar>(app, "TiltBar").Focus();
        Gui.Settle();
        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.Settings.IsOpen);
    }

    [AvaloniaFact]
    public async Task The_sidebar_lists_a_page_per_section_by_name_and_shows_the_one_chosen()
    {
        await using var app = await OpenAsync();
        OpenSettings(app);

        var entries = Gui.All<ListBoxItem>(Find<ListBox>(app, "SettingsPages")).ToList();
        Assert.Equal(AllPages, entries.Select(e => ((SettingsPage)e.DataContext!).Title));
        Assert.Equal(AllPages, entries.Select(AutomationProperties.GetName)); // what a screen reader says
        Assert.Equal("Settings pages", AutomationProperties.GetName(Find<ListBox>(app, "SettingsPages")));
        Assert.True(Entry(app, "Appearance").IsSelected);
        Assert.True(Has(app, "ThemeChoice"));

        ShowPage(app, "Shelf");

        Assert.Equal("Shelf", Shown(app));
        Assert.True(Entry(app, "Shelf").IsSelected);
        Assert.False(Entry(app, "Appearance").IsSelected);
        Assert.True(Has(app, "TiltBar"));
        Assert.False(Has(app, "ThemeChoice")); // one page at a time

        foreach (var (title, control) in AllPages.Zip(new[] { "MotionChoice", "DesignChoice", "DepthBar", "AddFolder", "ExclusiveMode", "EqualizerSwitch" }))
        {
            ShowPage(app, title);
            Assert.Equal(title, Shown(app));
            Assert.True(Has(app, control), $"{title} shows {control}");
        }
    }

    [AvaloniaFact]
    public async Task Arrow_keys_move_through_the_pages_and_stop_at_either_end()
    {
        await using var app = await OpenAsync();
        OpenSettings(app);
        var settings = app.ViewModel.Settings;

        Key(app, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(("Turntable", "Turntable"), (settings.SelectedPage.Title, Shown(app)));
        Assert.Same(Entry(app, "Turntable"), Focused(app));
        Assert.True(Has(app, "DesignChoice"));

        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal("Appearance", Shown(app));
        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp); // already the first
        Assert.Equal("Appearance", Shown(app));
        Assert.Same(Entry(app, "Appearance"), Focused(app));

        Key(app, Avalonia.Input.Key.End, PhysicalKey.End);
        Assert.Equal("Equalizer", Shown(app));
        Key(app, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown); // already the last
        Assert.Equal("Equalizer", Shown(app));

        Key(app, Avalonia.Input.Key.Home, PhysicalKey.Home);
        Assert.Equal("Appearance", Shown(app));
        Assert.True(settings.IsOpen);
    }

    [AvaloniaFact]
    public async Task Tab_goes_from_the_sidebar_into_the_page_then_to_close_and_back_round()
    {
        await using var app = await OpenAsync();
        OpenSettings(app);
        var page = Find<ContentControl>(app, "SettingsPage");

        Key(app, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
        Assert.True(Focused(app) is Visual first && Find<ListBox>(app, "ThemeChoice").IsVisualAncestorOf(first), $"focus on {Focused(app)}");

        Key(app, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
        Assert.True(Focused(app) is Visual second && Find<ListBox>(app, "MotionChoice").IsVisualAncestorOf(second), $"focus on {Focused(app)}");

        Key(app, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
        Assert.Same(Find<Button>(app, "CloseSettings"), Focused(app)); // after the page's settings

        Key(app, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
        Assert.Same(Entry(app, "Appearance"), Focused(app)); // round inside the dialog

        Key(app, Avalonia.Input.Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Same(Find<Button>(app, "CloseSettings"), Focused(app));
        Assert.False(page.IsVisualAncestorOf((Visual)Focused(app)!));
        Assert.True(app.ViewModel.Settings.IsOpen);
    }

    [AvaloniaFact]
    public async Task It_reopens_on_the_page_last_used_but_a_new_window_starts_on_the_first()
    {
        await using (var app = await OpenAsync())
        {
            OpenSettings(app);
            ShowPage(app, "Audio");
            Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);

            OpenSettings(app);

            Assert.Equal("Audio", Shown(app));
            Assert.Same(Entry(app, "Audio"), Focused(app));
            Assert.True(Has(app, "ExclusiveMode"));
        }

        await using var next = await OpenAsync(); // not saved: the app starting again opens on the first page
        OpenSettings(next);
        Assert.Equal("Appearance", Shown(next));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_page_that_cannot_load_says_why_and_marks_its_entry_until_Try_again_works()
    {
        await using var app = await OpenAsync();
        var library = (StubLibrary)app.Services.Library;
        library.Failures[nameof(StubLibrary.FoldersAsync)] = StubLibrary.Corrupt();
        OpenSettings(app);
        var page = app.ViewModel.Settings.Pages.Single(p => p.Title == "Library");
        await Gui.UntilAsync(() => page.HasProblem, "the Library page's problem");

        // on another page: only the entry says so
        Assert.Equal("Appearance", Shown(app));
        Assert.True(Gui.All<TextBlock>(Entry(app, "Library")).Single(t => t.Classes.Contains("pageProblem")).IsEffectivelyVisible);
        Assert.False(Gui.All<TextBlock>(Entry(app, "Shelf")).Single(t => t.Classes.Contains("pageProblem")).IsVisible);
        Assert.False(Find<StackPanel>(app, "PageProblem").IsVisible);
        Assert.Null(app.ViewModel.Banner); // the page says it, not a notice

        ShowPage(app, "Library");
        Assert.True(Find<StackPanel>(app, "PageProblem").IsEffectivelyVisible);
        Assert.Equal("Library couldn't load", Gui.All<TextBlock>(Find<StackPanel>(app, "PageProblem")).First().Text);
        Assert.Equal(StubLibrary.Corrupt().Message, Find<TextBlock>(app, "ProblemText").Text);
        Assert.False(Has(app, "AddFolder"));

        library.Failures.Clear();
        Gui.Click(app.Window, Find<Button>(app, "TryAgain"));
        await Gui.UntilAsync(() => !page.HasProblem, "the page loaded");

        Assert.False(Find<StackPanel>(app, "PageProblem").IsVisible);
        Assert.True(Has(app, "AddFolder"));
        Assert.False(Gui.All<TextBlock>(Entry(app, "Library")).Single(t => t.Classes.Contains("pageProblem")).IsVisible);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_page_that_failed_loads_again_when_Settings_reopens()
    {
        await using var app = await OpenAsync();
        var library = (StubLibrary)app.Services.Library;
        library.Failures[nameof(StubLibrary.FoldersAsync)] = StubLibrary.Corrupt();
        OpenSettings(app);
        var page = app.ViewModel.Settings.Pages.Single(p => p.Title == "Library");
        await Gui.UntilAsync(() => page.HasProblem, "the Library page's problem");
        app.ViewModel.Settings.Close();

        library.Failures.Clear();
        var reads = library.ReadsOf(nameof(StubLibrary.FoldersAsync));
        OpenSettings(app);

        await Gui.UntilAsync(() => library.ReadsOf(nameof(StubLibrary.FoldersAsync)) > reads && !page.HasProblem, "read again");
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public void Without_the_library_or_audio_view_models_their_pages_are_left_out()
    {
        // A host without them (as before #31 / #32, or tests of the other sections): the rest still works.
        var root = TempDir.Create("settings-pages");
        using var spinClock = new SpinClock(new FakeTimeProvider(), new QueueDispatcher());
        var window = new Window { Width = 1000, Height = 800 };
        try
        {
            var data = new DataDirectory(System.IO.Path.Combine(root, "data"), DataDirectorySource.Override);
            var settings = new SettingsViewModel(new SettingsService(data), new StubAppearance(), spinClock, new Notices());
            window.Content = new SettingsDialog { DataContext = settings };
            window.Show();
            settings.Open();
            Gui.Settle();

            var entries = Gui.All<ListBoxItem>(Gui.Find<ListBox>(window, "SettingsPages")).ToList();
            Assert.Equal(["Appearance", "Turntable", "Shelf"], entries.Select(i => ((SettingsPage)i.DataContext!).Title));

            Gui.Click(window, entries[^1]);
            Assert.Equal("Shelf", Gui.Find<TextBlock>(window, "SettingsPageTitle").Text);
            Assert.True(Gui.Find<ScrubBar>(window, "TiltBar").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            TempDir.TryDelete(root);
        }
    }

    [AvaloniaFact]
    public async Task Shortcuts_behind_it_are_off_while_it_is_open()
    {
        await using var app = await OpenAsync();
        app.Scripted!.Status = ScriptedPlayer.Playing("Blue in Green", position: 60);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.IsPlaying, "playing");
        OpenSettings(app);
        Key(app, Avalonia.Input.Key.Space, PhysicalKey.Space); // on a sidebar entry
        ShowPage(app, "Shelf");
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

            ShowPage(app, "Turntable");
            Gui.Click(app.Window, Item(app, "SpeedChoice", "45"));
            Assert.Equal(45, app.ViewModel.SpinClock.Rpm);

            var classic = Item(app, "DesignChoice", "Classic");
            Gui.Click(app.Window, classic);
            Assert.True(classic.IsSelected);
            Assert.IsType<ClassicDeck>(app.ViewModel.Settings.Design);
            Assert.Single(Gui.All<Panel>(classic), p => p.Classes.Contains("deckPreview"));

            var vinyl = Find<ToggleButton>(app, "VinylTransition"); // #197: on by default
            Assert.True(vinyl.IsChecked);
            vinyl.BringIntoView();
            Gui.Settle();
            Gui.Click(app.Window, vinyl);
            Assert.False(app.ViewModel.Settings.VinylTransition);

            ShowPage(app, "Shelf");
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
            Assert.False(saved.VinylTransition);
            Assert.Equal((70.0, app.ViewModel.Settings.ShelfDepth, 0.7), (saved.ShelfTilt, saved.ShelfDepth, saved.Volume));
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    /// <summary>The whole bar, left to right at its vertical middle, in steps of <paramref name="steps"/> moves.</summary>
    private static IReadOnlyList<Point> Across(AppHarness app, string bar, int steps)
    {
        app.ShowSettingsPage("Shelf");
        var bounds = Gui.BoundsIn(app.Window, Find<ScrubBar>(app, bar));
        return [.. Enumerable.Range(0, steps + 1).Select(i => new Point(bounds.X + 2 + ((bounds.Width - 4) * i / steps), bounds.Center.Y))];
    }

    [AvaloniaFact]
    public async Task Dragging_a_slider_applies_each_step_but_saves_only_where_it_is_let_go()
    {
        await using var app = await OpenAsync(settings: new AppSettings { ShelfDepth = 40, Volume = 0.7 });
        OpenSettings(app);
        var before = File.ReadAllText(app.Data.SettingsFile);
        var points = Across(app, "DepthBar", 20);

        app.Window.MouseMove(points[0]);
        app.Window.MouseDown(points[0], MouseButton.Left);
        var seen = new HashSet<double>();
        foreach (var point in points.Skip(1).Take(10))
        {
            app.Window.MouseMove(point);
            Gui.Settle();
            seen.Add(app.ViewModel.Settings.ShelfDepth);
            Assert.Equal(app.ViewModel.Settings.ShelfDepthText, Find<TextBlock>(app, "DepthText").Text); // applies live
            Assert.Equal(before, File.ReadAllText(app.Data.SettingsFile)); // nothing written mid-drag
        }

        Assert.True(seen.Count > 5, $"the drag moved through {seen.Count} values");
        var last = app.ViewModel.Settings.ShelfDepth;
        app.Window.MouseUp(points[10], MouseButton.Left);
        Gui.Settle();

        var saved = new SettingsStore(app.Data).Load();
        Assert.Equal((last, 0.7), (saved.ShelfDepth, saved.Volume));
        Assert.InRange(last, 45, 55); // half way along
    }

    [AvaloniaFact]
    public async Task Closing_the_window_mid_drag_saves_the_value_shown()
    {
        var app = await OpenAsync(settings: new AppSettings { ShelfTilt = 58 });
        await using var _ = app;
        OpenSettings(app);
        var points = Across(app, "TiltBar", 10);
        app.Window.MouseMove(points[0]);
        app.Window.MouseDown(points[0], MouseButton.Left);
        app.Window.MouseMove(points[3]);
        Gui.Settle();
        var shown = app.ViewModel.Settings.ShelfTilt;
        Assert.NotEqual(58, shown);
        Assert.Equal(58, new SettingsStore(app.Data).Load().ShelfTilt);

        await app.CloseAsync(); // still holding the button

        Assert.Equal(shown, new SettingsStore(app.Data).Load().ShelfTilt);
    }

    [AvaloniaFact]
    public async Task Shutting_down_with_a_drag_unsaved_saves_it()
    {
        // However the window goes (the OS may not end the pointer capture first), shutting down saves what a drag left.
        await using var app = await OpenAsync(settings: new AppSettings { ShelfDepth = 40 });
        var settings = app.ViewModel.Settings;
        settings.DepthScrub = 80; // mid-drag, as the bar reports it
        settings.SetShelfDepth(80);
        Assert.Equal(40, new SettingsStore(app.Data).Load().ShelfDepth);

        await app.ViewModel.ShutdownAsync();

        Assert.Equal(80, new SettingsStore(app.Data).Load().ShelfDepth);
    }

    [AvaloniaFact]
    public async Task Closing_the_dialog_mid_drag_saves_the_value_shown()
    {
        await using var app = await OpenAsync(settings: new AppSettings { ShelfTilt = 58 });
        OpenSettings(app);
        var points = Across(app, "TiltBar", 10);
        app.Window.MouseMove(points[0]);
        app.Window.MouseDown(points[0], MouseButton.Left);
        app.Window.MouseMove(points[8]);
        Gui.Settle();
        var shown = app.ViewModel.Settings.ShelfTilt;

        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Gui.Settle();

        Assert.False(app.ViewModel.Settings.IsOpen);
        Assert.Equal(shown, new SettingsStore(app.Data).Load().ShelfTilt);
    }

    [AvaloniaFact]
    public async Task A_new_window_shows_the_saved_settings()
    {
        await using var app = await OpenAsync(AppTheme.Light, new AppSettings { RecordSpeedRpm = 16.667, ShelfTilt = 33, ShelfDepth = 0 });
        OpenSettings(app);

        Assert.True(Item(app, "ThemeChoice", "Light").IsSelected);
        Assert.True(Item(app, "MotionChoice", "Smooth").IsSelected);
        ShowPage(app, "Turntable");
        Assert.True(Item(app, "SpeedChoice", "16⅔").IsSelected);
        Assert.True(Item(app, "DesignChoice", "Classic").IsSelected);
        ShowPage(app, "Shelf");
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

    /// <summary>The pages with nothing to read; Library, Audio and Equalizer have theirs next to their tests.</summary>
    [AvaloniaTheory]
    [InlineData("Turntable", AppTheme.Dark)]
    [InlineData("Turntable", AppTheme.Light)]
    [InlineData("Shelf", AppTheme.Dark)]
    [InlineData("Shelf", AppTheme.Light)]
    public async Task Snapshot_of_a_Settings_page(string page, AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        app.ShowSettingsPage(page);
        app.ViewModel.Settings.Open();
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, Find<Border>(app, "Dialog"), $"settings-{page.ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_a_Settings_page_that_cannot_load(AppTheme theme)
    {
        await using var app = await OpenAsync(theme);
        ((StubLibrary)app.Services.Library).Failures[nameof(StubLibrary.FoldersAsync)] = StubLibrary.Corrupt();
        app.ShowSettingsPage("Library");
        app.ViewModel.Settings.Open();
        await Gui.UntilAsync(() => app.ViewModel.Settings.SelectedPage.HasProblem, "the problem");
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, Find<Border>(app, "Dialog"), $"settings-problem-{theme.ToString().ToLowerInvariant()}");
    }
}
