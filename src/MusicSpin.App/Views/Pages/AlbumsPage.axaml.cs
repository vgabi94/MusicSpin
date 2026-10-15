using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views.Pages;

public partial class AlbumsPage : UserControl
{
    private AlbumsPageViewModel? _viewModel;

    public AlbumsPage()
    {
        InitializeComponent();
        AlbumGrid.SizeChanged += (_, e) => ReportWidth(e.NewSize.Width);
        CrateGrid.SizeChanged += (_, e) => ReportCrateColumns(e.NewSize.Width);
        DataContextChanged += (_, _) => OnViewModelChanged();
    }

    private void OnViewModelChanged()
    {
        _viewModel?.Settings.PropertyChanged -= OnSettingsChanged;
        _viewModel = DataContext as AlbumsPageViewModel;
        _viewModel?.Settings.PropertyChanged += OnSettingsChanged;
        ReportWidth(AlbumGrid.Bounds.Width);
        ReportCrateColumns(CrateGrid.Bounds.Width);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ShelfTilt))
        {
            ReportPerBoard(AlbumGrid.Bounds.Width);
        }
    }

    private void ReportWidth(double width)
    {
        ReportColumns(width);
        ReportPerBoard(width);
    }

    /// <summary>
    /// The mockup's grid: as many columns of at least CardMinWidth as fit with CardColumnGap between them (CSS
    /// repeat(auto-fill, minmax(176px, 1fr))).
    /// </summary>
    private void ReportColumns(double width)
    {
        if (_viewModel is { } viewModel && width > 0 && Resource<double>("CardMinWidth") is { } minWidth && Resource<double>("CardColumnGap") is { } gap)
        {
            viewModel.Columns = Math.Max(1, (int)((width + gap) / (minWidth + gap)));
        }
    }

    /// <summary>The mockup's shelfPerRow: as many sleeves as fit on a board at the current tilt (see <see cref="ShelfGeometry"/>).</summary>
    private void ReportPerBoard(double width)
    {
        if (_viewModel is { } viewModel && width > 0 && Resource<double>("ShelfSleeveSize") is { } size && Resource<Thickness>("ShelfRowPadding") is { } padding)
        {
            viewModel.PerBoard = ShelfGeometry.PerBoard(width - padding.Left - padding.Right, size, viewModel.Settings.ShelfTilt);
        }
    }

    /// <summary>Grouped crates: as many cells of at least CrateCellMinWidth as fit (mockup: minmax(250px, 1fr)).</summary>
    private void ReportCrateColumns(double width)
    {
        if (_viewModel is { } viewModel && width > 0 && Resource<double>("CrateCellMinWidth") is { } minWidth && Resource<double>("CrateCellColumnGap") is { } gap)
        {
            viewModel.CrateColumns = Math.Max(1, (int)((width + gap) / (minWidth + gap)));
        }
    }

    /// <summary>The wheel over a crate flips it; past either end it's left to the page, which scrolls.</summary>
    private void OnCrateWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is { } viewModel && sender is Control { DataContext: CrateViewModel crate })
        {
            e.Handled = viewModel.WheelCrate(crate, -e.Delta.Y);
        }
    }

    private T? Resource<T>(string key)
        where T : struct
        => this.TryFindResource(key, out var value) && value is T found ? found : null;
}
