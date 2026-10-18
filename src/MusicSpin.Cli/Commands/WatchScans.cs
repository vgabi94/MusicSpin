using MusicSpin.Core;

namespace MusicSpin.Cli.Commands;

/// <summary>
/// <c>music-spin library watch</c>'s scans: one at a time, in the order the changes came, and none once the watch stops.
/// A change can still arrive after <see cref="StopAsync"/> (the watcher's debounce timer may have flushed it just as the
/// watch stopped); it finds the watch stopped and does nothing.
/// </summary>
/// <param name="scan">Scans the library after the changes at these paths; the token is the watch's own.</param>
/// <param name="failed">A scan failed (the library busy or unreadable, say): the watch reports it and goes on.</param>
/// <param name="stopping">Ctrl+C: the scan under way ends early, and no other starts.</param>
#pragma warning disable CA1001 // its one disposable, the turn lock, is deliberately never disposed (see _turn)
internal sealed class WatchScans(Func<IReadOnlyCollection<string>, CancellationToken, Task> scan, Action<MusicSpinException> failed, CancellationToken stopping)
{
    // Deliberately never disposed: a change arriving after the watch stopped still takes its turn here, and a disposed
    // SemaphoreSlim would throw ObjectDisposedException in the watcher's handler. (It holds nothing to free: its wait
    // handle is never created.)
    private readonly SemaphoreSlim _turn = new(1, 1);
    private volatile bool _stopped;

    private bool IsStopping => _stopped || stopping.IsCancellationRequested;

    /// <summary>Scans once the scan before it is done, unless the watch is stopping by then.</summary>
    public async Task ScanAsync(IReadOnlyCollection<string> paths)
    {
        await _turn.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsStopping)
            {
                return;
            }

            await scan(paths, stopping).ConfigureAwait(false);
        }
        catch (MusicSpinException ex)
        {
            failed(ex);
        }
        catch (OperationCanceledException) when (IsStopping)
        {
            // Ctrl+C mid-scan: the watch is stopping anyway
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// Stops the watch: completes once the scan under way, and any change already waiting its turn, are done. Changes
    /// after this do nothing.
    /// </summary>
    public async Task StopAsync()
    {
        _stopped = true;
        await _turn.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _turn.Release(); // a change still to come takes its turn, finds the watch stopped and does nothing
    }
}
#pragma warning restore CA1001
