using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Composition;
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

/// <summary>
/// The startup splash screen (#226): layout, theme/motion handling, startup ordering before slow work, handoff to
/// <see cref="MainWindow"/>, early close cancellation, readiness timeout, and startup failures.
/// </summary>
public sealed class SplashTests
{
    [AvaloniaFact]
    public void The_splash_window_shows_the_app_icon_name_and_loading_bar_without_chrome()
    {
        RenderClock.KeepRunning();
        var vm = new SplashViewModel();
        var splash = new SplashWindow { DataContext = vm };
        try
        {
            splash.Show();
            Gui.Settle();

            Assert.Equal(WindowDecorations.None, splash.WindowDecorations);
            Assert.False(splash.CanResize);
            Assert.Equal(WindowStartupLocation.CenterScreen, splash.WindowStartupLocation);
            Assert.False(splash.ShowInTaskbar);
            Assert.False(splash.Topmost);
            Assert.Equal(320, splash.Bounds.Width);
            Assert.Equal(210, splash.Bounds.Height);
            Assert.NotNull(splash.Icon);
            Assert.Equal("Music Spin", splash.Title);

            var icon = Gui.Find<Image>(splash, "SplashIcon");
            var name = Gui.Find<TextBlock>(splash, "SplashName");
            var loader = Gui.Find<LoadingBar>(splash, "SplashLoader");
            Assert.NotNull(icon.Source);
            Assert.Equal(new Size(64, 64), icon.Bounds.Size);
            Assert.Equal("Music Spin", name.Text);
            Assert.True(loader.IsLoading);
            Assert.True(loader.IsSweeping);
            Assert.Equal("Loading", AutomationProperties.GetName(loader));
        }
        finally
        {
            if (splash.IsVisible)
            {
                splash.Close();
            }

            Gui.Settle();
        }
    }

    [AvaloniaFact]
    public void The_loading_bar_respects_motion_and_stops_when_the_splash_closes()
    {
        RenderClock.KeepRunning();
        var splash = new SplashWindow { DataContext = new SplashViewModel() };
        try
        {
            splash.Show();
            Gui.Settle();
            var loader = Gui.Find<LoadingBar>(splash, "SplashLoader");
            Assert.True(loader.IsSweeping);

            UiTime.AdvanceAnimations(TimeSpan.FromMilliseconds(350));
            Assert.NotEqual(0.5, loader.Phase);

            MotionScale.Current.Set(MotionMode.None);
            Gui.Settle();
            Assert.False(loader.IsSweeping);
            Assert.Equal(0.5, loader.Phase);

            MotionScale.Current.Set(MotionMode.Fast);
            Gui.Settle();
            Assert.True(loader.IsSweeping);

            splash.Close();
            Gui.Settle();
            Assert.False(loader.IsSweeping);
        }
        finally
        {
            if (splash.IsVisible)
            {
                splash.Close();
            }

            MotionScale.Current.Set(MotionMode.Smooth);
            Gui.Settle();
        }
    }

