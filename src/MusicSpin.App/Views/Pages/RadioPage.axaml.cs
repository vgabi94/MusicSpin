using Avalonia.Controls;
using Avalonia.Input;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views.Pages;

/// <summary>The Radio page. A right-click on a tag leaves it out at once (the view model decides what that does).</summary>
public partial class RadioPage : UserControl
{
    public RadioPage() => InitializeComponent();

    private void OnTagContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: RadioChipViewModel chip })
        {
            chip.LeaveOutCommand.Execute(null);
            e.Handled = true;
        }
    }
}
