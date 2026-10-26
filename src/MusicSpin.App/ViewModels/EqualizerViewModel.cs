using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Equalization;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// Settings › Audio › Equalizer (#141): the switch, a preset, the pre-amp and the ten bands, saving and deleting the
/// user's presets. Every change applies at once: it is saved (as <c>music-spin eq</c> saves it, so the next player starts
/// with it) and the running player is told (<c>eq</c>). Moving a band or choosing a preset turns the equalizer on, as
/// <c>eq</c> does. While Settings is open it follows what the player says (the CLI or an agent may change it), and a
/// drag saves only when it ends.
/// </summary>
public sealed partial class EqualizerViewModel : ObservableObject
{
    /// <summary>The sliders' step: pointer, arrow keys and the wheel (dB).</summary>
    public const double Step = 0.5;

    /// <summary>Page Up/Down, and the wheel with Shift (dB).</summary>
    public const double LargeStep = 3;

    /// <summary>Every slider's range (dB): the bands' and the pre-amp's are the same.</summary>
    public const double MinGain = EqualizerSettings.MinGainDb;

    public const double MaxGain = EqualizerSettings.MaxGainDb;

    public const int MaxNameLength = EqualizerSettings.MaxPresetNameLength;

    public const string Note = "Ten bands, before the volume";

    private const int DefaultRate = 48_000;

    private readonly ISettingsService _settings;
    private readonly PlayerViewModel _player;
    private readonly MenuViewModel _menu;
    private readonly Notices _notices;
    private GraphicEqualizer _eq;
    private bool _ready;
    private bool _pending;
    private int _sending;
    private bool _missed; // a status came mid-drag or while changes were on their way: Settle follows it

    public EqualizerViewModel(ISettingsService settings, PlayerViewModel player, MenuViewModel menu, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(player);
        _settings = settings;
        _player = player;
        _menu = menu;
        _notices = notices;
        _eq = new GraphicEqualizer(EqualizerSettings.From(settings.Current), EqualizerSettings.UserPresets(settings.Current));
        Preamp = new EqualizerSliderViewModel(this, -1, "Pre", "Pre-amp", EqualizerSettings.MinPreampDb, EqualizerSettings.MaxPreampDb);
        Bands = [.. Enumerable.Range(0, EqualizerSettings.BandCount).Select(i => new EqualizerSliderViewModel(
            this, i, BandLabel(i), $"{BandLabel(i)}Hz", EqualizerSettings.MinGainDb, EqualizerSettings.MaxGainDb))];
        Show();
        _player.StatusApplied += (_, _) => FollowPlayer();
        _ready = true; // from here on, a change is the user's: saved, and the player told
    }

    /// <summary>On: playback goes through the bands. Off (or on and flat) leaves the samples untouched.</summary>
    [ObservableProperty]
    public partial bool Enabled { get; set; }

    /// <summary>The pre-amp (dB), shown left of the bands.</summary>
    public EqualizerSliderViewModel Preamp { get; }

    /// <summary>31 Hz .. 16 kHz.</summary>
    public IReadOnlyList<EqualizerSliderViewModel> Bands { get; }

    /// <summary>The preset the gains are from ("rock", a saved one), or null once a band was moved by hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetLabel))]
    [NotifyPropertyChangedFor(nameof(IsUserPreset))]
    [NotifyCanExecuteChangedFor(nameof(AskDeletePresetCommand))]
    public partial string? Preset { get; private set; }

    /// <summary>The preset button's text: "Bass boost", or "Custom".</summary>
    public string PresetLabel => Display(Preset);

    /// <summary>The preset is one the user saved: it can be deleted.</summary>
    public bool IsUserPreset => Preset is { } name && _eq.Find(name) is { BuiltIn: false };

    /// <summary>How far the pre-amp is lowered automatically so boosts can't clip (dB, 0 when nothing is boosted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadroomNote))]
    public partial double Headroom { get; private set; }

    /// <summary>"Lowered 6 dB so boosts can't clip" while on and something is boosted; null otherwise.</summary>
    public string? HeadroomNote => Enabled && Headroom >= 0.05
        ? string.Create(CultureInfo.InvariantCulture, $"Lowered {Headroom:0.#} dB so boosts can't clip")
        : null;

    /// <summary>The name to save the gains under.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    public partial string? NewPresetName { get; set; }

    /// <summary>Why the name can't be saved under ("'rock' is a built-in preset…"); null otherwise.</summary>
    [ObservableProperty]
    public partial string? PresetProblem { get; private set; }

    /// <summary>A slider is being dragged: the player hears every step, the file is written when the drag ends.</summary>
    private bool IsDragging => Preamp.Scrub is not null || Bands.Any(b => b.Scrub is not null);

    /// <summary>"bass-boost" as "Bass boost"; null as "Custom".</summary>
    public static string Display(string? preset)
        => string.IsNullOrEmpty(preset) ? "Custom" : char.ToUpperInvariant(preset[0]) + preset[1..].Replace('-', ' ');

