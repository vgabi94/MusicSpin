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
/// <c>Property DurationKey [EasingKey]</c>, several separated by <c>;</c>. Durations and easings are resources from
/// Styles/Motion.axaml; durations follow the Motion setting like <see cref="MotionExtension"/>.
/// </remarks>
public static class Animate
{
    public static readonly AttachedProperty<string?> TransitionsProperty =
        AvaloniaProperty.RegisterAttached<Animatable, string?>("Transitions", typeof(Animate));

    static Animate() => TransitionsProperty.Changed.AddClassHandler<Animatable>((target, e) => Apply(target, e.NewValue as string));

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
        if (words.Length is < 2 or > 3)
        {
            throw new FormatException($"Animate.Transitions: '{string.Join(' ', words)}' is not 'Property DurationKey [EasingKey]'.");
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
        if (words.Length == 3)
        {
            transition.Easing = Resource<Easing>(words[2]);
        }

        return transition;
    }

    private static T Resource<T>(string key)
        => Application.Current?.TryGetResource(key, Avalonia.Styling.ThemeVariant.Default, out var value) == true && value is T found
            ? found
            : throw new FormatException($"Animate.Transitions: no {typeof(T).Name} resource '{key}' (see Styles/Motion.axaml).");
}
