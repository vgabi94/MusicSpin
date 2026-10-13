using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The window shell (#6): layout, navigation, Esc and Space, themes, motion, settings, the banner.</summary>
public sealed class ShellTests
{
    [AvaloniaFact]
    public async Task Regions_have_the_mockup_sizes_at_1440_by_880()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        Assert.Equal(new Rect(0, 0, 1440, 44), Gui.BoundsIn(window, Gui.Find<TitleBar>(window, "TitleBarView")));
        Assert.Equal(new Rect(0, 44, 220, 880 - 44 - 84), Gui.BoundsIn(window, Gui.Find<SidebarView>(window, "SidebarView")));
        Assert.Equal(new Rect(220, 44, 1440 - 220, 880 - 44 - 84), Gui.BoundsIn(window, Gui.Find<DockPanel>(window, "ContentArea")));
        Assert.Equal(new Rect(0, 880 - 84, 1440, 84), Gui.BoundsIn(window, Gui.Find<PlayerBar>(window, "PlayerBarView")));
    }

    [AvaloniaFact]
    public async Task The_window_draws_only_its_own_title_bar_and_the_system_buttons()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        // Fluent's drawn decorations add a second title and a fullscreen button over ours (Styles/Window.axaml).
        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(44, window.ExtendClientAreaTitleBarHeightHint);
        Assert.True(Application.Current!.TryGetResource("ShellDecorations", null, out var theme));
        Assert.Same(theme, window.WindowDecorationsTheme);
    }

    [AvaloniaFact]
    public async Task The_window_does_not_go_below_1280_wide()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Width = 1000, Height = 600 });
        var window = app.Window;

        Assert.Equal(1280, window.MinWidth);
        Assert.Equal(720, window.MinHeight);
        Assert.True(window.Bounds.Width >= 1280, $"width {window.Bounds.Width}");
        Assert.True(Gui.Find<DockPanel>(window, "ContentArea").Bounds.Width >= 1280 - 220);
    }

    [AvaloniaFact]
    public async Task Clicking_a_sidebar_item_shows_its_page_and_highlights_it()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var vm = app.ViewModel;

        foreach (var (title, page, view) in new (string, PageViewModel, Type)[]
                 {
                     ("Tracks", vm.TracksPage, typeof(TracksPage)),
                     ("Radio", vm.RadioPage, typeof(RadioPage)),
                     ("Albums", vm.AlbumsPage, typeof(AlbumsPage)),
                 })
        {
            var button = NavButton(window, title);
            Gui.Click(window, button);

            Assert.Same(page, vm.CurrentPage);
            Assert.Equal(title, Gui.Find<TextBlock>(window, "PageTitle").Text);
            Assert.IsType(view, Gui.Find<ContentControl>(window, "Page").Presenter?.Child);
            Assert.Equal([title], Gui.All<Button>(window).Where(b => b.Classes.Contains("nav") && b.Classes.Contains("active"))
                .Select(b => ((NavItemViewModel)b.DataContext!).Title));
        }
    }

    [AvaloniaFact]
    public async Task Playlists_appear_in_the_sidebar_and_open_their_page()
    {
        var library = new StubLibrary();
        library.Playlists.Add(new PlaylistSummaryItem("pl1", "Evening", 2));
        library.PlaylistEntries["pl1"] = [StubLibrary.Track(1), StubLibrary.Track(2)];
        await using var app = await AppHarness.OpenAsync(new AppOptions { Library = library, Covers = new StubCovers() });
        var window = app.Window;

        Gui.Click(window, NavButton(window, "Evening"));
        await Gui.UntilAsync(() => app.ViewModel.CurrentPage.CountText == "2", "the playlist loaded");

        var page = Assert.IsType<PlaylistPageViewModel>(app.ViewModel.CurrentPage);
        Assert.Equal("Evening", page.Title);
        Assert.IsType<TracksPage>(Gui.Find<ContentControl>(window, "Page").Presenter?.Child);
        Assert.True(Gui.Find<Button>(window, "PlayAll").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_topmost_layer_first()
    {
        await using var app = await AppHarness.OpenAsync();
        var layers = new[]
        {
            new StubLayer("now playing", LayerOrder.NowPlaying), new StubLayer("queue", LayerOrder.QueueDrawer),
            new StubLayer("settings", LayerOrder.Settings), new StubLayer("menu", LayerOrder.Menu),
        };
        foreach (var layer in layers.Reverse())
        {
            app.ViewModel.Layers.Register(layer);
        }

        var closed = new List<string>();
        foreach (var layer in layers)
        {
            layer.Closed += (_, _) => closed.Add(layer.Name);
        }

        for (var i = 0; i < 5; i++)
        {
            Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        }

        Assert.Equal(["menu", "settings", "queue", "now playing"], closed);
    }

    [AvaloniaFact]
    public async Task Space_toggles_play_and_pause_but_types_in_the_search_box()
    {
        await using var app = await AppHarness.OpenAsync();
        var paths = new[] { Path.GetFullPath("/music/ui/space.flac") };
        app.Player.AddTracks(paths);
        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("queue.set", new { items = paths, play = true }, TestContext.Current.CancellationToken);
        }

        var player = app.ViewModel.Player;
        await Gui.UntilAsync(() => player.State == PlayerState.Playing, "playing");

        Gui.Press(app.Window, Key.Space, PhysicalKey.Space, " ");
        await Gui.UntilAsync(() => player.State == PlayerState.Paused, "paused by Space");

        var search = Gui.Find<TextBox>(app.Window, "SearchBox");
        search.Focus();
        Gui.Press(app.Window, Key.A, PhysicalKey.A, "a");
        Gui.Press(app.Window, Key.Space, PhysicalKey.Space, " ");
        Gui.Press(app.Window, Key.B, PhysicalKey.B, "b");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Gui.Settle();

        Assert.Equal("a b", search.Text);
        Assert.Equal("a b", app.ViewModel.SearchText);
        Assert.Equal(PlayerState.Paused, player.State);
    }

    [AvaloniaFact]
    public async Task Switching_the_theme_repaints_controls_already_on_screen()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var sidebar = Gui.All<Border>(window).Single(b => b.Classes.Contains("sidebar")); // no transitions on these brushes
        var dark = (Color(window.Background), Color(sidebar.BorderBrush), Color(Gui.Find<TextBlock>(window, "PageTitle").Foreground));

        app.ViewModel.SetTheme(AppTheme.Light);
        Gui.Settle();

        Assert.Equal((Rgb(0x0E0F12), Argb(0x12FFFFFF), Rgb(0xEEF0F4)), dark);
        Assert.Equal((Rgb(0xF4F2EE), Argb(0x14000000), Rgb(0x1B1C1F)),
            (Color(window.Background), Color(sidebar.BorderBrush), Color(Gui.Find<TextBlock>(window, "PageTitle").Foreground)));

        app.ViewModel.SetTheme(AppTheme.Dark);
        Gui.Settle();
        Assert.Equal(dark, (Color(window.Background), Color(sidebar.BorderBrush), Color(Gui.Find<TextBlock>(window, "PageTitle").Foreground)));
    }

    [AvaloniaTheory]
    [InlineData(MotionMode.Smooth, 0.2)]
    [InlineData(MotionMode.Fast, 0.1)]
    [InlineData(MotionMode.None, 0)]
    public async Task The_motion_setting_scales_animations(MotionMode motion, double seconds)
    {
        await using var app = await AppHarness.OpenAsync();
        app.ViewModel.SetMotion(motion);
        var transition = new DoubleTransition { Property = Visual.OpacityProperty };
        transition.Bind(TransitionBase.DurationProperty, (Avalonia.Data.BindingBase)new MotionExtension("HoverDuration").ProvideValue(null!));
        var sample = new Border { Transitions = [transition] };
        ((Panel)Gui.Find<Grid>(app.Window, "ShellGrid")).Children.Add(sample);
        Gui.Settle();

        Assert.Equal(TimeSpan.FromSeconds(seconds), transition.Duration);

        sample.Opacity = 0;
        app.Advance(TimeSpan.FromSeconds(seconds / 2));
        if (seconds > 0)
        {
            Assert.InRange(sample.Opacity, 0.05, 0.95); // half way
        }

        app.Advance(TimeSpan.FromSeconds((seconds / 2) + 0.05));
        Assert.Equal(0, sample.Opacity, 3);
    }

    [AvaloniaFact]
    public async Task Settings_are_read_at_start_and_written_on_change()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions
        {
            Theme = AppTheme.Light,
            Settings = new AppSettings { Motion = "fast", RecordSpeedRpm = 45, Volume = 0.7 },
        });
        var vm = app.ViewModel;

        Assert.Equal(AppTheme.Light, vm.Theme);
        Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        Assert.Equal(MotionMode.Fast, vm.Motion);
        Assert.Equal(0.5, MotionScale.Current.Multiplier);
        Assert.Equal(45, vm.SpinClock.Rpm);

        vm.SetTheme(AppTheme.Dark);
        vm.SetMotion(MotionMode.None);
        vm.SetRecordSpeed(16.667);

        var saved = new SettingsStore(app.Data).Load();
        Assert.Equal(("dark", "none", 16.667, 0.7), (saved.Theme, saved.Motion, saved.RecordSpeedRpm, saved.Volume));
    }

    [AvaloniaFact]
    public async Task The_startup_warning_shows_in_a_banner_until_dismissed()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { StartupWarning = "settings.json could not be read; using defaults." });
        var banner = Gui.Find<Border>(app.Window, "Banner");

        Assert.True(banner.IsVisible);
        Assert.Equal("settings.json could not be read; using defaults.", Gui.Find<TextBlock>(app.Window, "BannerText").Text);

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "DismissBanner"));

        Assert.False(banner.IsVisible);
    }

    [AvaloniaFact]
    public async Task There_is_no_banner_without_a_warning()
    {
        await using var app = await AppHarness.OpenAsync();

        Assert.False(Gui.Find<Border>(app.Window, "Banner").IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(1440, 880, AppTheme.Dark)]
    [InlineData(1440, 880, AppTheme.Light)]
    [InlineData(1280, 800, AppTheme.Dark)]
    [InlineData(1280, 800, AppTheme.Light)]
    public async Task Snapshot_of_the_empty_shell(double width, double height, AppTheme theme)
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Width = width, Height = height, Theme = theme });

        Snapshots.Match(app.Window, $"shell-{width}x{height}-{theme.ToString().ToLowerInvariant()}");
    }

    private static Button NavButton(Window window, string title)
        => Gui.All<Button>(window).Single(b => b.Classes.Contains("nav") && b.DataContext is NavItemViewModel item && item.Title == title);

    private static Color Color(IBrush? brush) => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color Rgb(uint rgb) => Avalonia.Media.Color.FromUInt32(0xFF000000 | rgb);

    private static Color Argb(uint argb) => Avalonia.Media.Color.FromUInt32(argb);

    private sealed class StubLayer(string name, int order) : ILayer
    {
        public event EventHandler? Closed;

        public string Name { get; } = name;

        public int Order { get; } = order;

        public bool IsOpen { get; private set; } = true;

        public void Close()
        {
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
