using Avalonia.Controls;

namespace MusicSpin.App.Views;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        ClearSearch.Click += (_, _) => SearchBox.Focus();
    }
}
