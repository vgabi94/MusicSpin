using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The volume (mockup: "Player bar: volume"), in percent to 0.1; the player's volume is the percent divided by 100, and
/// 100% is its bit-perfect path. The bar moves in whole percents, the box takes exact values. Mute is the player's: it
/// plays at 0 and keeps the level, so a mute outlives the app (the next session shows it muted at that level, and
/// unmuting goes back to it); setting a level unmutes. Changes made elsewhere (the CLI) show up here.
/// </summary>
/// <remarks>
/// With <see cref="InDecibels"/> (Settings › Audio, #39) the same volume shows in decibels to 0.1 dB: 100% is 0 dB, and
/// the bar spreads 0 to −60 dB evenly, one step a dB, with one last step to silence (<see cref="DecibelBarMinimum"/>), so
/// the quiet levels get as much of it as the loud ones. Keys, the wheel and the bar then step in whole dB. The box takes
/// both units either way. Only the display changes: the player's volume stays linear.
/// </remarks>
public sealed partial class VolumeViewModel : ObservableObject
{
    /// <summary>Below this the speaker shows one wave.</summary>
    public const double LowLevel = 50;

    /// <summary>In decibels, below this (the decibel bar's middle) the speaker shows one wave.</summary>
    public const double LowDecibels = -30;

    /// <summary>Unmuting at 0% goes to this.</summary>
    public const double UnmuteLevel = 50;

    /// <summary>The steps for the keys: ↑/↓ in the box and Ctrl+↑/↓ anywhere by one, Shift+↑/↓ in the box by ten.</summary>
    public const double KeyStep = 1;
    public const double KeyStepDown = -KeyStep;
    public const double LargeKeyStep = 10;
    public const double LargeKeyStepDown = -LargeKeyStep;

    /// <summary>The wheel over the bar: one step a notch, by five with Shift.</summary>
    public const double WheelStep = 1;
    public const double LargeWheelStep = 5;

    /// <summary>The decibel bar's left end: silence, one step below <see cref="TypedValues.DecibelFloor"/>.</summary>
    public const double DecibelBarMinimum = TypedValues.DecibelFloor - KeyStep;

    /// <summary>The decibel bar's right end: 0 dB, the bit-perfect 100%.</summary>
    public const double DecibelBarMaximum = 0;

    public const string BitPerfectTip = "Bit-perfect: the samples are left untouched";
    public const string TypeTip = "Type a volume: 37, 37.5, +5, -10%";
    public const string DecibelTypeTip = "Type a volume: -12, -12.5 dB, +3 dB, 40%";

    private readonly PlayerViewModel _player;
    private (double Volume, bool Muted)? _sent;
    private bool _answered;
    private int _sends;

    public VolumeViewModel(PlayerViewModel player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        Number = new TypedField(() => FormatShown(Level), ApplyTyped);
        player.StatusApplied += (_, _) => Sync();
        Sync();
    }

    /// <summary>The level box.</summary>
    public TypedField Number { get; }

