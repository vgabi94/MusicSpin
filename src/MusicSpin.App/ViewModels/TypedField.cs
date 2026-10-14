using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// A text box that shows a value and takes a typed one (the elapsed time, the volume). It shows the value until it gets
/// focus; then it holds what is typed (the value stops updating in it). Enter applies a valid entry, or marks it invalid
/// (red outline, a shake) and stays open; Esc cancels; leaving applies an edited valid entry and drops anything else.
/// </summary>
/// <remarks>
/// The view connects it (Controls/TypedBox.cs): focus calls <see cref="Begin"/> and <see cref="End"/>, and the view moves
/// focus away on <see cref="Finished"/> and selects the text on <see cref="SelectionRequested"/>.
/// </remarks>
public sealed partial class TypedField : ObservableObject
{
    private readonly Func<string> _format;
    private readonly Func<string, bool> _apply;
    private bool _edited;
    private bool _showing;

    /// <param name="format">The current value as text.</param>
    /// <param name="apply">Applies typed text; false when it isn't valid.</param>
    public TypedField(Func<string> format, Func<string, bool> apply)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(apply);
        _format = format;
        _apply = apply;
        Show(format());
    }

    /// <summary>The view should move focus away from the box (Enter applied it, or Esc).</summary>
    public event EventHandler? Finished;

    /// <summary>The view should select the box's text (an entry was refused, or a step changed it).</summary>
    public event EventHandler? SelectionRequested;

    /// <summary>The box's text (bound two-way).</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>The last Enter was refused; cleared by typing.</summary>
    [ObservableProperty]
    public partial bool IsInvalid { get; private set; }

    /// <summary>Counts refusals: the box shakes on each.</summary>
    [ObservableProperty]
    public partial int Rejections { get; private set; }

    /// <summary>The value changed: shows it, unless the box is being edited.</summary>
    public void Refresh()
    {
        if (!IsEditing)
        {
            Show(_format());
        }
    }

    /// <summary>Shows the current value even while editing (after a step from the box), selected.</summary>
    public void ShowValue()
    {
        Show(_format());
        _edited = false;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The box got focus.</summary>
    public void Begin()
    {
        IsEditing = true;
        _edited = false;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The box lost focus: an edited valid entry applies; anything else is dropped.</summary>
    public void End()
    {
        if (!IsEditing)
        {
            return;
        }

        if (_edited)
        {
            _apply(Text);
        }

        IsEditing = false;
        IsInvalid = false;
        _edited = false;
        Show(_format());
    }

    /// <summary>Enter.</summary>
    [RelayCommand]
    private void Commit()
    {
        if (_apply(Text))
        {
            _edited = false;
            Finish();
            return;
        }

        IsInvalid = true;
        Rejections++;
        SelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _edited = false;
        Finish();
    }

    partial void OnTextChanged(string value)
    {
        if (IsEditing && !_showing)
        {
            _edited = true;
            IsInvalid = false;
        }
    }

    private void Finish()
    {
        End();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void Show(string text)
    {
        _showing = true;
        try
        {
            Text = text;
        }
        finally
        {
            _showing = false;
        }
    }
}
