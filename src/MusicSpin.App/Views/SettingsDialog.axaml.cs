using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// The Settings dialog. Opening it moves the keyboard focus to the chosen page's entry in the sidebar, so ↑ ↓ move
/// through the pages at once (Tab then goes into the page, and cycles inside the dialog); closing it gives the focus
/// back to where it was.
/// </summary>
public partial class SettingsDialog : UserControl
{
    private SettingsViewModel? _viewModel;
    private IInputElement? _returnFocus;

    public SettingsDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.IsOpen) || TopLevel.GetTopLevel(this)?.FocusManager is not { } focus)
        {
            return;
        }

        if (_viewModel!.IsOpen)
        {
            _returnFocus = focus.GetFocusedElement();
            // after the dialog is shown (the window shows it on the same change)
            Dispatcher.UIThread.Post(FocusChosenPage, DispatcherPriority.Loaded);
        }
        else
        {
            var back = _returnFocus;
            _returnFocus = null;
            if (back is Visual { IsEffectivelyVisible: true } && back.Focusable)
            {
                back.Focus();
            }
            else
            {
                focus.Focus(null);
            }
        }
    }

    private void FocusChosenPage()
    {
        if (SettingsPages.ContainerFromItem(SettingsPages.SelectedItem!) is { } entry)
        {
            entry.Focus();
        }
        else
        {
            CloseSettings.Focus();
        }
    }
}
