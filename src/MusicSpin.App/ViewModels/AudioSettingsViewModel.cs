using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Normalization;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// Settings › Audio (#32): the output device ("System default" first, then the devices <c>music-spin devices</c> lists;
/// saved as <c>outputDevice</c>), exclusive mode (saved as <c>exclusiveMode</c>), and what the player plays through now.
/// Output options apply only when the player starts, so a change restarts it: the queue and the position come back, and
/// the music goes on if it was playing.
/// </summary>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    public const string SystemDefault = "System default";

    public const string ExclusiveNote = "Other apps go silent and the system doesn't resample";

    public const string LevellingNote = "Evens out track loudness (ReplayGain / R128)";

    private readonly IAudioDevices _devices;
    private readonly ISettingsService _settings;
    private readonly PlayerViewModel _player;
    private readonly MenuViewModel _menu;
    private readonly Notices _notices;
    private IReadOnlyList<string> _names = [];
    private bool _ready;
    private NormalizationMode _levellingMode;

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

        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Output))
            {
                OnPropertyChanged(nameof(InUse));
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

    /// <summary>Exclusive mode: the switch; a change restarts the player.</summary>
    [ObservableProperty]
    public partial bool Exclusive { get; set; }

    /// <summary>
    /// Volume levelling (#139): the switch. On keeps the mode saved (auto unless <c>music-spin normalize</c> chose track or
    /// album); off leaves the samples untouched. Applies at once, without a restart.
    /// </summary>
    [ObservableProperty]
    public partial bool Levelling { get; set; }

    /// <summary>A change is restarting the player: the controls wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDeviceMenuCommand))]
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
        var devices = await _devices.ListAsync().ConfigureAwait(true);
        _names = [.. devices.Select(d => d.Name).Distinct(StringComparer.Ordinal)];
        OnPropertyChanged(nameof(DeviceLabel));
    });

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

        _ = _player.SetNormalizationAsync(NormalizationSettings.Name(mode));
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
