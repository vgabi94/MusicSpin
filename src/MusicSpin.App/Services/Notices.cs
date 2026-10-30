namespace MusicSpin.App.Services;

/// <summary>Problems to show the user without blocking the UI (a banner); nothing here throws.</summary>
public sealed class Notices(AppLog? log = null)
{
    private readonly AppLog _log = log ?? AppLog.None;

    public event EventHandler<string>? Posted;

    public void Post(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Posted?.Invoke(this, message);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/>, turning a library or player failure into a notice. Any other failure is a bug: it
    /// becomes a notice too, with its stack trace in <c>app.log</c>, instead of ending the app (#185). A cancellation
    /// still reaches the caller.
    /// </summary>
    public async Task GuardAsync(Func<Task> action)
    {
        if (await TryAsync(action).ConfigureAwait(true) is { } problem)
        {
            Post(problem);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> as <see cref="GuardAsync"/> does, but returns what went wrong (null: nothing) for
    /// the caller to show in place instead of in a notice (a Settings page that couldn't load, #188).
    /// </summary>
    public async Task<string?> TryAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(true);
            return null;
        }
        catch (Exception ex) when (ex is Core.MusicSpinException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write("unexpected failure", ex);
            return _log.File is { } file
                ? $"Something went wrong: {ex.Message} The details are in {file}."
                : $"Something went wrong: {ex.Message}";
        }
    }
}
