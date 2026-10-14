namespace MusicSpin.App.Services;

/// <summary>Problems to show the user without blocking the UI (a banner); nothing here throws.</summary>
public sealed class Notices
{
    public event EventHandler<string>? Posted;

    public void Post(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Posted?.Invoke(this, message);
        }
    }

    /// <summary>Runs <paramref name="action"/>, turning a library or player failure into a notice.</summary>
    public async Task GuardAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Core.MusicSpinException or IOException or UnauthorizedAccessException)
        {
            Post(ex.Message);
        }
    }
}
