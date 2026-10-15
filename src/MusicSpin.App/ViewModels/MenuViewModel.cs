using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>One choice in a menu; <see cref="IsChosen"/> marks the current option of a dropdown with a ✓.</summary>
public sealed class MenuEntryViewModel(string label, Func<Task> run, bool isChosen = false)
{
    public string Label { get; } = label;

    public bool IsChosen { get; } = isChosen;

    public Func<Task> Run { get; } = run;

    /// <summary>An entry that does something at once.</summary>
    public static MenuEntryViewModel Of(string label, Action run, bool isChosen = false)
        => new(label, () =>
        {
            run();
            return Task.CompletedTask;
        }, isChosen);
}

/// <summary>
/// The window's menu (mockup: .menu): the Sort and Group dropdowns and the ＋ buttons fill it and open it; the window
/// places it under the button that opened it. Choosing an entry closes it; so do Esc and a click outside.
/// </summary>
public sealed partial class MenuViewModel : LayerViewModel
{
    public override int Order => LayerOrder.Menu;

    public ObservableCollection<MenuEntryViewModel> Entries { get; } = [];

    /// <summary>Opens the menu with <paramref name="entries"/> (a menu already open closes first, so it moves).</summary>
    public void Show(IEnumerable<MenuEntryViewModel> entries)
    {
        Close();
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        Open();
    }

    [RelayCommand]
    private Task Choose(MenuEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Close();
        return entry.Run();
    }
}
