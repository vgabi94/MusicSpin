using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// The About dialog. Opening it moves the keyboard focus into it (to ✕, so Tab goes round its links and Enter or Esc
/// closes it); closing it gives the focus back to where it was.
/// </summary>
public partial class AboutDialog : UserControl
{
    private AboutViewModel? _viewModel;
    private IInputElement? _returnFocus;

    public AboutDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as AboutViewModel;
        _viewModel?.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AboutViewModel.IsOpen) || TopLevel.GetTopLevel(this)?.FocusManager is not { } focus)
        {
            return;
        }

        if (_viewModel!.IsOpen)
        {
            _returnFocus = focus.GetFocusedElement();
            // after the dialog is shown (the window shows it on the same change)
            Dispatcher.UIThread.Post(() => CloseAbout.Focus(), DispatcherPriority.Loaded);
            return;
        }

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
