using Avalonia.Headless.XUnit;
using MusicSpin.App;

namespace MusicSpin.Tests.Ui;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Main_window_can_be_created_and_shown_headlessly()
    {
        var window = new MainWindow();
        window.Show();

        Assert.True(window.IsVisible);
        Assert.Equal("MusicSpin", window.Title);

        window.Close();
    }
}