    /// <summary>A band as graphic equalizers label it: "31", "63", "125" … "1k" … "16k" (31.25 and 62.5 Hz rounded).</summary>
    public static string BandLabel(int band)
        => EqualizerSettings.Bands[band] >= 1000
            ? EqualizerSettings.BandName(band)
            : Math.Round(EqualizerSettings.Bands[band], MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    /// <summary>"+3", "-4.5", "0".</summary>
    public static string Format(double db) => db.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(HeadroomNote));
        if (_ready)
        {
            Change(eq => eq.Configure(eq.Settings with { Enabled = value }), new { enabled = value });
        }
    }

    partial void OnNewPresetNameChanged(string? value) => PresetProblem = null;

    /// <summary>The preset button: every preset, built-in then the user's, the current one ticked.</summary>
    [RelayCommand]
    private void OpenPresetMenu()
        => _menu.Show(_eq.Presets.Select(p => MenuEntryViewModel.Of(Display(p.Name), () => ChoosePreset(p.Name), p.Name == Preset)));

    /// <summary>Applies preset <paramref name="name"/> (and turns the equalizer on).</summary>
    public void ChoosePreset(string name)
    {
        if (_eq.Find(name) is not { } preset)
        {
            return; // deleted meanwhile (the CLI)
        }

        Change(eq => eq.ApplyPreset(preset.Name), new { preset = preset.Name });
    }

    /// <summary>Every band and the pre-amp back to 0 dB (the flat preset); on or off stays as it is.</summary>
    [RelayCommand]
    private void Reset()
    {
        var on = Enabled;
        Change(eq =>
        {
            eq.ApplyPreset("flat");
            eq.Configure(eq.Settings with { PreampDb = 0, Enabled = on });
        }, new { preset = "flat", preamp = 0.0, enabled = on });
    }

    /// <summary>Saves the gains under <see cref="NewPresetName"/> (replacing a saved preset of that name).</summary>
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        var name = NewPresetName;
        var problem = EqualizerSettings.CheckPresetName(name)
            ?? (_eq.Find(name) is null && _eq.UserPresets.Count >= EqualizerSettings.MaxUserPresets
                ? $"At most {EqualizerSettings.MaxUserPresets} presets can be saved; delete one first."
                : null);
        if (problem is not null)
        {
            PresetProblem = problem;
            return;
        }

        var normalized = EqualizerSettings.NormalizeName(name);
        Change(eq => eq.SavePreset(normalized), new { save = normalized });
        NewPresetName = null;
    }

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(NewPresetName);

    /// <summary>Delete asks first, in the menu's place.</summary>
    [RelayCommand(CanExecute = nameof(IsUserPreset))]
    private void AskDeletePreset()
    {
        if (Preset is not { } name)
        {
            return;
        }

        _menu.Show(
            [
                new MenuEntryViewModel("Delete", () =>
                {
                    DeletePreset(name);
                    return Task.CompletedTask;
                }) { IsDestructive = true },
                MenuEntryViewModel.Of("Cancel", () => { }),
            ],
            $"Delete the preset “{Display(name)}”?");
    }

    /// <summary>Deletes saved preset <paramref name="name"/>; the gains stay, as custom ones.</summary>
    public void DeletePreset(string name)
    {
        if (_eq.Find(name) is not { BuiltIn: false } preset)
        {
            return; // built in, or deleted meanwhile
        }

        Change(eq => eq.DeletePreset(preset.Name), new { delete = preset.Name });
    }

    /// <summary>A slider's new value: the pre-amp's, or a band's (which turns the equalizer on).</summary>
    internal void SetGain(EqualizerSliderViewModel slider, double gain)
    {
        if (!double.IsFinite(gain))
        {
            return;
        }

        gain = Math.Clamp(Math.Round(gain / Step) * Step, slider.Minimum, slider.Maximum);
        var current = _eq.Settings;
        if (slider.Band < 0)
        {
            if (gain != current.PreampDb)
            {
                Change(eq => eq.Configure(eq.Settings with { PreampDb = gain }), new { preamp = gain });
            }
        }
        else if (gain != current.BandGains[slider.Band] || !current.Enabled)
        {
            Change(eq =>
            {
                eq.SetBand(slider.Band, gain);
                eq.Configure(eq.Settings with { Enabled = true });
            }, new { band = slider.Band + 1, gain });
        }
    }

    /// <summary>A drag started or ended: its last value is saved when it ends.</summary>
    internal void OnScrubChanged() => Settle();

    /// <summary>
    /// Saves what is shown and not saved yet: Settings or the window closing mid-drag, or before the player answered.
    /// </summary>
    public void SavePending()
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        var (eq, presets) = (_eq.Settings, _eq.UserPresets);
        try
        {
            _settings.Update(s => eq.ApplyTo(s, presets));
        }
        catch (MusicSpinException ex)
        {
            _notices.Post($"The equalizer applies now but wasn't saved: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a change here, shows it and tells the running player; it is saved once the player has answered and no drag
    /// is under way (<see cref="Settle"/>). Without a running player that is at once.
    /// </summary>
    private void Change(Action<GraphicEqualizer> change, object command)
    {
        change(_eq);
        Show();
        _pending = true;
        _ = TellAsync(command);
    }

    /// <summary>
    /// Tells a running player; one that isn't takes the saved settings when it starts, so the change is saved now. Its
    /// answers are not followed while changes are on their way: a status read before them would undo the ones after it
    /// here. Once the last one is answered and applied, <see cref="Settle"/> catches up. "Answered" isn't "applied" by
    /// the audio, though: while the player's input is held (the turntable scene) the command waits and this returns at once.
    /// </summary>
    private async Task TellAsync(object command)
    {
        if (!_player.IsConnected)
        {
            Settle();
            return;
        }

        Interlocked.Increment(ref _sending);
        try
        {
            await _player.SetEqualizerAsync(command).ConfigureAwait(true);
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

    /// <summary>
    /// Nothing on its way and no drag: follows the status skipped meanwhile (the CLI may have changed the equalizer, and
    /// the player's answer to the last change is the newest status, so the order holds), then saves. The status is not
    /// followed when the player didn't get the changes: none is running, or its input is held.
    /// </summary>
    private void Settle()
    {
        if (IsDragging || Volatile.Read(ref _sending) > 0)
        {
            return;
        }

        if (_missed)
        {
            _missed = false;
            if (_player.IsConnected && !_player.IsInputHeld)
            {
                FollowPlayer();
            }
        }

        SavePending();
    }

    /// <summary>
    /// Shows what the player has (its status): nothing is saved and nobody is told. Mid-drag or while changes from here
    /// are on their way it waits for <see cref="Settle"/>; a player without an equalizer says nothing, and a status the
    /// same as what is shown changes nothing (it comes with every poll while playing).
    /// </summary>
    private void FollowPlayer()
    {
        if (_player.Status?.Equalizer is not { } status)
        {
            return;
        }

        if (IsDragging || Volatile.Read(ref _sending) > 0)
        {
            _missed = true;
            return;
        }

        if (Shows(status))
        {
            return;
        }

        _eq = new GraphicEqualizer(
            new EqualizerSettings(status.Enabled, status.Gains, status.PreampDb, status.Preset),
            status.UserPresets.Select(p => new EqualizerPreset(p.Key, p.Value, false)));
        Show(status.HeadroomDb);
    }

    /// <summary>The status says what is shown already: the switch, gains, preset, headroom and the user's presets.</summary>
    private bool Shows(EqualizerStatus status)
    {
        var settings = _eq.Settings;
        var presets = _eq.UserPresets;
        return settings.Enabled == status.Enabled && settings.PreampDb == status.PreampDb && Headroom == status.HeadroomDb
            && string.Equals(settings.Preset, status.Preset, StringComparison.Ordinal)
            && settings.BandGains.SequenceEqual(status.Gains)
            && presets.Count == status.UserPresets.Count
            && presets.All(p => status.UserPresets.TryGetValue(p.Name, out var gains) && p.Gains.SequenceEqual(gains));
    }

    /// <summary>Shows the model: the switch, sliders, preset and headroom (at the stream's rate unless the player said).</summary>
    private void Show(double? headroom = null)
    {
        var settings = _eq.Settings;
        var wasReady = _ready;
        _ready = false;
        try
        {
            Enabled = settings.Enabled;
            Preamp.Gain = settings.PreampDb;
            for (var i = 0; i < Bands.Count; i++)
            {
                Bands[i].Gain = settings.BandGains[i];
            }

            Preset = settings.Preset;
            OnPropertyChanged(nameof(IsUserPreset)); // the same name may have been saved or deleted
            AskDeletePresetCommand.NotifyCanExecuteChanged();
            Headroom = headroom ?? _eq.HeadroomDb(_player.Output?.Format?.SampleRate is > 0 and var rate ? rate : DefaultRate);
        }
        finally
        {
            _ready = wasReady;
        }
    }
}

/// <summary>One of the equalizer's sliders: a band, or the pre-amp (<see cref="Band"/> -1).</summary>
public sealed partial class EqualizerSliderViewModel : ObservableObject
{
    private readonly EqualizerViewModel _owner;

    internal EqualizerSliderViewModel(EqualizerViewModel owner, int band, string label, string name, double minimum, double maximum)
    {
        _owner = owner;
        Band = band;
        Label = label;
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>0..9, or -1 for the pre-amp.</summary>
    public int Band { get; }

    /// <summary>Under the slider: "31", "1k", "Pre".</summary>
    public string Label { get; }

    /// <summary>For screen readers: "31Hz", "1kHz", "Pre-amp".</summary>
    public string Name { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    /// <summary>The gain in dB.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    public partial double Gain { get; internal set; }

    /// <summary>The value while the slider is dragged, null otherwise (the slider sets it).</summary>
    [ObservableProperty]
    public partial double? Scrub { get; set; }

    /// <summary>Over the slider: "+3".</summary>
    public string GainText => EqualizerViewModel.Format(Gain);

    /// <summary>"31Hz: +3 dB".</summary>
    public string ValueText => $"{Name}: {GainText} dB";

    [RelayCommand]
    private void SetGain(double gain) => _owner.SetGain(this, gain);

    partial void OnScrubChanged(double? value) => _owner.OnScrubChanged();
}
