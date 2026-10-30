using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Normalization;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// Settings › Audio (#32): the output device ("System default" first, then the devices <c>music-spin devices</c> lists;
/// saved as <c>outputDevice</c>), exclusive mode (saved as <c>exclusiveMode</c>), and what the player plays through now.
/// Output options apply only when the player starts, so a change restarts it: the queue and the position come back, and
/// the music goes on if it was playing. Volume levelling (#139) and the equalizer (#141, <see cref="Equalizer"/>) apply at
/// once, without a restart. The player bar's Exclusive pill (#191) is the same switch: <see cref="ToggleExclusiveCommand"/>.
/// </summary>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    public const string SystemDefault = "System default";

    public const string ExclusiveNote = "Other apps go silent and the system doesn't resample";

    public const string ExclusiveOnTip = "Exclusive mode: on. " + ExclusiveNote + ". Click for shared mode.";

    public const string ExclusiveOffTip = "Exclusive mode: off (shared). Click for exclusive mode.";

    public const string ExclusiveRefusedTip = "Exclusive mode is on, but the player plays shared.";

    public const string LevellingNote = "Evens out track loudness (ReplayGain / R128)";

    private readonly IAudioDevices _devices;
    private readonly ISettingsService _settings;
    private readonly PlayerViewModel _player;
    private readonly MenuViewModel _menu;
    private readonly Notices _notices;
    private IReadOnlyList<string> _names = [];
    private bool _ready;
    private NormalizationMode _levellingMode;
    private int _sending; // normalize commands on their way: FollowPlayer must not undo a newer switch flip
    private bool _missed; // a status came while a change was in flight: Settle follows it once answered
    private OutputInfo? _outputBeforeRestart; // until the restarted player says what it opened, its output is unknown

    public AudioSettingsViewModel(IAudioDevices devices, ISettingsService settings, PlayerViewModel player, MenuViewModel menu, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(player);
        _devices = devices;
        _settings = settings;
        _player = player;
        _menu = menu;
        _notices = notices;
        var output = PlayerOutput.From(settings.Current);
        Device = output.Device;
        Exclusive = output.Exclusive;
        _levellingMode = NormalizationSettings.ModeOrDefault(settings.Current.Normalization);
        Levelling = _levellingMode != NormalizationMode.Off;
        if (_levellingMode == NormalizationMode.Off)
        {
            _levellingMode = NormalizationMode.Auto; // what the switch turns on
        }

        Equalizer = new EqualizerViewModel(settings, player, menu, notices);
        _player.StatusApplied += (_, _) => FollowPlayer(); // the CLI or an agent may change the mode while Settings is open
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Output))
            {
                OnPropertyChanged(nameof(InUse));
                OnPropertyChanged(nameof(IsExclusiveRefused));
                OnPropertyChanged(nameof(ExclusiveTip));
            }
        };
        _ready = true; // from here on, a change is the user's: saved, and the player restarts
    }

    /// <summary>The device chosen, by name; null: the system default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceLabel))]
    public partial string? Device { get; private set; }

    /// <summary>The device button's text: "System default", the device's name, or "… (not connected)".</summary>
    public string DeviceLabel => Label(Device);

    /// <summary>Exclusive mode: the switch, and the player bar's pill; a change restarts the player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExclusiveRefused), nameof(ExclusiveTip))]
    public partial bool Exclusive { get; set; }

    /// <summary>
    /// Exclusive was asked for, but the player plays shared (<c>status.output</c>): the device refused the format or was
    /// busy. Not while restarting, nor before the restarted player has said what it opened.
    /// </summary>
    public bool IsExclusiveRefused
        => Exclusive && !IsRestarting && _player.Output is { Exclusive: false } output && !ReferenceEquals(output, _outputBeforeRestart);

    /// <summary>The pill's tooltip and accessible name: on, off, or asked but shared with the player's reason.</summary>
    public string ExclusiveTip => IsExclusiveRefused
        ? ExclusiveRefusedTip + (_player.Output?.Notice is { } notice ? $"\n{notice}" : string.Empty) + "\nClick for shared mode."
        : Exclusive ? ExclusiveOnTip : ExclusiveOffTip;

    /// <summary>
    /// Volume levelling (#139): the switch. On keeps the mode saved (auto unless <c>music-spin normalize</c> chose track or
    /// album); off leaves the samples untouched. Applies at once, without a restart.
    /// </summary>
    [ObservableProperty]
    public partial bool Levelling { get; set; }

    /// <summary>The 10-band graphic equalizer (#141): applies at once, without a restart.</summary>
    public EqualizerViewModel Equalizer { get; }

    /// <summary>A change is restarting the player: the controls wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDeviceMenuCommand), nameof(ToggleExclusiveCommand))]
    [NotifyPropertyChangedFor(nameof(IsExclusiveRefused), nameof(ExclusiveTip))]
    public partial bool IsRestarting { get; private set; }

    /// <summary>
    /// "In use now: FiiO BTR13, exclusive, 44.1 kHz 24-bit" from <c>status.output</c>: what the device really opened at
    /// once a track played, with the reason on a second line when it is not what was asked; null before the player said.
    /// </summary>
    public string? InUse => _player.Output is not { } output ? null
        : output.Backend == "null" ? "In use now: no audio device (silent output)"
        : $"In use now: {(string.IsNullOrWhiteSpace(output.Device) ? "the system default" : output.Device)}, {output.Describe()}"
          + (output.Notice is { } notice ? $"\n{notice}" : string.Empty);

    /// <summary>Reads the devices (when Settings opens: one may have been plugged in).</summary>
    public Task LoadAsync() => _notices.GuardAsync(async () =>
    {
        FollowPlayer(); // the mode the player has now: music-spin normalize may have changed it since the app started
        if (_player.IsConnected)
        {
            await _player.RefreshAsync().ConfigureAwait(true); // the answer comes through StatusApplied
        }

        var devices = await _devices.ListAsync().ConfigureAwait(true);
        _names = [.. devices.Select(d => d.Name).Distinct(StringComparer.Ordinal)];
        OnPropertyChanged(nameof(DeviceLabel));
    });

    /// <summary>
    /// The switch shows the mode the running player has (its status), not only the one read at start-up: nothing is saved
    /// and nobody is told, it is only what the player says. A player without levelling says nothing: the switch stays.
    /// While a change from here is on its way, a status read before it would undo the flip: those are deferred to
    /// <see cref="Settle"/> (same idea as <see cref="EqualizerViewModel"/>).
    /// </summary>
    private void FollowPlayer()
    {
        if (_player.Status?.Normalization is not { } status || !NormalizationSettings.TryParseMode(status.Mode, out var mode))
        {
            return;
        }

        if (Volatile.Read(ref _sending) > 0)
        {
            _missed = true;
            return;
        }

        ApplyMode(mode);
    }

    private void ApplyMode(NormalizationMode mode)
    {
        var wasReady = _ready;
        _ready = false;
        try
        {
            if (mode != NormalizationMode.Off)
            {
                _levellingMode = mode; // what the switch turns on again
            }

            Levelling = mode != NormalizationMode.Off;
        }
        finally
        {
            _ready = wasReady;
        }
    }

    /// <summary>
    /// Nothing on its way: follows a status skipped while a normalize was in flight (the CLI or a poll may have raced
    /// the user's flip), then clears the miss flag.
    /// </summary>
    private void Settle()
    {
        if (Volatile.Read(ref _sending) > 0)
        {
            return;
        }

        if (!_missed)
        {
            return;
        }

        _missed = false;
        FollowPlayer();
    }

    /// <summary>The device button: "System default", then the devices, the chosen one ticked.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private void OpenDeviceMenu()
    {
        IEnumerable<string?> choices = [null, .. _names];
        if (Device is { } chosen && !_names.Contains(chosen, StringComparer.Ordinal))
        {
            choices = choices.Append(chosen); // saved but not connected now: still shown, ticked
        }

        _menu.Show(choices.Select(name => new MenuEntryViewModel(Label(name), () => ChooseAsync(name), name == Device)));
    }

    /// <summary>A device chosen: saved, and the player restarts on it.</summary>
    public Task ChooseAsync(string? device)
    {
        if (device == Device)
        {
            return Task.CompletedTask;
        }

        if (!Save(s => s with { OutputDevice = device }))
        {
            return Task.CompletedTask; // the button keeps the device the player has
        }

        Device = device;
        return RestartAsync();
    }

    /// <summary>The player bar's pill (#191): flips the switch, so saved and restarted as from Settings.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private void ToggleExclusive() => Exclusive = !Exclusive;

    partial void OnExclusiveChanged(bool value)
    {
        if (!_ready)
        {
            return;
        }

        if (!Save(s => s with { ExclusiveMode = value }))
        {
            _ready = false; // the switch goes back: the player keeps the mode it has
            Exclusive = !value;
            _ready = true;
            return;
        }

        _ = RestartAsync();
    }

    partial void OnLevellingChanged(bool value)
    {
        if (!_ready)
        {
            return;
        }

        var mode = value ? _levellingMode : NormalizationMode.Off;
        if (!Save(s => s with { Normalization = NormalizationSettings.Name(mode) }))
        {
            _ready = false; // the switch goes back: the player keeps the mode it has
            Levelling = !value;
            _ready = true;
            return;
        }

        _ = TellNormalizationAsync(NormalizationSettings.Name(mode));
    }

    /// <summary>
    /// Tells a running player the new mode. Status answers are not followed while the change is on its way: a poll
    /// read before it would flip the switch back. Once the last one is answered and applied, <see cref="Settle"/>
    /// catches up (mirrors <see cref="EqualizerViewModel"/>).
    /// </summary>
    private async Task TellNormalizationAsync(string mode)
    {
        if (!_player.IsConnected)
        {
            Settle();
            return;
        }

        Interlocked.Increment(ref _sending);
        try
        {
            await _player.SetNormalizationAsync(mode).ConfigureAwait(true);
        }
        finally
        {
            _player.AfterAnswers(() =>
            {
                if (Interlocked.Decrement(ref _sending) == 0)
                {
                    Settle();
                }
            });
        }
    }

    /// <summary>A device's name in the menu and on the button; a saved one the list doesn't have now says so.</summary>
    private string Label(string? device)
        => device is null ? SystemDefault
            : _names.Count == 0 || _names.Contains(device, StringComparer.Ordinal) ? device
            : $"{device} (not connected)";

    private bool CanChange() => !IsRestarting;

    /// <summary>Saves a change; false (and a notice) when the file can't be written: the player keeps its output then.</summary>
    private bool Save(Func<Core.Settings.AppSettings, Core.Settings.AppSettings> change)
    {
        try
        {
            _settings.Update(change);
            return true;
        }
        catch (MusicSpinException ex)
        {
            _notices.Post($"The setting wasn't saved, so the player keeps its output: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restarts the player so it takes the output saved now (the controls wait meanwhile).</summary>
    private async Task RestartAsync()
    {
        _outputBeforeRestart = _player.Output;
        IsRestarting = true;
        try
        {
            await _player.RestartAsync().ConfigureAwait(true);
        }
        finally
        {
            IsRestarting = false;
        }
    }
}
