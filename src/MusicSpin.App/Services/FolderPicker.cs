using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace MusicSpin.App.Services;

/// <summary>The OS's folder picker (an OS boundary: tests use a fake).</summary>
public interface IFolderPicker
{
    /// <summary>The folder chosen, as a local path; null when the picker was cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}

/// <summary>The picker of the platform the app runs on, over the main window.</summary>
public sealed class AvaloniaFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false })
            .ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
