using System.Diagnostics;

namespace MusicSpin.Tests.Shared;

/// <summary>
/// The one bounded wait the test projects share, for state that another thread (the player, the library, a child
/// process) changes without raising an event the test can await. The timeout is a failure cap only: a passing test
/// returns as soon as <c>condition</c> holds. Prefer awaiting a <see cref="TaskCompletionSource"/> where there is an event.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Returns once <paramref name="condition"/> holds; fails with <paramref name="what"/> after <paramref name="timeoutMs"/>.
    /// <paramref name="pump"/> runs before every check (e.g. to run queued UI work).
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000, Action? pump = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            pump?.Invoke();
            if (condition())
            {
                return;
            }

            if (clock.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail($"Timed out after {timeoutMs} ms waiting for: {what}");
            }

            await Task.Delay(Poll, TestContext.Current.CancellationToken); // yields between checks; never what a test waits for
        }
    }
}
