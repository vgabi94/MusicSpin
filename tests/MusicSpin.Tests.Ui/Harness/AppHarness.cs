using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Composition;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>What <see cref="AppHarness.OpenAsync"/> builds. Defaults: 1440×880, dark, an empty library, a player.</summary>
internal sealed record AppOptions
{
    public double Width { get; init; } = 1440;

    public double Height { get; init; } = 880;

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>settings.json before start (Theme above is applied to it).</summary>
    public AppSettings Settings { get; init; } = new();

    /// <summary>Writes music into the folder; the harness then adds and scans it. Null: no library.</summary>
    public Action<string>? Music { get; init; }

    public string? StartupWarning { get; init; }

    /// <summary>Replaces the library on disk (large or scripted libraries).</summary>
    public ILibraryService? Library { get; init; }

    /// <summary>Replaces the covers on disk.</summary>
    public ICoverService? Covers { get; init; }

    /// <summary>A player is already running when the app starts (as if the CLI started it).</summary>
    public bool PlayerRunning { get; init; }

    /// <summary>Call <see cref="MainWindowViewModel.InitializeAsync"/> (connects the player, loads the first page).</summary>
    public bool Initialize { get; init; } = true;
}

/// <summary>
/// The app's main window over a temporary data folder, built by the app's own composition root with test services: an
/// in-process player (<see cref="InProcessPlayer"/>), a library scanned from fixture files, and a fake clock.
/// </summary>
internal sealed class AppHarness : IAsyncDisposable
{
    private AppHarness(string root, AppOptions options)
    {
        Root = root;
        Music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        Data = new DataDirectory(Path.Combine(root, "data"), DataDirectorySource.Override);
        Options = options;
        Player = new InProcessPlayer(Data);
    }

    public string Root { get; }

    public string Music { get; }

    public DataDirectory Data { get; }

    public AppOptions Options { get; }

    public InProcessPlayer Player { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public AppServices Services { get; private set; } = null!;

    public MainWindow Window { get; private set; } = null!;

    public MainWindowViewModel ViewModel => (MainWindowViewModel)Window.DataContext!;

    /// <summary>The scanned library's track paths (registered with the player's fake decoders).</summary>
    public IReadOnlyList<string> TrackPaths { get; private set; } = [];

    public static async Task<AppHarness> OpenAsync(AppOptions? options = null)
    {
        options ??= new AppOptions();
        var harness = new AppHarness(TempDir.Create("ui"), options);
        try
        {
            await harness.StartAsync();
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    private async Task StartAsync()
    {
        RenderClock.KeepRunning();
        new SettingsStore(Data).Save(Options.Settings with { Theme = Options.Theme == AppTheme.Light ? "light" : "dark" });
        if (Options.Music is { } music)
        {
            music(Music);
            TrackPaths = Scan();
            Player.AddTracks(TrackPaths);
        }

        if (Options.PlayerRunning)
        {
            Player.Start();
        }

        Services = new AppServices(
            new SettingsService(Data),
            Options.Library ?? new LibraryService(Data, Time),
            Options.Covers ?? new CoverService(Data, new SkiaCoverResizer()),
            Player,
            new Appearance(Application.Current!),
            Time,
            new AvaloniaUiDispatcher(),
            Options.StartupWarning);
        Window = AppComposition.CreateMainWindow(Services);
        Window.Width = Options.Width;
        Window.Height = Options.Height;
        Window.Show();
        if (Options.Initialize)
        {
            await ViewModel.InitializeAsync();
            await Gui.UntilAsync(() => ViewModel.Player.IsConnected, "the player connected");
        }

        Gui.Settle();
    }

    /// <summary>Adds the music folder to the library and scans it (fake decoders get the same paths).</summary>
    public IReadOnlyList<string> Scan()
    {
        using var library = MusicLibrary.OpenWritable(Data, lockTimeout: TimeSpan.FromSeconds(5));
        if (library.Folders().Count == 0)
        {
            library.AddFolder(Music);
        }

        library.Scan(cancellationToken: TestContext.Current.CancellationToken);
        return [.. library.Tracks(new TrackQuery { Paging = new Paging(0, Paging.MaxLimit) }).Items.Select(t => t.Path)];
    }

    /// <summary>Moves the fake clock (the app's timers) and Avalonia's animations forward together.</summary>
    public void Advance(TimeSpan by) => UiTime.Advance(Time, by);

    /// <summary>Closes the window as the user would and waits until it has shut down.</summary>
    public async Task CloseAsync()
    {
        Window.Close();
        await Gui.UntilAsync(() => !Window.IsVisible, "the window closed");
    }

    public async ValueTask DisposeAsync()
    {
        if (Window is { IsVisible: true } window)
        {
            window.Close();
            await Gui.UntilAsync(() => !window.IsVisible, "the window closed");
        }

        (Services?.Library as IDisposable)?.Dispose();
        await Player.DisposeAsync();
        Dispatcher.UIThread.RunJobs();
        TempDir.TryDelete(Root);
    }
}
