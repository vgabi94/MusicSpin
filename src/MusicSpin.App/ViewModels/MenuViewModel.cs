using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>One choice in a menu; <see cref="IsChosen"/> marks the current option of a dropdown with a ✓.</summary>
public sealed class MenuEntryViewModel(string label, Func<Task> run, bool isChosen = false)
{
    public string Label { get; } = label;

    public bool IsChosen { get; } = isChosen;

    public Func<Task> Run { get; } = run;

    /// <summary>A submenu ("Add to playlist ›"): choosing the entry shows these in the menu's place, read then.</summary>
    public Func<IReadOnlyList<MenuEntryViewModel>>? Submenu { get; private init; }

    public bool HasSubmenu => Submenu is not null;

    /// <summary>It deletes something (a confirmation's Delete): drawn in the danger colour.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>An entry that does something at once.</summary>
    public static MenuEntryViewModel Of(string label, Action run, bool isChosen = false)
        => new(label, () =>
        {
            run();
            return Task.CompletedTask;
        }, isChosen);

    /// <summary>An entry that opens <paramref name="submenu"/>.</summary>
    public static MenuEntryViewModel Opening(string label, Func<IReadOnlyList<MenuEntryViewModel>> submenu)
        => new(label, () => Task.CompletedTask) { Submenu = submenu };
}

/// <summary>
/// The window's menu (mockup: .menu): the Sort and Group dropdowns and the ＋ buttons fill it and open it; the window
/// places it under the button that opened it. Choosing an entry closes it; so do Esc and a click outside. An entry with a
/// submenu shows the submenu instead, in the same place; a confirmation has a caption over its entries.
/// </summary>
public sealed partial class MenuViewModel : LayerViewModel
{
    public override int Order => LayerOrder.Menu;

    public ObservableCollection<MenuEntryViewModel> Entries { get; } = [];

    /// <summary>A line over the entries ("Delete “Evening”?"), or null.</summary>
    [ObservableProperty]
    public partial string? Caption { get; private set; }

    /// <summary>
    /// Opens the menu with <paramref name="entries"/>, under <paramref name="caption"/> if given (a menu already open
    /// closes first, so it moves).
    /// </summary>
    public void Show(IEnumerable<MenuEntryViewModel> entries, string? caption = null)
    {
        Close();
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        Caption = caption;
        Open();
    }

    [RelayCommand]
    private Task Choose(MenuEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Submenu is { } submenu)
        {
            Show(submenu());
            return Task.CompletedTask;
        }

        Close();
        return entry.Run();
    }
}
