using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MusicSpin.App.Views;

/// <summary>The tray icon and its menu (#83), bound to a <see cref="ViewModels.TrayViewModel"/>.</summary>
public partial class TrayIconView : TrayIcon
{
    public TrayIconView() => AvaloniaXamlLoader.Load(this);
}
