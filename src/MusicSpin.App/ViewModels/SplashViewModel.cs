using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicSpin.App.ViewModels;

/// <summary>The startup splash screen (#226): app name and loading state.</summary>
public sealed partial class SplashViewModel : ObservableObject
{
    public const string AppName = AboutViewModel.AppName;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;
}
