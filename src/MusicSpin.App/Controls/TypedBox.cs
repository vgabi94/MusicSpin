using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Controls;

/// <summary>
/// Connects a text box to a <see cref="TypedField"/>: <c>controls:TypedBox.Field="{Binding Volume.Number}"</c>. Focus
/// begins and ends the edit, the text is selected when the edit begins or the field asks, and the box gives up focus
/// when the field is finished. Enter and Esc are key bindings on the box, to the field's commands.
/// </summary>
public static class TypedBox
{
    public static readonly AttachedProperty<TypedField?> FieldProperty =
        AvaloniaProperty.RegisterAttached<TextBox, TypedField?>("Field", typeof(TypedBox));

    static TypedBox() => FieldProperty.Changed.AddClassHandler<TextBox>(OnFieldChanged);

    public static TypedField? GetField(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return box.GetValue(FieldProperty);
    }

    public static void SetField(TextBox box, TypedField? value)
    {
        ArgumentNullException.ThrowIfNull(box);
        box.SetValue(FieldProperty, value);
    }

    private static void OnFieldChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is null && e.NewValue is not null)
        {
            box.GotFocus += OnGotFocus;
            box.LostFocus += OnLostFocus;
        }
        else if (e.OldValue is not null && e.NewValue is null)
        {
            box.GotFocus -= OnGotFocus;
            box.LostFocus -= OnLostFocus;
        }

        if (e.OldValue is TypedField old)
        {
            old.Finished -= Handlers(box).Finished;
            old.SelectionRequested -= Handlers(box).Select;
        }

        if (e.NewValue is TypedField field)
        {
            field.Finished += Handlers(box).Finished;
            field.SelectionRequested += Handlers(box).Select;
        }
    }

    private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox box && GetField(box) is { IsEditing: false } field)
        {
            field.Begin();
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && !box.IsFocused)
        {
            GetField(box)?.End();
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, BoxHandlers> s_handlers = [];

    private static BoxHandlers Handlers(TextBox box) => s_handlers.GetValue(box, b => new BoxHandlers(b));

    /// <summary>The field's requests, for one box.</summary>
    private sealed class BoxHandlers(TextBox box)
    {
        public void Finished(object? sender, EventArgs e)
        {
            if (box.IsFocused)
            {
                TopLevel.GetTopLevel(box)?.FocusManager?.Focus(null);
            }
        }

        // After the click that focused the box has placed the caret.
        public void Select(object? sender, EventArgs e) => Dispatcher.UIThread.Post(box.SelectAll, DispatcherPriority.Input);
    }
}