    [AvaloniaFact]
    public void ApplyInitialAppearance_applies_the_saved_theme_and_motion_before_the_splash_opens()
    {
        RenderClock.KeepRunning();
        var app = Application.Current!;
        try
        {
            AppStartup.ApplyInitialAppearance(app, new AppSettings { Theme = "light", Motion = "none" });
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, app.RequestedThemeVariant);
            Assert.Equal(0, MotionScale.Current.Multiplier);

            var splash = new SplashWindow { DataContext = new SplashViewModel() };
            try
            {
                splash.Show();
                Gui.Settle();
                var bg = Assert.IsType<ISolidColorBrush>(splash.Background, exactMatch: false).Color;
                Assert.Equal(Color.FromRgb(0xEB, 0xE8, 0xE2), bg);
                Assert.False(Gui.Find<LoadingBar>(splash, "SplashLoader").IsSweeping);
            }
            finally
            {
                if (splash.IsVisible)
                {
                    splash.Close();
                }

                Gui.Settle();
            }
        }
        finally
        {
            AppStartup.ApplyInitialAppearance(app, new AppSettings { Theme = "dark", Motion = "smooth" });
            Gui.Settle();
        }
    }

    [AvaloniaFact]
    public async Task The_splash_appears_before_slow_startup_work_and_closes_once_the_main_window_is_ready()
    {
        RenderClock.KeepRunning();
        await using var env = new StartupEnv();
        var albumsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Library.Albums.Add(StubLibrary.Album(1));
        env.Library.AlbumsGate = albumsGate;

        var lifetime = new FakeDesktopLifetime();
        var createdMain = false;
        SplashWindow? splash = null;
        MainWindow? main = null;

        try
        {
            var run = AppStartup.RunAsync(
                lifetime,
                () => splash = new SplashWindow { DataContext = new SplashViewModel() },
                () =>
                {
                    createdMain = true;
                    main = AppComposition.CreateMainWindow(env.Services);
                    return (env.Services, main);
                },
                w => ((MainWindowViewModel)w.DataContext!).InitializeAsync(),
                env.Time,
                env.Log);

            // Before the splash even opens: splash is the desktop main window, ShutdownMode is explicit, and no slow work started.
            Assert.NotNull(splash);
            Assert.Same(splash, lifetime.MainWindow);
            Assert.Equal(ShutdownMode.OnExplicitShutdown, lifetime.ShutdownMode);
            Assert.False(createdMain);
            Assert.Equal(0, env.Library.AlbumReads);

            // Simulate ClassicDesktopStyleApplicationLifetime showing the initial MainWindow.
            splash.Show();
            await Gui.UntilAsync(() => createdMain && env.Library.AlbumReads == 1, "startup work to begin after splash paint");

            // While the library read is still pending, splash stays visible and MainWindow stays hidden.
            Assert.NotNull(main);
            Assert.True(splash.IsVisible);
            Assert.False(main.IsVisible);
            Assert.Same(splash, lifetime.MainWindow);
            Assert.False(run.IsCompleted);

            // Once startup finishes, MainWindow opens already populated and splash closes.
            albumsGate.SetResult();
            await Gui.UntilAsync(() => run.IsCompleted, "startup handoff");
            await run;

            Assert.Same(main, lifetime.MainWindow);
            Assert.True(main.IsVisible);
            Assert.False(splash.IsVisible);
            Assert.False(((SplashViewModel)splash.DataContext!).IsLoading);
            Assert.Equal(ShutdownMode.OnMainWindowClose, lifetime.ShutdownMode);
            Assert.Equal(0, lifetime.ShutdownCalls);
            Assert.Single(((MainWindowViewModel)main.DataContext!).AlbumsPage.Albums);
        }
        finally
        {
            albumsGate.TrySetResult();
            await CloseIfOpenAsync(main);
            await CloseIfOpenAsync(splash);
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Closing_the_splash_before_the_main_window_is_built_exits_without_creating_it()
    {
        RenderClock.KeepRunning();
        await using var env = new StartupEnv();
        var lifetime = new FakeDesktopLifetime();
        var createdMain = false;
        SplashWindow? splash = null;

        var run = AppStartup.RunAsync(
            lifetime,
            () => splash = new SplashWindow { DataContext = new SplashViewModel() },
            () =>
            {
                createdMain = true;
                return (env.Services, AppComposition.CreateMainWindow(env.Services));
            },
            w => ((MainWindowViewModel)w.DataContext!).InitializeAsync(),
            env.Time,
            env.Log);

        Assert.NotNull(splash);
        splash.Show();
        // Close immediately before the Background-priority post runs.
        splash.Close();
        await Gui.UntilAsync(() => run.IsCompleted, "cancelled startup");
        await run;

        Assert.False(createdMain);
        Assert.False(splash.IsVisible);
        Assert.Equal(1, lifetime.ShutdownCalls);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task Closing_the_splash_during_initialization_cancels_startup_quits_the_player_and_never_shows_main_window()
    {
        RenderClock.KeepRunning();
        await using var env = new StartupEnv();
        var albumsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Library.AlbumsGate = albumsGate;

        var lifetime = new FakeDesktopLifetime();
        SplashWindow? splash = null;
        MainWindow? main = null;

        var run = AppStartup.RunAsync(
            lifetime,
            () => splash = new SplashWindow { DataContext = new SplashViewModel() },
            () =>
            {
                main = AppComposition.CreateMainWindow(env.Services);
                return (env.Services, main);
            },
            w => ((MainWindowViewModel)w.DataContext!).InitializeAsync(),
            env.Time,
            env.Log);

        splash!.Show();
        await Gui.UntilAsync(() => main is not null && ((MainWindowViewModel)main.DataContext!).Player.IsConnected && env.Library.AlbumReads == 1,
            "player connected while library read is still pending");
        var host = env.Player.Host!;
        Assert.True(((MainWindowViewModel)main!.DataContext!).Player.StartedPlayer);
        Assert.False(main.IsVisible);

        splash.Close();
        await Gui.UntilAsync(() => run.IsCompleted, "cancelled startup cleanup");
        await run;

        Assert.False(main.IsVisible);
        Assert.False(splash.IsVisible);
        Assert.Equal(1, lifetime.ShutdownCalls);
        Assert.Equal("quit", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        albumsGate.TrySetResult();
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_stalled_startup_opens_the_main_window_once_the_readiness_cap_elapses()
    {
        RenderClock.KeepRunning();
        await using var env = new StartupEnv();
        var albumsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Library.Albums.Add(StubLibrary.Album(1));
        env.Library.AlbumsGate = albumsGate;

        var lifetime = new FakeDesktopLifetime();
        SplashWindow? splash = null;
        MainWindow? main = null;

        try
        {
            var run = AppStartup.RunAsync(
                lifetime,
                () => splash = new SplashWindow { DataContext = new SplashViewModel() },
                () =>
                {
                    main = AppComposition.CreateMainWindow(env.Services);
                    return (env.Services, main);
                },
                w => ((MainWindowViewModel)w.DataContext!).InitializeAsync(),
                env.Time,
                env.Log,
                readinessTimeout: AppStartup.DefaultReadinessTimeout);

            splash!.Show();
            await Gui.UntilAsync(() => main is not null && env.Library.AlbumReads == 1, "blocked startup read");

            // Halfway through the cap: splash is still up.
            env.Time.Advance(AppStartup.DefaultReadinessTimeout / 2);
            Gui.Settle();
            Assert.False(run.IsCompleted);
            Assert.True(splash.IsVisible);
            Assert.False(main!.IsVisible);

            // Past the cap: MainWindow opens anyway so the user isn't trapped on the splash.
            env.Time.Advance(AppStartup.DefaultReadinessTimeout);
            await Gui.UntilAsync(() => run.IsCompleted, "readiness timeout handoff");
            await run;

            Assert.Same(main, lifetime.MainWindow);
            Assert.True(main.IsVisible);
            Assert.False(splash.IsVisible);
            Assert.Equal(ShutdownMode.OnMainWindowClose, lifetime.ShutdownMode);

            // Late-arriving library read still populates the now-visible MainWindow.
            albumsGate.SetResult();
            var vm = (MainWindowViewModel)main.DataContext!;
            await Gui.UntilAsync(() => vm.AlbumsPage.Albums.Count == 1, "late library population");
        }
        finally
        {
            albumsGate.TrySetResult();
            await CloseIfOpenAsync(main);
            await CloseIfOpenAsync(splash);
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_failure_creating_the_main_window_is_logged_closes_the_splash_and_shuts_down()
    {
        RenderClock.KeepRunning();
        await using var env = new StartupEnv();
        var lifetime = new FakeDesktopLifetime();
        SplashWindow? splash = null;

        var run = AppStartup.RunAsync(
            lifetime,
            () => splash = new SplashWindow { DataContext = new SplashViewModel() },
            () => throw new InvalidOperationException("boom during shell build"),
            static _ => Task.CompletedTask,
            env.Time,
            env.Log);

        splash!.Show();
        await Gui.UntilAsync(() => run.IsCompleted, "failed startup shutdown");
        await run;

        Assert.False(splash.IsVisible);
        Assert.Equal(1, lifetime.ShutdownCalls);
        Assert.True(File.Exists(env.Log.File!));
        var logText = File.ReadAllText(env.Log.File!);
        Assert.Contains("startup failed", logText, StringComparison.Ordinal);
        Assert.Contains("boom during shell build", logText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_faulted_initialize_task_is_logged_and_still_shows_the_main_window()
    {
        RenderClock.KeepRunning();
        await using var env = new StartupEnv(startupWarning: "corrupt settings.json");
        var lifetime = new FakeDesktopLifetime();
        SplashWindow? splash = null;
        MainWindow? main = null;

        try
        {
            var run = AppStartup.RunAsync(
                lifetime,
                () => splash = new SplashWindow { DataContext = new SplashViewModel() },
                () =>
                {
                    main = AppComposition.CreateMainWindow(env.Services);
                    return (env.Services, main);
                },
                static _ => Task.FromException(new InvalidOperationException("init failed")),
                env.Time,
                env.Log);

            splash!.Show();
            await Gui.UntilAsync(() => run.IsCompleted, "handoff after faulted init");
            await run;

            Assert.NotNull(main);
            Assert.Same(main, lifetime.MainWindow);
            Assert.True(main.IsVisible);
            Assert.False(splash.IsVisible);
            Assert.Equal(ShutdownMode.OnMainWindowClose, lifetime.ShutdownMode);
            Assert.Equal("corrupt settings.json", ((MainWindowViewModel)main.DataContext!).Banner);
            Assert.Contains("init failed", File.ReadAllText(env.Log.File!), StringComparison.Ordinal);
        }
        finally
        {
            await CloseIfOpenAsync(main);
            await CloseIfOpenAsync(splash);
        }
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Snapshot_of_the_splash_screen(AppTheme theme)
    {
        RenderClock.KeepRunning();
        var app = Application.Current!;
        try
        {
            // Motion None keeps the loading bar centered and still so the snapshot is deterministic.
            AppStartup.ApplyInitialAppearance(app, new AppSettings { Theme = theme == AppTheme.Light ? "light" : "dark", Motion = "none" });
            var splash = new SplashWindow { DataContext = new SplashViewModel() };
            try
            {
                splash.Show();
                Gui.Settle();

                Snapshots.Match(splash, $"splash-{theme.ToString().ToLowerInvariant()}");
            }
            finally
            {
                if (splash.IsVisible)
                {
                    splash.Close();
                }

                Gui.Settle();
            }
        }
        finally
        {
            AppStartup.ApplyInitialAppearance(app, new AppSettings { Theme = "dark", Motion = "smooth" });
            Gui.Settle();
        }
    }

    private static async Task CloseIfOpenAsync(Window? window)
    {
        if (window is { IsVisible: true })
        {
            window.Close();
            await Gui.UntilAsync(() => !window.IsVisible, "window closed");
        }
    }

    private sealed class StartupEnv : IAsyncDisposable
    {
        private readonly string _root = TempDir.Create("splash");

        public StartupEnv(string? startupWarning = null)
        {
            Data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
            Directory.CreateDirectory(Data.Path);
            Player = new InProcessPlayer(Data);
            Log = AppLog.For(Data, Time);
            Services = new AppServices(
                new SettingsService(Data, new AppSettings()),
                Library,
                new StubCovers(),
                Player,
                new Appearance(Application.Current!),
                Time,
                new AvaloniaUiDispatcher(),
                startupWarning)
            {
                FolderPicker = new StubFolderPicker(),
                AudioDevices = new StubAudioDevices(),
                Log = Log,
            };
        }

        public DataDirectory Data { get; }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        public StubLibrary Library { get; } = new();

        public InProcessPlayer Player { get; }

        public AppLog Log { get; }

        public AppServices Services { get; }

        public async ValueTask DisposeAsync()
        {
            await Player.DisposeAsync();
            MotionScale.Current.Set(MotionMode.Smooth);
            TempDir.TryDelete(_root);
        }
    }

    private sealed class FakeDesktopLifetime : IDesktopHost
    {
        public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnMainWindowClose;

        public Window? MainWindow { get; set; }

        public int ShutdownCalls { get; private set; }

        public void Shutdown(int exitCode = 0) => ShutdownCalls++;
    }
}
