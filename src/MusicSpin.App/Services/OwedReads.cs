using MusicSpin.Core;

namespace MusicSpin.App.Services;

/// <summary>
/// Background reads the busy library refused (another process, a scan say, held it for longer than the lock timeout):
/// each is owed, tried again on the UI thread when the library is next checked for changes (see
/// <see cref="LibraryService.PollInterval"/>), whether or not the holder changed anything. A retry that is refused again
/// owes itself again. Used on the UI thread only.
/// </summary>
public sealed class OwedReads(TimeProvider time, IUiDispatcher ui) : IDisposable
{
    private readonly List<Action> _owed = [];
    private ITimer? _check;
    private bool _disposed;

    /// <summary>Whether <paramref name="ex"/> is the busy library's refusal (the only failure that is owed, not shown).</summary>
    public static bool IsBusy(Exception ex) => ex is MusicSpinException { Code: MusicSpinErrorCode.LibraryLocked };

    /// <summary>Runs <paramref name="retry"/> at the next check (it decides then whether it is still wanted).</summary>
    public void Owe(Action retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (_disposed)
        {
            return;
        }

        _owed.Add(retry);
        _check ??= time.CreateTimer(_ => ui.Post(RunOwed), null, LibraryService.PollInterval, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _disposed = true;
        _check?.Dispose();
        _check = null;
        _owed.Clear();
    }

    private void RunOwed()
    {
        _check?.Dispose();
        _check = null;
        if (_disposed)
        {
            return;
        }

        var owed = _owed.ToArray();
        _owed.Clear();
        foreach (var retry in owed)
        {
            retry();
        }
    }
}
