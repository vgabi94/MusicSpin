using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Data;
using Avalonia.Media;

namespace MusicSpin.App.Theming;

/// <summary>
/// Transitions for styles: <c>&lt;Setter Property="theming:Animate.Transitions" Value="Background HoverDuration" /&gt;</c>.
/// </summary>
/// <remarks>
/// A <see cref="Transitions"/> object set by a style setter is one instance shared by every control the style matches,
/// and a shared transition leaves later controls stuck mid-animation (seen with brushes once the first window closed).
/// This property takes a text spec instead and builds each control its own transitions. The spec is
/// <c>Property DurationKey [EasingKey [DelayKey]]</c>, several separated by <c>;</c>. Durations, easings and delays
/// are resources from Styles/Motion.axaml; durations and delays follow the Motion setting like <see cref="MotionExtension"/>.
/// </remarks>
public static class Animate
{
    public static readonly AttachedProperty<string?> TransitionsProperty =
        AvaloniaProperty.RegisterAttached<Animatable, string?>("Transitions", typeof(Animate));

    /// <summary>
    /// An animation from Styles/Motion.axaml to play on the control each time <see cref="TriggerProperty"/> changes:
    /// <c>theming:Animate.Play="ShakeAnimation" theming:Animate.Trigger="{Binding Number.Rejections}"</c>. Its duration
    /// follows the Motion setting; at None it doesn't play.
    /// </summary>
    public static readonly AttachedProperty<string?> PlayProperty =
        AvaloniaProperty.RegisterAttached<Animatable, string?>("Play", typeof(Animate));

    public static readonly AttachedProperty<object?> TriggerProperty =
        AvaloniaProperty.RegisterAttached<Animatable, object?>("Trigger", typeof(Animate));

    /// <summary>
    /// Plays <see cref="PlayProperty"/> each time this turns true, also when the control comes on screen with it true (a
    /// list row made for an item that is already marked): <c>theming:Animate.When="{Binding IsFresh}"</c>.
    /// </summary>
    public static readonly AttachedProperty<bool> WhenProperty =
        AvaloniaProperty.RegisterAttached<Animatable, bool>("When", typeof(Animate));

    static Animate()
    {
        TransitionsProperty.Changed.AddClassHandler<Animatable>((target, e) => Apply(target, e.NewValue as string));
        TriggerProperty.Changed.AddClassHandler<Animatable>((target, e) =>
        {
            if (e.OldValue is not null && target is Visual visual)
            {
                _ = PlayAsync(visual);
            }
        });
        WhenProperty.Changed.AddClassHandler<Animatable>((target, e) =>
        {
            if (e.NewValue is true && target is Visual visual)
            {
                PlayOnScreen(visual);
            }
        });
    }

    /// <summary>How many times <see cref="PlayProperty"/> played on the control (tests count it).</summary>
    public static readonly AttachedProperty<int> PlayedProperty =
        AvaloniaProperty.RegisterAttached<Animatable, int>("Played", typeof(Animate));

    public static int GetPlayed(Animatable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.GetValue(PlayedProperty);
    }

    public static string? GetPlay(Animatable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.GetValue(PlayProperty);
    }

    public static void SetPlay(Animatable target, string? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetValue(PlayProperty, value);
    }

    public static object? GetTrigger(Animatable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.GetValue(TriggerProperty);
    }

    public static void SetTrigger(Animatable target, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetValue(TriggerProperty, value);
    }

    public static bool GetWhen(Animatable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.GetValue(WhenProperty);
    }

    public static void SetWhen(Animatable target, bool value)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetValue(WhenProperty, value);
    }

    private static void PlayOnScreen(Visual target)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(target) is not null)
        {
            _ = PlayAsync(target);
            return;
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            target.AttachedToVisualTree -= OnAttached;
            if (GetWhen(target))
            {
                _ = PlayAsync(target);
            }
        }

        target.AttachedToVisualTree += OnAttached;
    }

    private static async Task PlayAsync(Visual target)
    {
        if (GetPlay(target) is not { } key || MotionScale.Current.Multiplier <= 0)
        {
            return;
        }

        var animation = Resource<Animation>(key);
        var duration = Resource<TimeSpan>(key + ".Duration");
        animation.Duration = MotionScale.Current.Scale(duration);
        target.SetValue(PlayedProperty, target.GetValue(PlayedProperty) + 1);
        await animation.RunAsync(target).ConfigureAwait(true);
    }

    public static string? GetTransitions(Animatable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.GetValue(TransitionsProperty);
    }

    public static void SetTransitions(Animatable target, string? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetValue(TransitionsProperty, value);
    }

    private static void Apply(Animatable target, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            target.Transitions = null;
            return;
        }

        var transitions = new Transitions();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            transitions.Add(Create(target, part.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        target.Transitions = transitions;
    }

    private static TransitionBase Create(Animatable target, string[] words)
    {
        if (words.Length is < 2 or > 4)
        {
            throw new FormatException($"Animate.Transitions: '{string.Join(' ', words)}' is not 'Property DurationKey [EasingKey [DelayKey]]'.");
        }

        var property = AvaloniaPropertyRegistry.Instance.FindRegistered(target, words[0])
                       ?? throw new FormatException($"Animate.Transitions: {target.GetType().Name} has no property '{words[0]}'.");
        TransitionBase transition = property.PropertyType switch
        {
            var t when t == typeof(IBrush) => new BrushTransition(),
            var t when t == typeof(double) => new DoubleTransition(),
            var t when t == typeof(ITransform) => new TransformOperationsTransition(),
            var t when t == typeof(Thickness) => new ThicknessTransition(),
            var t => throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Animate.Transitions: no transition for {t.Name}.")),
        };
        transition.Property = property;
        transition.Bind(TransitionBase.DurationProperty, (BindingBase)new MotionExtension(words[1]).ProvideValue(null!));
        if (words.Length >= 3)
        {
            transition.Easing = Resource<Easing>(words[2]);
        }

        if (words.Length == 4)
        {
            transition.Bind(TransitionBase.DelayProperty, (BindingBase)new MotionExtension(words[3]).ProvideValue(null!));
        }

        return transition;
    }

    private static T Resource<T>(string key)
        => Application.Current?.TryGetResource(key, Avalonia.Styling.ThemeVariant.Default, out var value) == true && value is T found
            ? found
            : throw new FormatException($"Animate.Transitions: no {typeof(T).Name} resource '{key}' (see Styles/Motion.axaml).");
}
