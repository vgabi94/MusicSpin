using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// The sidebar. Renaming a playlist in place: its text box takes the focus with the name selected; Enter saves (the
/// window's Esc cancels, as it closes the topmost layer), and so does a click elsewhere, which takes the focus away.
/// What is dragged asks <see cref="DropTargetAt"/> which playlist (or Favorites) it is over.
/// </summary>
public partial class SidebarView : UserControl
{
    private SidebarViewModel? _viewModel;

    public SidebarView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }

    /// <summary>The entry at <paramref name="point"/> (in this view) that takes drops (Favorites or a playlist), if any.</summary>
    public NavItemViewModel? DropTargetAt(Point point)
        => this.GetVisualDescendants().OfType<Panel>()
            .Where(p => p.Classes.Contains("navItem") && p.DataContext is NavItemViewModel { TakesDrops: true } && p.IsEffectivelyVisible)
            .FirstOrDefault(p => p.TransformToVisual(this) is { } transform && new Rect(p.Bounds.Size).TransformToAABB(transform).Contains(point))
            ?.DataContext as NavItemViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.Rename.Started -= OnRenameStarted;
        _viewModel = DataContext as SidebarViewModel;
        _viewModel?.Rename.Started += OnRenameStarted;
    }

    /// <summary>The entry's text box takes the focus with the name selected, so typing replaces it.</summary>
    private void OnRenameStarted(object? sender, NavItemViewModel item)
        => Dispatcher.UIThread.Post(() =>
        {
            if (RenameBox(item) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Loaded);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { DataContext: NavItemViewModel { IsRenaming: true } }
            && TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel window)
        {
            e.Handled = true;
            window.Playlists.CommitRenameCommand.Execute(null);
        }
    }

    /// <summary>A click elsewhere (or Tab) leaves the box: the rename is cancelled.</summary>
    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: NavItemViewModel item } && _viewModel?.Rename is { IsOpen: true } rename && rename.Item == item)
        {
            rename.Close();
        }
    }

    private TextBox? RenameBox(NavItemViewModel item)
        => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("rename") && t.DataContext == item);
}
