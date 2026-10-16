using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>Something that opens over the window and Esc closes (see <see cref="LayerRegistry"/>).</summary>
public abstract partial class LayerViewModel : ObservableObject, ILayer
{
    public abstract int Order { get; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>How many times it opened (views play their opening animation on each).</summary>
    [ObservableProperty]
    public partial int Opens { get; private set; }

    [RelayCommand]
    public void Open() => IsOpen = true;

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>It opened or closed, by any means.</summary>
    protected virtual void OnOpenChanged(bool isOpen)
    {
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            Opens++;
        }

        OnOpenChanged(value);
    }
}