    /// <summary>Percent, 0..100, to 0.1 (in decibels: to 0.1 dB). Kept while muted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilent), nameof(IsLow), nameof(IsFull), nameof(NumberTip), nameof(MuteTip), nameof(ValueText), nameof(BarValue))]
    public partial double Level { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilent), nameof(IsLow), nameof(IsFull), nameof(MuteTip), nameof(ValueText))]
    public partial bool IsMuted { get; private set; }

    /// <summary>The volume shows in decibels on a loudness bar (#39), not in percent on a linear one. Off by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLow), nameof(IsFull), nameof(NumberTip), nameof(ValueText), nameof(BarValue))]
    public partial bool InDecibels { get; set; }

    /// <summary>
    /// Where the bar is: <see cref="Level"/> in percent; in decibels the level's dB (anything below −60 dB at −60), and
    /// <see cref="DecibelBarMinimum"/> at 0%.
    /// </summary>
    public double BarValue => !InDecibels ? Level
        : Level <= 0 ? DecibelBarMinimum
        : Math.Max(Decibels(Level), TypedValues.DecibelFloor);

    /// <summary>Muted, or at 0%: the speaker shows a cross and the bar and the number are dimmed.</summary>
    public bool IsSilent => IsMuted || Level == 0;

    /// <summary>Below <see cref="LowLevel"/> (in decibels, <see cref="LowDecibels"/>): the speaker shows one wave.</summary>
    public bool IsLow => !IsSilent && (InDecibels ? BarValue < LowDecibels : Level < LowLevel);

    /// <summary>At <see cref="LowLevel"/> or above: the speaker shows both waves.</summary>
    public bool IsFull => !IsSilent && !IsLow;

    public string MuteTip => IsSilent ? "Unmute" : "Mute";

    public string NumberTip => Level == 100 ? BitPerfectTip : InDecibels ? DecibelTypeTip : TypeTip;

    /// <summary>For screen readers: "80%" (or "−1.9 dB"), or "Muted".</summary>
    public string ValueText => IsMuted ? "Muted" : FormatShown(Level);

    /// <summary>"80%", "37.5%".</summary>
    public static string Format(double percent) => string.Create(CultureInfo.InvariantCulture, $"{TypedValues.RoundVolume(percent):0.#}%");

    /// <summary>A volume in percent as "−12 dB", "−8.5 dB", "0 dB" (100%) or "−∞ dB" (0%).</summary>
    public static string FormatDecibels(double percent)
    {
        if (percent <= 0)
        {
            return "−∞ dB";
        }

        var decibels = Decibels(percent);
        return decibels == 0 ? "0 dB" : string.Create(CultureInfo.InvariantCulture, $"−{-decibels:0.#} dB");
    }

    /// <summary>Sets the level (unmuting).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetLevel(double percent)
    {
        IsMuted = false;
        Level = Round(Math.Clamp(percent, 0, 100));
        Number.Refresh();
        return SendAsync(Level / 100, muted: false, () => _player.SetVolumeCommand.ExecuteAsync(Level / 100)); // a volume unmutes
    }

    /// <summary>The bar: a level in percent, or in decibels (<see cref="DecibelBarMinimum"/> is silence).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetBar(double value)
        => SetLevel(!InDecibels ? value : value <= DecibelBarMinimum ? 0 : TypedValues.FromDecibels(Math.Min(value, DecibelBarMaximum)));

    /// <summary>
    /// Ctrl+↑/↓: one step to the next round number (by 1: 37.5 goes to 38 or 37), in the unit shown: in decibels a step
    /// is a dB, and below −60 dB the next one down is silence.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Step(double step) => SetBar(Steps.Next(BarValue, step));

    /// <summary>↑/↓ in the box (by 10 with Shift): steps, and shows the new value selected.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StepInBox(double step)
    {
        var task = Step(step);
        Number.ShowValue();
        await task.ConfigureAwait(true);
    }

    /// <summary>The speaker: mutes, keeping the level; unmutes, back to it (at 0%, to <see cref="UnmuteLevel"/>).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleMute()
    {
        if (Level == 0)
        {
            return SetLevel(UnmuteLevel);
        }

        IsMuted = !IsMuted;
        var muted = IsMuted;
        return SendAsync(Level / 100, muted, () => _player.SetMutedCommand.ExecuteAsync(muted));
    }

    /// <summary>
    /// The unit changed (Settings › Audio): the level is the player's again, rounded in the new unit, and the box shows
    /// it. Nothing is sent: the player's volume stays. While a change is on its way, the level it set stays.
    /// </summary>
    partial void OnInDecibelsChanged(bool value)
    {
        if (_sent is null)
        {
            Level = Round(_player.Volume * 100);
        }

        Number.Refresh();
    }

    /// <summary>A level below 100% to 0.1 dB, never rounded up to 0 dB; 0 dB at 100%.</summary>
    private static double Decibels(double percent) => TypedValues.DecibelsOf(percent);

    private string FormatShown(double percent) => InDecibels ? FormatDecibels(percent) : Format(percent);

    /// <summary>To the unit shown: 0.1% in percent, 0.1 dB in decibels.</summary>
    private double Round(double percent) => InDecibels ? TypedValues.RoundToDecibels(percent) : TypedValues.RoundVolume(percent);

    private bool ApplyTyped(string text)
    {
        if (!TypedValues.TryVolume(text, Level, InDecibels, out var percent))
        {
            return false;
        }

        _ = SetLevel(percent);
        return true;
    }

    /// <summary>
    /// Sends a volume or a mute that leaves the player at <paramref name="volume"/>, <paramref name="muted"/>. Until a
    /// status says so, statuses saying otherwise (a poll already on its way) are ignored, so they can't undo a mute or
    /// make a drag jump back. Once the player has answered, the next status counts whatever it says (something else may
    /// have changed the volume meanwhile).
    /// </summary>
    private async Task SendAsync(double volume, bool muted, Func<Task> send)
    {
        var sending = ++_sends;
        (_sent, _answered) = ((volume, muted), false);
        try
        {
            await send().ConfigureAwait(true);
        }
        finally
        {
            if (sending == _sends)
            {
                _answered = true;
            }
        }
    }

    /// <summary>
    /// The player's volume is a float: the one sent, read back, differs by its rounding at most. Relative, so two levels
    /// a tenth of a dB apart near −60 dB (0.1% and 0.101%) still differ.
    /// </summary>
    private static bool SameVolume(double a, double b) => Math.Abs(a - b) <= (Math.Max(a, b) * 1e-5) + 1e-9;

    /// <summary>Follows the player: its level, and its mute (a volume set elsewhere, the CLI's, unmutes it).</summary>
    private void Sync()
    {
        if (_sent is { } sent)
        {
            if ((!SameVolume(_player.Volume, sent.Volume) || _player.Muted != sent.Muted) && !_answered)
            {
                return;
            }

            _sent = null;
        }

        IsMuted = _player.Muted;
        Level = Round(_player.Volume * 100);
        Number.Refresh();
    }
}
