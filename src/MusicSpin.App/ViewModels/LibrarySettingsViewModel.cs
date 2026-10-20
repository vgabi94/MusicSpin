using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>A library folder in Settings › Library: its path, and its track count, or that it can't be found now.</summary>
public sealed class LibraryFolderViewModel(FolderInfo folder)
{
    public const string NotFound = "Not found: moved, renamed or unplugged";

    public string Path { get; } = folder.Path;

    public bool IsReachable { get; } = folder.Exists;

    /// <summary>"1,204 tracks", or <see cref="NotFound"/>.</summary>
    public string Detail { get; } = !folder.Exists ? NotFound
        : string.Create(CultureInfo.CurrentCulture, $"{folder.TrackCount:N0} {(folder.TrackCount == 1 ? "track" : "tracks")}");
}

/// <summary>A file (or a folder) the last scan couldn't use, and why.</summary>
public sealed record LibraryIssueViewModel(string Path, string Message);

/// <summary>
/// Settings › Library (#31): the music folders, Add folder… (the OS's picker, then a scan), Remove (asked first), Rescan
/// with its progress and Cancel, when the last scan ran, Watch folders (scans after changes while the app runs; saved as
/// <c>watchLibrary</c>), and the files the last scan couldn't read. Every write goes through the library's lock: while
/// the CLI holds it, the notice says the library is busy.
/// </summary>
public sealed partial class LibrarySettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the folders stay quiet after a change before the watch scans (as <c>library watch</c>'s default).</summary>
    public static readonly TimeSpan WatchDebounce = TimeSpan.FromSeconds(2);

    public const string PickerTitle = "Add a music folder";

    private readonly ILibraryService _library;
    private readonly IFolderPicker _picker;
    private readonly ISettingsService _settings;
    private readonly MenuViewModel _menu;
    private readonly Notices _notices;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly Func<Task> _reload;
    private LibraryWatcher? _watcher;
    private string _watching = string.Empty;
    private bool _ready;

    public LibrarySettingsViewModel(ILibraryService library, IFolderPicker picker, ISettingsService settings, LibraryScanViewModel scan, MenuViewModel menu,
        Notices notices, TimeProvider time, IUiDispatcher ui, Func<Task> reload)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scan);
        _library = library;
        _picker = picker;
        _settings = settings;
        _menu = menu;
        _notices = notices;
        _time = time;
        _ui = ui;
        _reload = reload;
        Scan = scan;
        Scan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryScanViewModel.ProgressText) or nameof(LibraryScanViewModel.IsScanning))
            {
                OnPropertyChanged(nameof(ScanNote));
            }

            if (e.PropertyName == nameof(LibraryScanViewModel.IsScanning))
            {
                AddFolderCommand.NotifyCanExecuteChanged();
                RemoveCommand.NotifyCanExecuteChanged();
                RescanCommand.NotifyCanExecuteChanged();
            }
        };
        Scan.Finished += (_, _) => _ = LoadAsync();
        WatchFolders = settings.Current.WatchLibrary;
        _ready = true;
    }

    public LibraryScanViewModel Scan { get; }

    [ObservableProperty]
    public partial IReadOnlyList<LibraryFolderViewModel> Folders { get; private set; } = [];

    /// <summary>When the last scan of any folder ran (null: never).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastScanText), nameof(ScanNote))]
    public partial DateTimeOffset? LastScan { get; private set; }

    /// <summary>"Last scanned 18 Oct 2026, 14:22", or "Not scanned yet".</summary>
    public string LastScanText => LastScan is { } at
        ? $"Last scanned {TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}"
        : "Not scanned yet";

    /// <summary>The scan row's note: the progress while a scan runs, else when the last one ran.</summary>
    public string ScanNote => Scan.ProgressText ?? LastScanText;

    /// <summary>What adding a folder did, in plain words; null until a folder is added.</summary>
    [ObservableProperty]
    public partial string? Outcome { get; private set; }

    /// <summary>What the last scan couldn't read (a library folder that is gone shows on its row instead).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IssuesText), nameof(HasIssues))]
    public partial IReadOnlyList<LibraryIssueViewModel> Issues { get; private set; } = [];

    public bool HasIssues => Issues.Count > 0;

    /// <summary>"3 files couldn't be read".</summary>
    public string IssuesText => string.Create(CultureInfo.CurrentCulture, $"{Issues.Count:N0} {(Issues.Count == 1 ? "file" : "files")} couldn't be read");

    /// <summary>The list under the link is open.</summary>
    [ObservableProperty]
    public partial bool ShowsIssues { get; private set; }

    /// <summary>Scan after changes in the folders while the app runs (saved as <c>watchLibrary</c>).</summary>
    [ObservableProperty]
    public partial bool WatchFolders { get; set; }

    /// <summary>The folders are being watched now (tests read it).</summary>
    public int WatchedFolders => _watcher?.WatchedFolders ?? 0;

    /// <summary>The watch saw a change and waits for the folders to go quiet (tests read it).</summary>
    internal bool WatchHasPending => _watcher?.HasPending ?? false;

    /// <summary>Reads the folders, the last scan and the issues; starts the watch when it is on.</summary>
    public Task LoadAsync() => _notices.GuardAsync(async () =>
    {
        var folders = await _library.FoldersAsync().ConfigureAwait(true);
        var issues = await _library.IssuesAsync().ConfigureAwait(true);
        Folders = [.. folders.Select(f => new LibraryFolderViewModel(f))];
        LastScan = folders.Select(f => f.LastScan).Max();
        Issues = [.. issues.Where(i => i.Kind != "folder_missing").Select(i => new LibraryIssueViewModel(i.Path, i.Message))];
        if (Issues.Count == 0)
        {
            ShowsIssues = false;
        }

        Watch();
    });

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// "Add a music folder": the OS's picker, then <c>library add</c>, said in plain words. A folder added is scanned at
    /// once and its albums appear when the scan ends.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task AddFolder() => _notices.GuardAsync(async () =>
    {
        if (await _picker.PickFolderAsync(PickerTitle).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        var result = await _library.AddFolderAsync(path).ConfigureAwait(true);
        Outcome = Describe(result);
        await LoadAsync().ConfigureAwait(true);
        if (result.Status == "added")
        {
            await Scan.ScanAsync().ConfigureAwait(true);
        }
    });

    /// <summary>Remove asks first, in the menu's place.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Remove(LibraryFolderViewModel folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        _menu.Show(
            [
                new MenuEntryViewModel("Remove", () => RemoveAsync(folder)) { IsDestructive = true },
                MenuEntryViewModel.Of("Cancel", () => { }),
            ],
            $"Remove “{folder.Path}” from the library? Its tracks leave the library; the files stay on disk.");
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Rescan() => Scan.ScanAsync();

    [RelayCommand]
    private void ToggleIssues() => ShowsIssues = !ShowsIssues && HasIssues;

    /// <summary>The folder buttons wait while a scan holds the library.</summary>
    private bool IsIdle() => !Scan.IsScanning;

    private Task RemoveAsync(LibraryFolderViewModel folder) => _notices.GuardAsync(async () =>
    {
        await _library.RemoveFolderAsync(folder.Path).ConfigureAwait(true);
        Outcome = null;
        await LoadAsync().ConfigureAwait(true);
        await _reload().ConfigureAwait(true); // its albums leave the pages
    });

    partial void OnWatchFoldersChanged(bool value)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            _settings.Update(s => s with { WatchLibrary = value });
        }
        catch (MusicSpinException ex)
        {
            _notices.Post($"The setting applies now but wasn't saved: {ex.Message}");
        }

        Watch();
    }

    /// <summary>Watches the folders there now while the switch is on (again after the folders changed); stops otherwise.</summary>
    private void Watch()
    {
        List<string> folders = WatchFolders ? [.. Folders.Where(f => f.IsReachable).Select(f => f.Path)] : [];
        var watching = string.Join('|', folders);
        if (watching == _watching)
        {
            return; // the same folders: the watch goes on
        }

        _watcher?.Dispose();
        _watcher = null;
        _watching = watching;
        if (folders.Count > 0)
        {
            _watcher = new LibraryWatcher(folders, WatchDebounce, _time);
            _watcher.Changed += (_, _) => _ui.Post(() => _ = Scan.ScanAsync());
        }

        OnPropertyChanged(nameof(WatchedFolders));
    }

    /// <summary><c>library add</c>'s outcomes in plain words.</summary>
    internal static string Describe(FolderAddResult result) => result.Status switch
    {
        "exists" => $"{result.Path} is already a library folder.",
        "covered" => $"{result.Path} is already covered: it's inside {result.CoveredBy}, a library folder.",
        _ when result.Absorbed.Count > 0 =>
            $"Added {result.Path}. It takes in {string.Join(", ", result.Absorbed)}, which {(result.Absorbed.Count == 1 ? "was a library folder" : "were library folders")} inside it.",
        _ => $"Added {result.Path}.",
    };
}
