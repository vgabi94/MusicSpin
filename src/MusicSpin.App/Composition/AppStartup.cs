using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;

namespace MusicSpin.App.Composition;

/// <summary>The tiny slice of <see cref="IClassicDesktopStyleApplicationLifetime"/> startup drives (tests provide a fake).</summary>
public interface IDesktopHost
{
    Window? MainWindow { get; set; }

    ShutdownMode ShutdownMode { get; set; }

    void Shutdown(int exitCode = 0);
}

/// <summary>
/// Desktop startup with a splash screen (#226): applies the saved theme and motion, shows <see cref="SplashWindow"/>
/// before constructing services or <see cref="MainWindow"/>, runs <see cref="MainWindowViewModel.InitializeAsync"/>
/// while the splash animates, and hands off once ready (or once <see cref="DefaultReadinessTimeout"/> elapses so a
/// busy library or stalled player never traps the splash). Closing the splash early cancels startup and cleans up.
/// </summary>
public static class AppStartup
{
    /// <summary>
    /// Maximum wait for initial library and player startup before showing <see cref="MainWindow"/> anyway; any work
    /// still in flight finishes in the background and surfaces notices in the window's banner.
    /// </summary>
    public static TimeSpan DefaultReadinessTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Applies the saved theme and motion before the splash window shows so it opens in the user's look.</summary>
    public static Appearance ApplyInitialAppearance(Application application, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(settings);
        var appearance = new Appearance(application);
        appearance.SetTheme(Appearance.ParseTheme(settings.Theme));
        appearance.SetMotion(MotionModes.Parse(settings.Motion));
        return appearance;
    }

    /// <summary>Starts the desktop app: shows the splash immediately, then builds and initializes the main window.</summary>
    public static Task StartDesktopAsync(
        Application application,
        IClassicDesktopStyleApplicationLifetime desktop,
        DataDirectory directory,
        AppSettings settings,
        string? startupWarning,
        AppLog log)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        var appearance = ApplyInitialAppearance(application, settings);
        return RunAsync(
            new ClassicDesktopHost(desktop),
            static () => new SplashWindow { DataContext = new SplashViewModel() },
            () =>
            {
                var services = AppComposition.CreateServices(application, directory, startupWarning, log, settings, appearance);
                return (services, AppComposition.CreateMainWindow(services));
            },
            static window => ((MainWindowViewModel)window.DataContext!).InitializeAsync(),
            TimeProvider.System,
            log,
            DefaultReadinessTimeout);
    }

    /// <summary>
    /// Runs the splash-to-main-window handoff over injected factories and <paramref name="time"/> (tests drive gates
    /// and timeouts deterministically without waiting on wall-clock time).
    /// </summary>
    public static async Task RunAsync(
        IDesktopHost desktop,
        Func<SplashWindow> createSplash,
        Func<(AppServices Services, Window MainWindow)> createMainWindow,
        Func<Window, Task> initializeAsync,
        TimeProvider time,
        AppLog log,
        TimeSpan? readinessTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(createSplash);
        ArgumentNullException.ThrowIfNull(createMainWindow);
        ArgumentNullException.ThrowIfNull(initializeAsync);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        var timeout = readinessTimeout ?? DefaultReadinessTimeout;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var splash = createSplash();
        var handoff = false;
        using var startupCancel = new CancellationTokenSource();

        void OnSplashClosed(object? sender, EventArgs e)
        {
            if (!handoff)
            {
                startupCancel.Cancel();
            }
        }

        splash.Closed += OnSplashClosed;
        desktop.MainWindow = splash;

        AppServices? services = null;
        Window? mainWindow = null;
        Task? initTask = null;

        try
        {
            await WaitForSplashPaintAsync(splash, startupCancel.Token).ConfigureAwait(true);
            startupCancel.Token.ThrowIfCancellationRequested();

            (services, mainWindow) = createMainWindow();
            startupCancel.Token.ThrowIfCancellationRequested();

            initTask = initializeAsync(mainWindow);
            await WaitForReadinessAsync(initTask, time, timeout, log, startupCancel.Token).ConfigureAwait(true);
            startupCancel.Token.ThrowIfCancellationRequested();

            handoff = true;
            splash.Closed -= OnSplashClosed;
            if (splash.DataContext is SplashViewModel splashViewModel)
            {
                splashViewModel.IsLoading = false;
            }

            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            mainWindow.Activate();
            if (splash.IsVisible)
            {
                splash.Close();
            }

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (OperationCanceledException) when (startupCancel.IsCancellationRequested)
        {
            await CleanupAsync(mainWindow, services, initTask, log).ConfigureAwait(true);
            CloseSplash(splash, OnSplashClosed, ref handoff);
            desktop.Shutdown();
        }
#pragma warning disable CA1031 // startup must never leave a hung splash or unobserved crash; failures are logged to app.log
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write("startup failed", ex);
            await CleanupAsync(mainWindow, services, initTask, log).ConfigureAwait(true);
            CloseSplash(splash, OnSplashClosed, ref handoff);
            desktop.Shutdown();
        }
    }

    private static async Task WaitForSplashPaintAsync(Window splash, CancellationToken cancellationToken)
    {
        if (!splash.IsVisible)
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOpened(object? sender, EventArgs e) => opened.TrySetResult();
            void OnClosed(object? sender, EventArgs e) => opened.TrySetCanceled(cancellationToken);
            splash.Opened += OnOpened;
            splash.Closed += OnClosed;
            try
            {
                using (cancellationToken.Register(() => opened.TrySetCanceled(cancellationToken)))
                {
                    if (!splash.IsVisible)
                    {
                        await opened.Task.ConfigureAwait(true);
                    }
                }
            }
            finally
            {
                splash.Opened -= OnOpened;
                splash.Closed -= OnClosed;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Yield below Render priority so Avalonia lays out and paints the splash's first frame before heavy work begins.
        var painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => painted.TrySetCanceled(cancellationToken)))
        {
            Dispatcher.UIThread.Post(() => painted.TrySetResult(), DispatcherPriority.Background);
            await painted.Task.ConfigureAwait(true);
        }
    }

    private static async Task WaitForReadinessAsync(Task initTask, TimeProvider time, TimeSpan timeout, AppLog log, CancellationToken cancellationToken)
    {
        if (initTask.IsCompleted)
        {
            ObserveFault(initTask, log);
            return;
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelReg = cancellationToken.Register(() => cancelled.TrySetResult());

        if (timeout <= TimeSpan.Zero)
        {
            _ = ObserveLateFailureAsync(initTask, log);
            return;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, time, delayCts.Token);
        var winner = await Task.WhenAny(initTask, delayTask, cancelled.Task).ConfigureAwait(true);
        await delayCts.CancelAsync().ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (ReferenceEquals(winner, initTask))
        {
            ObserveFault(initTask, log);
        }
        else
        {
            // Readiness cap hit while startup work still runs: show MainWindow now and log if the background task faults later.
            _ = ObserveLateFailureAsync(initTask, log);
        }
    }

    private static void ObserveFault(Task task, AppLog log)
    {
        if (task.IsFaulted && task.Exception is { } ex)
        {
            log.Write("startup initialization failed", ex.GetBaseException());
        }
    }

    private static async Task ObserveLateFailureAsync(Task task, AppLog log)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancelled during shutdown
        }
