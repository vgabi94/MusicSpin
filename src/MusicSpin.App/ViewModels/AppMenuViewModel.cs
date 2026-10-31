using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The app menu (#135): File, Playback and View, after the title on Windows and Linux and in the menu bar on macOS. Its
/// entries run the commands the rest of the window runs (the player bar's, the sidebar's, Settings'); what is here is only
/// when each one can run, the checks, and the two things the window does itself (focus the search box, close).
/// </summary>
public sealed partial class AppMenuViewModel : ObservableObject
{
    private readonly PlayerBarViewModel _bar;
    private readonly SidebarViewModel _sidebar;
    private readonly LayerViewModel _nowPlaying;
    private readonly LayerViewModel _queue;

    public AppMenuViewModel(PlayerBarViewModel bar, SidebarViewModel sidebar, LayerViewModel nowPlaying, LayerViewModel queue)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(nowPlaying);
        ArgumentNullException.ThrowIfNull(queue);
        _bar = bar;
        _sidebar = sidebar;
        _nowPlaying = nowPlaying;
        _queue = queue;
        Player = bar.Player;
        Player.PropertyChanged += OnPlayerChanged;
        bar.Volume.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VolumeViewModel.IsMuted))
            {
                OnPropertyChanged(nameof(IsMuted));
            }
        };
    }

    public PlayerViewModel Player { get; }

    /// <summary>Ctrl+F (⌘F on macOS) or View › Search: the window focuses the search box.</summary>
    public event EventHandler? SearchRequested;

    /// <summary>File › Quit: the window closes, as with its ✕ (the music stops and a player the app started quits).</summary>
    public event EventHandler? QuitRequested;

    /// <summary>"Play" or "Pause", for what Playback's first entry does now.</summary>
    public string PlayPauseHeader => Player.IsPlaying ? "Pause" : "Play";

    public bool IsShuffleOn => Player.Shuffle;

    public bool IsRepeatOff => Player.Repeat == RepeatMode.Off;

    public bool IsRepeatAll => Player.Repeat == RepeatMode.All;

    public bool IsRepeatOne => Player.Repeat == RepeatMode.One;

    public bool IsMuted => _bar.Volume.IsMuted;

    /// <summary>A track is loaded: play/pause, ⏮ and Now Playing have something to act on.</summary>
    private bool HasTrack() => Player.IsConnected && Player.HasTrack;

    /// <summary>
    /// The player has an entry after this one: a later entry in the queue, the first again under repeat all, or the
    /// station's next pick (radio always has one). Otherwise the player refuses <c>next</c>.
    /// </summary>
    public bool HasNext => Player.IsConnected && (Player.IsRadio
        || (Player.QueueIndex is { } index && index + 1 < Player.QueueLength)
        || (Player.Repeat == RepeatMode.All && Player.QueueLength > 0));

    private bool IsConnected() => Player.IsConnected;

    [RelayCommand(CanExecute = nameof(HasTrack))]
    private Task TogglePlay() => Player.TogglePlayCommand.ExecuteAsync(null);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private Task Next() => Player.NextCommand.ExecuteAsync(null);

    /// <summary>As the player bar's ⏮: back to the start after a few seconds, otherwise the previous track.</summary>
    [RelayCommand(CanExecute = nameof(HasTrack))]
    private Task Previous() => _bar.PreviousCommand.ExecuteAsync(null);

    /// <summary>Radio is random already, so Shuffle waits until it ends (as the player bar's button does).</summary>
    [RelayCommand(CanExecute = nameof(CanShuffle))]
    private Task ToggleShuffle() => _bar.ToggleShuffleCommand.ExecuteAsync(null);

    private bool CanShuffle() => Player.IsConnected && _bar.CanShuffle;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task SetRepeat(RepeatMode mode) => Player.SetRepeatCommand.ExecuteAsync(mode);

    [RelayCommand(CanExecute = nameof(IsConnected), AllowConcurrentExecutions = true)]
    private Task VolumeUp() => _bar.Volume.StepCommand.ExecuteAsync(VolumeViewModel.KeyStep);

    [RelayCommand(CanExecute = nameof(IsConnected), AllowConcurrentExecutions = true)]
    private Task VolumeDown() => _bar.Volume.StepCommand.ExecuteAsync(-VolumeViewModel.KeyStep);

    [RelayCommand(CanExecute = nameof(IsConnected), AllowConcurrentExecutions = true)]
    private Task ToggleMute() => _bar.Volume.ToggleMuteCommand.ExecuteAsync(null);

    [RelayCommand]
    private void Show(PageKind page) => _sidebar.Navigate(page switch
    {
        PageKind.Tracks => _sidebar.Tracks,
        PageKind.Radio => _sidebar.Radio,
        PageKind.Favorites => _sidebar.Favorites,
        _ => _sidebar.Albums,
    });

    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void OpenNowPlaying() => _nowPlaying.Open();

    [RelayCommand]
    private void OpenQueue() => _queue.Open();

    [RelayCommand]
    private void Search() => SearchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsPlaying):
                OnPropertyChanged(nameof(PlayPauseHeader));
                break;
            case nameof(PlayerViewModel.Shuffle):
                OnPropertyChanged(nameof(IsShuffleOn));
                break;
            case nameof(PlayerViewModel.Repeat):
                OnPropertyChanged(nameof(IsRepeatOff));
                OnPropertyChanged(nameof(IsRepeatAll));
                OnPropertyChanged(nameof(IsRepeatOne));
                NextCommand.NotifyCanExecuteChanged();
                break;
            case nameof(PlayerViewModel.IsConnected):
            case nameof(PlayerViewModel.HasTrack):
                TogglePlayCommand.NotifyCanExecuteChanged();
                PreviousCommand.NotifyCanExecuteChanged();
                OpenNowPlayingCommand.NotifyCanExecuteChanged();
                NextCommand.NotifyCanExecuteChanged();
                ToggleShuffleCommand.NotifyCanExecuteChanged();
                SetRepeatCommand.NotifyCanExecuteChanged();
                VolumeUpCommand.NotifyCanExecuteChanged();
                VolumeDownCommand.NotifyCanExecuteChanged();
                ToggleMuteCommand.NotifyCanExecuteChanged();
                break;
            case nameof(PlayerViewModel.IsRadio):
                ToggleShuffleCommand.NotifyCanExecuteChanged();
                NextCommand.NotifyCanExecuteChanged();
                break;
            case nameof(PlayerViewModel.QueueIndex):
            case nameof(PlayerViewModel.QueueLength):
                NextCommand.NotifyCanExecuteChanged();
                break;
        }
    }
}
