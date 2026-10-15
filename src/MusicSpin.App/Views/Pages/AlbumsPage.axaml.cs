using Avalonia.Controls;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views.Pages;

public partial class AlbumsPage : UserControl
{
    public AlbumsPage()
    {
        InitializeComponent();
        AlbumGrid.SizeChanged += (_, e) => ReportColumns(e.NewSize.Width);
        DataContextChanged += (_, _) => ReportColumns(AlbumGrid.Bounds.Width);
    }

    /// <summary>
    /// The mockup's grid: as many columns of at least CardMinWidth as fit with CardColumnGap between them (CSS
    /// repeat(auto-fill, minmax(176px, 1fr))).
    /// </summary>
    private void ReportColumns(double width)
    {
        if (DataContext is AlbumsPageViewModel viewModel && width > 0
            && this.TryFindResource("CardMinWidth", out var min) && min is double minWidth
            && this.TryFindResource("CardColumnGap", out var gap) && gap is double columnGap)
        {
            viewModel.Columns = Math.Max(1, (int)((width + columnGap) / (minWidth + columnGap)));
        }
    }
}