#pragma warning disable CA1031 // background startup faults are logged to app.log instead of becoming unobserved exceptions
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write("startup initialization failed", ex);
        }
    }

    private static async Task CleanupAsync(Window? mainWindow, AppServices? services, Task? initTask, AppLog log)
    {
        if (mainWindow?.DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.ShutdownAsync().ConfigureAwait(true);
                await viewModel.DisposeAsync().ConfigureAwait(true);
            }
#pragma warning disable CA1031 // cleanup on cancelled/failed startup must still release services and close
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.Write("startup cleanup failed", ex);
            }
        }

        if (services?.Library is IDisposable disposableLibrary)
        {
            disposableLibrary.Dispose();
        }

        if (initTask is not null)
        {
            _ = ObserveLateFailureAsync(initTask, log);
        }
    }

    private static void CloseSplash(SplashWindow splash, EventHandler onSplashClosed, ref bool handoff)
    {
        handoff = true;
        splash.Closed -= onSplashClosed;
        if (splash.DataContext is SplashViewModel splashViewModel)
        {
            splashViewModel.IsLoading = false;
        }

        if (splash.IsVisible)
        {
            splash.Close();
        }
    }

    private sealed class ClassicDesktopHost(IClassicDesktopStyleApplicationLifetime desktop) : IDesktopHost
    {
        public Window? MainWindow
        {
            get => desktop.MainWindow;
            set => desktop.MainWindow = value;
        }

        public ShutdownMode ShutdownMode
        {
            get => desktop.ShutdownMode;
            set => desktop.ShutdownMode = value;
        }

        public void Shutdown(int exitCode = 0) => desktop.Shutdown(exitCode);
    }
}
