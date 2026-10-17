using MusicSpin.Cli.Commands;
using MusicSpin.Core;

namespace MusicSpin.Tests.Cli;

/// <summary>
/// <c>library watch</c>'s scans (<see cref="WatchScans"/>): one at a time, and stopping waits for the scan under way.
/// A change arriving after the watch stopped (the watcher's debounce timer flushing it just then) does nothing. Before
/// #82 the watch disposed its lock as it returned, and such a change threw from the watcher's handler. The scans here
/// are held by gates, never by delays.
/// </summary>
public sealed class WatchScansTests
{
    private static readonly string[] Paths = ["/music/a.flac"];

    /// <summary>Records the scans; each waits for the gate the test sets for it (none: it finishes at once).</summary>
    private sealed class Scanner
    {
        private readonly Queue<TaskCompletionSource> _gates = new();

        public List<int> Scans { get; } = [];

        public List<MusicSpinException> Failures { get; } = [];

        public TaskCompletionSource Started { get; private set; } = Gate();

        public static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The next scan waits for the returned gate.</summary>
        public TaskCompletionSource HoldNext()
        {
            var gate = Gate();
            _gates.Enqueue(gate);
            return gate;
        }

        /// <param name="endsEarly">A held scan ends early on Ctrl+C (as a real scan does), or finishes what it was writing.</param>
        public WatchScans Create(CancellationToken stopping, bool endsEarly = true) => new(async (paths, token) =>
        {
            Scans.Add(paths.Count);
            var started = Started;
            Started = Gate();
            started.SetResult();
            if (_gates.TryDequeue(out var gate))
            {
                await (endsEarly ? gate.Task.WaitAsync(token) : gate.Task);
            }
        }, Failures.Add, stopping);
    }

    [Fact]
    public async Task Scans_take_turns_in_the_order_the_changes_came()
    {
        var scanner = new Scanner();
        var scans = scanner.Create(TestContext.Current.CancellationToken);
        var first = scanner.HoldNext();
        var started = scanner.Started.Task;
        var one = scans.ScanAsync(["/music/a.flac"]);
        await started;

        var two = scans.ScanAsync(["/music/a.flac", "/music/b.flac"]);
        Assert.False(two.IsCompleted);
        Assert.Equal([1], scanner.Scans); // the second waits for the first

        first.SetResult();
        await Task.WhenAll(one, two);
        Assert.Equal([1, 2], scanner.Scans);
        Assert.Empty(scanner.Failures);
    }

    [Fact]
    public async Task Stopping_waits_for_the_scan_under_way()
    {
        var scanner = new Scanner();
        var scans = scanner.Create(TestContext.Current.CancellationToken);
        var gate = scanner.HoldNext();
        var started = scanner.Started.Task;
        var scan = scans.ScanAsync(Paths);
        await started;

        var stop = scans.StopAsync();
        Assert.False(stop.IsCompleted); // "stopped" isn't printed while a scan still writes the library

        gate.SetResult();
        await stop;
        await scan;
        Assert.Equal([1], scanner.Scans);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_change_after_the_watch_stopped_does_nothing_and_does_not_throw()
    {
        using var cts = new CancellationTokenSource();
        var scanner = new Scanner();
        var scans = scanner.Create(cts.Token);
        await scans.ScanAsync(Paths);
        await cts.CancelAsync();
        await scans.StopAsync();

        // The debounce timer flushed this change just as the watch stopped: it must not throw from the handler (an
        // async void one: that would crash the process) nor scan.
        await scans.ScanAsync(Paths);
        await scans.ScanAsync(Paths);
        Assert.Equal([1], scanner.Scans);
        Assert.Empty(scanner.Failures);

        await scans.StopAsync(); // stopping twice is harmless
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_change_waiting_its_turn_when_the_watch_stops_does_not_scan()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scanner = new Scanner();
        var scans = scanner.Create(cts.Token, endsEarly: false);
        var gate = scanner.HoldNext();
        var started = scanner.Started.Task;
        var first = scans.ScanAsync(Paths);
        await started;
        var waiting = scans.ScanAsync(Paths);

        await cts.CancelAsync(); // Ctrl+C while the first scan still writes
        var stop = scans.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(waiting.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(first, waiting, stop);
        Assert.Equal([1], scanner.Scans); // the waiting change found the watch stopping
        Assert.Empty(scanner.Failures);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Ctrl_C_mid_scan_ends_the_scan_quietly_and_the_watch_stops()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scanner = new Scanner();
        var scans = scanner.Create(cts.Token);
        var gate = scanner.HoldNext();
        var started = scanner.Started.Task;
        var scan = scans.ScanAsync(Paths);
        await started;

        await cts.CancelAsync(); // the held scan ends early with OperationCanceledException
        await scan; // which is the watch stopping, not a failure
        await scans.StopAsync();
        Assert.Equal([1], scanner.Scans);
        Assert.Empty(scanner.Failures);
        Assert.False(gate.Task.IsCompleted);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_failed_scan_is_reported_and_the_next_change_still_scans()
    {
        var failures = new List<MusicSpinException>();
        var scans = 0;
        var watch = new WatchScans((_, _) => ++scans == 1
                ? throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, "The library is locked by another MusicSpin process.")
                : Task.CompletedTask,
            failures.Add, CancellationToken.None);

        await watch.ScanAsync(Paths);
        Assert.Equal("The library is locked by another MusicSpin process.", Assert.Single(failures).Message);

        await watch.ScanAsync(Paths); // the failed scan let go of its turn
        Assert.Equal(2, scans);
        Assert.Single(failures);
        await watch.StopAsync(); // and stopping doesn't wait for a turn nobody gives back
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_cancellation_that_is_not_the_watch_stopping_is_not_swallowed()
    {
        var watch = new WatchScans((_, _) => throw new OperationCanceledException("Not Ctrl+C."), _ => { }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => watch.ScanAsync(Paths));
        Assert.Equal("Not Ctrl+C.", ex.Message);
        await watch.StopAsync(); // its turn was given back all the same
    }
}
