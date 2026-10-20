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

    /// <summary>Edits the scanned library before the app opens (playlists, favorites), given its track paths.</summary>
    public Action<MusicLibrary, IReadOnlyList<string>>? Prepare { get; init; }

    public string? StartupWarning { get; init; }

    /// <summary>Replaces the library on disk (large or scripted libraries).</summary>
    public ILibraryService? Library { get; init; }

    /// <summary>How long the library on disk waits to read or write while another process holds it (null: the app's default).</summary>
    public TimeSpan? LockTimeout { get; init; }

    /// <summary>Replaces the covers on disk.</summary>
    public ICoverService? Covers { get; init; }

    /// <summary>A player is already running when the app starts (as if the CLI started it).</summary>
    public bool PlayerRunning { get; init; }

    /// <summary>The app talks to a <see cref="ScriptedPlayer"/> (exact statuses and events) instead of a real one.</summary>
    public bool Scripted { get; init; }

    /// <summary>The folder picker the app gets (Settings › Library, the empty library).</summary>
    public StubFolderPicker FolderPicker { get; init; } = new();

    /// <summary>The audio devices the app sees (Settings › Audio).</summary>
    public StubAudioDevices AudioDevices { get; init; } = new();

    /// <summary>The in-process player reads the library (radio works).</summary>
    public bool PlayerReadsLibrary { get; init; }

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
        Player = new InProcessPlayer(Data) { ReadsLibrary = options.PlayerReadsLibrary };
        Scripted = options.Scripted ? new ScriptedPlayer(Data) { FollowsCommands = true } : null;
    }

    public string Root { get; }

    public string Music { get; }

    public DataDirectory Data { get; }

    public AppOptions Options { get; }

    public InProcessPlayer Player { get; }

    /// <summary>The player the app talks to with <see cref="AppOptions.Scripted"/>.</summary>
    public ScriptedPlayer? Scripted { get; }

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
            if (Options.Prepare is { } prepare)
            {
                using var library = OpenLibrary();
                prepare(library, TrackPaths);
            }
        }

        if (Options.PlayerRunning)
        {
            Player.Start();
        }

        var ui = new AvaloniaUiDispatcher();
        var libraryService = Options.Library ?? new LibraryService(Data, Time, Options.LockTimeout);
        Services = new AppServices(
            new SettingsService(Data),
            libraryService,
            Options.Covers ?? new CoverService(Data, new SkiaCoverResizer(), Time, library: libraryService, ui: ui),
            (IPlayerConnector?)Scripted ?? Player,
            new Appearance(Application.Current!),
            Time,
            ui,
            Options.StartupWarning)
        {
            FolderPicker = Options.FolderPicker,
            AudioDevices = Options.AudioDevices,
        };
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

    /// <summary>Opens the library as another process would (the CLI): dispose it to let go of the lock.</summary>
    public MusicLibrary OpenLibrary() => MusicLibrary.OpenWritable(Data, lockTimeout: TimeSpan.FromSeconds(5));

    /// <summary>Edits the library as the CLI would, then lets the app's poll see the change.</summary>
    public T Edit<T>(Func<MusicLibrary, T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        T result;
        using (var library = OpenLibrary())
        {
            result = edit(library);
        }

        Advance(LibraryService.PollInterval);
        return result;
    }

    /// <summary>
    /// <see cref="Edit"/>, then waits until the app has read all of the library again: the sidebar, the favorites and the
    /// page shown (one reload reads them in turn). Waiting only for the sidebar would leave the page's read in flight, to
    /// land in the middle of whatever the test does next.
    /// </summary>
    public async Task<T> EditAndReloadAsync<T>(Func<MusicLibrary, T> edit)
    {
        var asked = ViewModel.ReloadsAsked;
        var result = Edit(edit);
        await Gui.UntilAsync(() => ViewModel.ReloadsAsked > asked && ViewModel.ReloadsDone == ViewModel.ReloadsAsked, "the app's reload after the edit");
        return result;
    }

    /// <summary>Reads the library afresh, as <c>music-spin playlist show</c> would.</summary>
    public T Read<T>(Func<MusicLibrary, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var library = MusicLibrary.OpenForRead(Data, TimeSpan.FromSeconds(5));
        return read(library);
    }

    /// <summary>
    /// A barrier for "the app sent nothing" checks: once this returns, every command the app started sending before it
    /// has reached the player. The app sends one request at a time on its connection, in order, and this one goes last.
    /// </summary>
    public async Task PlayerRoundTripAsync()
    {
        Gui.Settle(); // UI work queued by the action under test runs first, so its sends start before ours
        await ViewModel.Player.SendAsync("status");
    }

    /// <summary>Moves the fake clock (the app's timers) and Avalonia's animations forward together.</summary>
    public void Advance(TimeSpan by) => UiTime.Advance(Time, by);

    /// <summary>
    /// After choosing an album on the albums page: waits for the turntable scene to start, plays it through on the
    /// app's clock and waits until it has ended (play sent).
    /// </summary>
    public async Task PlaySceneAsync()
    {
        await Gui.UntilAsync(() => ViewModel.Scene.IsRunning, "the scene started");
        Advance(TimeSpan.FromMilliseconds(SceneTimeline.End * MotionScale.Current.Multiplier));
        await Gui.UntilAsync(() => !ViewModel.Scene.IsRunning, "the scene ended");
    }

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
        if (Scripted is not null)
        {
            await Scripted.DisposeAsync();
        }
        Dispatcher.UIThread.RunJobs();
        TempDir.TryDelete(Root);
    }
}
