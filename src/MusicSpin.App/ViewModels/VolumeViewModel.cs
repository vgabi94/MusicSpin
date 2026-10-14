using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The volume (mockup: "Player bar: volume"), in percent to 0.1; the player's volume is the percent divided by 100, and
/// 100% is its bit-perfect path. The bar moves in whole percents, the box takes exact values. Mute keeps the level (the
/// player plays at 0 meanwhile); setting a level unmutes. Changes made elsewhere (the CLI) show up here.
/// </summary>
public sealed partial class VolumeViewModel : ObservableObject
{
    /// <summary>Below this the speaker shows one wave.</summary>
    public const double LowLevel = 50;

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

    public const string BitPerfectTip = "Bit-perfect: the samples are left untouched";
    public const string TypeTip = "Type a volume: 37, 37.5, +5, -10%";

    private readonly PlayerViewModel _player;
    private double? _sent;
    private bool _answered;
    private int _sends;

    public VolumeViewModel(PlayerViewModel player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        Number = new TypedField(() => Format(Level), ApplyTyped);
        player.StatusApplied += (_, _) => Sync();
        Sync();
    }

    /// <summary>The level box.</summary>
    public TypedField Number { get; }

    /// <summary>Percent, 0..100, to 0.1. Kept while muted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilent), nameof(IsLow), nameof(IsFull), nameof(NumberTip), nameof(MuteTip), nameof(ValueText))]
    public partial double Level { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSilent), nameof(IsLow), nameof(IsFull), nameof(MuteTip), nameof(ValueText))]
    public partial bool IsMuted { get; private set; }

    /// <summary>Muted, or at 0%: the speaker shows a cross and the bar and the number are dimmed.</summary>
    public bool IsSilent => IsMuted || Level == 0;

    /// <summary>Below <see cref="LowLevel"/>: the speaker shows one wave.</summary>
    public bool IsLow => !IsSilent && Level < LowLevel;

    /// <summary>At <see cref="LowLevel"/> or above: the speaker shows both waves.</summary>
    public bool IsFull => !IsSilent && !IsLow;

    public string MuteTip => IsSilent ? "Unmute" : "Mute";

    public string NumberTip => Level == 100 ? BitPerfectTip : TypeTip;

    /// <summary>For screen readers: "80%", or "Muted".</summary>
    public string ValueText => IsMuted ? "Muted" : Format(Level);

    /// <summary>"80%", "37.5%".</summary>
    public static string Format(double percent) => string.Create(CultureInfo.InvariantCulture, $"{TypedValues.RoundVolume(percent):0.#}%");

    /// <summary>Sets the level (unmuting).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetLevel(double percent)
    {
        IsMuted = false;
        Level = TypedValues.RoundVolume(Math.Clamp(percent, 0, 100));
        Number.Refresh();
        return SendAsync(Level / 100);
    }

    /// <summary>Ctrl+↑/↓: one step to the next round number (by 1: 37.5 goes to 38 or 37).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Step(double step) => SetLevel(Steps.Next(Level, step));

    /// <summary>↑/↓ in the box (by 10 with Shift): steps, and shows the new value selected.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StepInBox(double step)
    {
        var task = Step(step);
        Number.ShowValue();
        await task.ConfigureAwait(true);
    }

    /// <summary>The speaker: mutes, keeping the level; unmutes (at 0%, to <see cref="UnmuteLevel"/>).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleMute()
    {
        if (IsSilent)
        {
            return SetLevel(Level == 0 ? UnmuteLevel : Level);
        }

        IsMuted = true;
        return SendAsync(0);
    }

    private bool ApplyTyped(string text)
    {
        if (!TypedValues.TryVolume(text, Level, out var percent))
        {
            return false;
        }

        _ = SetLevel(percent);
        return true;
    }


    /// <summary>
    /// Sends a volume. Until a status carries it, statuses with another volume (a poll already on its way) are ignored, so
    /// they can't undo a mute or make a drag jump back. Once the player has answered, the next status counts whatever it
    /// says (something else may have changed the volume meanwhile).
    /// </summary>
    private async Task SendAsync(double volume)
    {
        var send = ++_sends;
        (_sent, _answered) = (volume, false);
        try
        {
            await _player.SetVolumeCommand.ExecuteAsync(volume).ConfigureAwait(true);
        }
        finally
        {
            if (send == _sends)
            {
                _answered = true;
            }
        }
    }

    /// <summary>Follows the player: while muted it plays at 0, and anything louder (the CLI) unmutes.</summary>
    private void Sync()
    {
        if (_sent is { } sent)
        {
            if (Math.Abs(_player.Volume - sent) > TypedValues.VolumeResolution / 200 && !_answered)
            {
                return;
            }

            _sent = null;
        }

        var level = TypedValues.RoundVolume(_player.Volume * 100);
        if (IsMuted && level == 0)
        {
            return;
        }

        IsMuted = false;
        Level = level;
        Number.Refresh();
    }
}
