using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The player bar (mockup: "Player bar"): the transport, the format label, the queue button and the parts with their
/// own models: <see cref="Position"/>, <see cref="Volume"/> and the <see cref="Spectrum"/> analyzer.
/// </summary>
public sealed partial class PlayerBarViewModel : ObservableObject, IDisposable
{
    /// <summary>⏮ restarts the track after this many seconds, otherwise goes to the previous track.</summary>
    public const double RestartAfter = 3;

    public const string RestartTip = "Back to the start";
    public const string PreviousTip = "Previous track";
    public const string RadioShuffleTip = "Radio is already random";

    private readonly LayerViewModel _nowPlaying;
    private readonly LayerViewModel _queue;
    private bool _restarts = true;
    private int? _queueCount;
    private string? _queuePath;
    private TrackFormat? _format;

    public PlayerBarViewModel(PlayerViewModel player, LayerViewModel nowPlaying, LayerViewModel queue, TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(nowPlaying);
        ArgumentNullException.ThrowIfNull(queue);
        Player = player;
        Time = time;
        _nowPlaying = nowPlaying;
        _queue = queue;
        Position = new PositionViewModel(player);
        Volume = new VolumeViewModel(player);
        Spectrum = new SpectrumViewModel(time, ui);
        player.PropertyChanged += OnPlayerChanged;
        player.StatusApplied += OnStatusApplied;
        player.SpectrumReceived += (_, levels) =>
        {
            if (player.IsPlaying)
            {
                Spectrum.SetLevels(levels);
            }
        };
        queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LayerViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsQueueOpen));
            }
        };
    }

    public PlayerViewModel Player { get; }

    /// <summary>The app's clock, for the bar's own timing in the view (the marquees of a long title or artist).</summary>
    public TimeProvider Time { get; }

    public PositionViewModel Position { get; }

    public VolumeViewModel Volume { get; }

    public SpectrumViewModel Spectrum { get; }

    // ---- transport -------------------------------------------------------------------------------------------

    /// <summary>What ⏮ does now: restart the track (after <see cref="RestartAfter"/> s, or with nothing before it).</summary>
    public bool PreviousRestarts => _restarts;

    public string PreviousToolTip => _restarts ? RestartTip : PreviousTip;

    public bool CanShuffle => !Player.IsRadio;

    public string ShuffleToolTip => Player.IsRadio ? RadioShuffleTip : Player.Shuffle ? "Shuffle: on" : "Shuffle: off";

    public string RepeatToolTip => Player.Repeat switch
    {
        RepeatMode.All => "Repeat: all",
        RepeatMode.One => "Repeat: one",
        _ => "Repeat: off",
    };

    public bool IsRepeatOn => Player.Repeat != RepeatMode.Off;

    public bool IsRepeatOne => Player.Repeat == RepeatMode.One;

    /// <summary>off → all → one → off.</summary>
    public static RepeatMode NextRepeat(RepeatMode mode) => mode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    private Task Previous() => _restarts ? Player.SeekCommand.ExecuteAsync(0) : Player.PreviousCommand.ExecuteAsync(null);

    [RelayCommand(CanExecute = nameof(CanShuffle))]
    private Task ToggleShuffle() => Player.SetShuffleCommand.ExecuteAsync(!Player.Shuffle);

    [RelayCommand]
    private Task CycleRepeat() => Player.SetRepeatCommand.ExecuteAsync(NextRepeat(Player.Repeat));

    // ---- format ----------------------------------------------------------------------------------------------

    /// <summary>The playing file's format ("FLAC" and "24 bit, 192 kHz, 6431k, Stereo"); null before a track loads.</summary>
    public TrackFormat? Format => _format;

    public string FormatName => Format?.Name ?? "—";

    /// <summary>The details after the format name, with a leading space.</summary>
    public string FormatDetails => Format is { } f ? " " + f.Details : string.Empty;

    public bool IsHiRes => Player.HiRes;

    // ---- the right-hand buttons ------------------------------------------------------------------------------

    /// <summary>The entries waiting up next (what the queue drawer lists): added with Play next or Add to queue.</summary>
    public int QueueCount => Player.UpNext;

    /// <summary>The count badge's text; null hides it.</summary>
    public string? QueueCountText => QueueCount > 0 ? QueueCount.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>Counts badge pulses: something was added to the queue while the drawer was closed.</summary>
    [ObservableProperty]
    public partial int QueuePulses { get; private set; }

    public bool IsQueueOpen => _queue.IsOpen;

    /// <summary>The cover: opens Now Playing (once something is loaded).</summary>
    [RelayCommand]
    private void OpenNowPlaying()
    {
        if (Player.HasTrack)
        {
            _nowPlaying.Toggle();
        }
    }

    [RelayCommand]
    private void ToggleQueue() => _queue.Toggle();

    public void Dispose() => Spectrum.Dispose();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Position):
                UpdatePrevious();
                break;
            case nameof(PlayerViewModel.Repeat):
                OnPropertyChanged(nameof(RepeatToolTip));
                OnPropertyChanged(nameof(IsRepeatOn));
                OnPropertyChanged(nameof(IsRepeatOne));
                break;
            case nameof(PlayerViewModel.Shuffle):
                OnPropertyChanged(nameof(ShuffleToolTip));
                break;
            case nameof(PlayerViewModel.IsRadio):
                OnPropertyChanged(nameof(CanShuffle));
                OnPropertyChanged(nameof(ShuffleToolTip));
                ToggleShuffleCommand.NotifyCanExecuteChanged();
                break;
            case nameof(PlayerViewModel.HiRes):
                OnPropertyChanged(nameof(IsHiRes));
                break;
            case nameof(PlayerViewModel.IsPlaying):
                if (!Player.IsPlaying)
                {
                    Spectrum.Silence();
                }

                break;
        }
    }

    /// <summary>A whole status is in: the format label, ⏮ and the queue count, which depend on several of its values.</summary>
    private void OnStatusApplied(object? sender, EventArgs e)
    {
        var format = TrackFormat.Of(Player.Status?.Track);
        if (format != _format)
        {
            _format = format;
            OnPropertyChanged(nameof(Format));
            OnPropertyChanged(nameof(FormatName));
            OnPropertyChanged(nameof(FormatDetails));
        }

        UpdatePrevious();
        UpdateQueueCount();
    }

    private void UpdatePrevious()
    {
        var first = Player.QueueIndex is null or 0 && Player.Repeat != RepeatMode.All && !Player.IsRadio;
        var restarts = Player.Position > RestartAfter || first;
        if (restarts != _restarts)
        {
            _restarts = restarts;
            OnPropertyChanged(nameof(PreviousRestarts));
            OnPropertyChanged(nameof(PreviousToolTip));
        }
    }

    /// <summary>
    /// The count badge pulses when entries were added while the drawer is closed: the count grew and the same entry is
    /// still playing (playing something new replaces the queue instead).
    /// </summary>
    private void UpdateQueueCount()
    {
        var count = QueueCount;
        var current = Player.Path;
        if (_queueCount is { } before && count > before && current == _queuePath && !_queue.IsOpen)
        {
            QueuePulses++;
        }

        (_queueCount, _queuePath) = (count, current);
        OnPropertyChanged(nameof(QueueCount));
        OnPropertyChanged(nameof(QueueCountText));
    }
}
