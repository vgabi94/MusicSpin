using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The library's scan (#31), shared by Settings › Library, the empty library's Add a music folder and the folder watch:
/// one at a time, asked for again while one runs means one more after it, with its progress and a Cancel. When a scan
/// ends the pages, the playlists and the favorites are read again. A busy library (the CLI scanning) is a notice.
/// </summary>
public sealed partial class LibraryScanViewModel(ILibraryService library, Notices notices, IUiDispatcher ui, Func<Task> reload) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cancel;
    private bool _again;

    /// <summary>A scan ended (finished, cancelled or failed): Settings reads the folders and the issues again.</summary>
    public event EventHandler? Finished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsScanning { get; private set; }

    /// <summary>The scanner's last report (null when none came yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial ScanProgress? Progress { get; private set; }

    /// <summary>"Looking for music: 340 files", "Reading 120 of 340 files · 2 couldn't be read", "Updating the albums".</summary>
    public string? ProgressText => !IsScanning ? null : Progress switch
    {
        null => "Scanning…",
        { Phase: "walk" } p => string.Create(CultureInfo.CurrentCulture, $"Looking for music: {p.FilesFound:N0} files"),
        { Phase: "read" } p => string.Create(CultureInfo.CurrentCulture, $"Reading {p.FilesRead:N0} of {p.FilesToRead:N0} files")
                               + (p.Failed > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {p.Failed:N0} couldn't be read") : string.Empty),
        _ => "Updating the albums",
    };

    /// <summary>Scans the library folders; while a scan runs, asks for one more after it.</summary>
    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            _again = true;
            return;
        }

        IsScanning = true;
        try
        {
            do
            {
                _again = false;
                using var cancel = _cancel = new CancellationTokenSource();
                var token = cancel.Token;
                try
                {
                    await notices.GuardAsync(() => library.ScanAsync(new UiProgress(ui, p =>
                    {
                        if (!token.IsCancellationRequested && IsScanning)
                        {
                            Progress = p;
                        }
                    }), token)).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break; // Cancel: no scan after this one either
                }
                finally
                {
                    _cancel = null;
                    Progress = null;
                }
            }
            while (_again);
        }
        finally
        {
            IsScanning = false;
        }

        await reload().ConfigureAwait(true);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window closing: a scan under way stops (what it read by then is kept).</summary>
    public void Dispose() => Cancel();

    /// <summary>Stops the scan under way (what it read by then stays in the library).</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel()
    {
        _again = false;
        _cancel?.Cancel();
    }

    /// <summary>The scanner reports from its own threads; the view model takes the reports on the UI thread.</summary>
    private sealed class UiProgress(IUiDispatcher ui, Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => ui.Post(() => report(value));
    }
}
