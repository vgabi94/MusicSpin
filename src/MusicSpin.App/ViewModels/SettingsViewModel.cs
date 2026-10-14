using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core;
using MusicSpin.Core.Settings;

namespace MusicSpin.App.ViewModels;

/// <summary>One option of a segmented control: what it shows.</summary>
public abstract record Choice(string Label);

/// <summary>One option of a segmented control and its value.</summary>
public sealed record Choice<T>(string Label, T Value) : Choice(Label);

/// <summary>
/// The Settings dialog (the player bar's cog). Every setting applies at once and is saved to <c>settings.json</c>; there
/// is no Save button. The theme and Motion go to <see cref="IAppearance"/>, the speed and Motion to the spin clock; the
/// design, tilt and depth are read by Now Playing and the shelf.
/// </summary>
public sealed partial class SettingsViewModel : LayerViewModel
{
    public const double MinTilt = 20;
    public const double MaxTilt = 80;
    public const double MinDepth = 0;
    public const double MaxDepth = 100;

    /// <summary>The sliders' arrow-key step, and the wheel's.</summary>
    public const double SliderStep = 1;

    /// <summary>The sliders' Page Up/Down step, and the wheel's with Shift.</summary>
    public const double LargeSliderStep = 10;

    private readonly ISettingsService _settings;
    private readonly IAppearance _appearance;
    private readonly SpinClock _spinClock;
    private readonly Notices _notices;
    private readonly bool _ready;

    public SettingsViewModel(ISettingsService settings, IAppearance appearance, SpinClock spinClock, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(spinClock);
        ArgumentNullException.ThrowIfNull(notices);
        _settings = settings;
        _appearance = appearance;
        _spinClock = spinClock;
        _notices = notices;

        // Setting each property applies it; nothing is saved until the user changes something.
        var current = settings.Current;
        var defaults = new AppSettings();
        ThemeChoice = Pick(Themes, Appearance.ParseTheme(current.Theme));
        MotionChoice = Pick(Motions, MotionModes.Parse(current.Motion));
        SpeedChoice = Pick(Speeds, current.RecordSpeedRpm);
        Design = DeckDesigns.Parse(current.TurntableDesign);
        ShelfTilt = Whole(current.ShelfTilt, MinTilt, MaxTilt, defaults.ShelfTilt);
        ShelfDepth = Whole(current.ShelfDepth, MinDepth, MaxDepth, defaults.ShelfDepth);
        _ready = true;
    }

    public override int Order => LayerOrder.Settings;

    public IReadOnlyList<Choice<AppTheme>> Themes { get; } = [new("Dark", AppTheme.Dark), new("Light", AppTheme.Light)];

    public IReadOnlyList<Choice<MotionMode>> Motions { get; } =
        [new("Smooth", MotionMode.Smooth), new("Fast", MotionMode.Fast), new("None", MotionMode.None)];

    /// <summary>Records turn at 33⅓ (the default), 45 or 16⅔ rpm.</summary>
    public IReadOnlyList<Choice<double>> Speeds { get; } = [new("33⅓", 33.333), new("45", 45), new("16⅔", 16.667)];

    public IReadOnlyList<DeckDesign> Designs { get; } = DeckDesigns.All;

    /// <summary>
    /// The theme's segment. The four selections ignore null: a list box clears its selection when it goes away, and that
    /// is not the user picking nothing.
    /// </summary>
    public Choice<AppTheme> ThemeChoice
    {
        get;
        set
        {
            if (Change(ref field, value))
            {
                _appearance.SetTheme(value.Value);
                OnPropertyChanged(nameof(Theme));
                Save(s => s with { Theme = value.Value == AppTheme.Light ? "light" : "dark" });
            }
        }
    } = null!;

    public Choice<MotionMode> MotionChoice
    {
        get;
        set
        {
            if (Change(ref field, value))
            {
                _appearance.SetMotion(value.Value);
                _spinClock.MotionMultiplier = MotionModes.Multiplier(value.Value);
                OnPropertyChanged(nameof(Motion));
                Save(s => s with { Motion = MotionModes.ToSetting(value.Value) });
            }
        }
    } = null!;

    public Choice<double> SpeedChoice
    {
        get;
        set
        {
            if (Change(ref field, value))
            {
                _spinClock.Rpm = value.Value;
                OnPropertyChanged(nameof(RecordSpeed));
                Save(s => s with { RecordSpeedRpm = value.Value });
            }
        }
    } = null!;

    /// <summary>The turntable in Now Playing.</summary>
    public DeckDesign Design
    {
        get;
        set
        {
            if (Change(ref field, value))
            {
                Save(s => s with { TurntableDesign = value.Id });
            }
        }
    } = DeckDesigns.Default;

    /// <summary>The shelf's tilt in degrees, 20..80.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShelfTiltText))]
    public partial double ShelfTilt { get; private set; }

    /// <summary>The shelf's perspective depth, 0 (flat) .. 100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShelfDepthText))]
    public partial double ShelfDepth { get; private set; }

    public AppTheme Theme => ThemeChoice.Value;

    public MotionMode Motion => MotionChoice.Value;

    /// <summary>33.333, 45 or 16.667 rpm.</summary>
    public double RecordSpeed => SpeedChoice.Value;

    /// <summary>"58°".</summary>
    public string ShelfTiltText => string.Create(CultureInfo.InvariantCulture, $"{ShelfTilt:0}°");

    /// <summary>"40".</summary>
    public string ShelfDepthText => ShelfDepth.ToString("0", CultureInfo.InvariantCulture);

    [RelayCommand]
    public void SetTheme(AppTheme theme) => ThemeChoice = Pick(Themes, theme);

    [RelayCommand]
    public void SetMotion(MotionMode motion) => MotionChoice = Pick(Motions, motion);

    /// <summary>33.333, 45 or 16.667; other speeds fall back to 33⅓.</summary>
    [RelayCommand]
    public void SetRecordSpeed(double rpm) => SpeedChoice = Pick(Speeds, rpm);

    /// <summary>The tilt slider: whole degrees, 20..80.</summary>
    [RelayCommand]
    public void SetShelfTilt(double degrees) => ShelfTilt = Whole(degrees, MinTilt, MaxTilt, ShelfTilt);

    /// <summary>The depth slider: whole numbers, 0..100.</summary>
    [RelayCommand]
    public void SetShelfDepth(double depth) => ShelfDepth = Whole(depth, MinDepth, MaxDepth, ShelfDepth);

    partial void OnShelfTiltChanged(double value) => Save(s => s with { ShelfTilt = value });

    partial void OnShelfDepthChanged(double value) => Save(s => s with { ShelfDepth = value });

    private static Choice<T> Pick<T>(IReadOnlyList<Choice<T>> choices, T value)
        => choices.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value)) ?? choices[0];

    private static double Whole(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Round(Math.Clamp(value, min, max)) : fallback;

    private bool Change<T>(ref T field, T? value, [CallerMemberName] string? name = null)
        where T : class
        => value is not null && SetProperty(ref field, value, name);

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            _settings.Update(change);
        }
        catch (MusicSpinException ex)
        {
            _notices.Post($"The setting applies now but wasn't saved: {ex.Message}");
        }
    }
}
