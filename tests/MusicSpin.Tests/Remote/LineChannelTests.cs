using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

/// <summary>The line channel closing while a write is still under way on another task.</summary>
public sealed class LineChannelTests
{
    /// <summary>A stream whose writes wait at a gate; <see cref="FailWrite"/> fails the waiting write as a closed stream does.</summary>
    private sealed class GatedStream : MemoryStream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObjectDisposedException Closed { get; } = new(nameof(GatedStream));

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await _gate.Task;
        }

        public void FailWrite() => _gate.TrySetException(Closed);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Closing_during_a_write_fails_the_write_with_the_streams_error_not_a_disposed_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new GatedStream();
        var channel = new LineChannel(stream, 1024);
        var write = channel.WriteLineAsync("{}", ct).AsTask();
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        await channel.DisposeAsync();
        stream.FailWrite(); // the write unwinds only now, after the channel has finished closing

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => write.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Same(stream.Closed, ex);
    }
}
