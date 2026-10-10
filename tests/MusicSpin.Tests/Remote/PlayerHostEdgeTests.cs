using System.IO.Pipes;
using MusicSpin.Core;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

/// <summary>Background-player host failure paths: endpoint squatted, broken log, player.json states, lock probes.</summary>
[Trait("Category", "Unhappy")]
public sealed class PlayerHostEdgeTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_endpoint_that_cannot_be_created_is_player_unavailable_and_releases_the_lock()
    {
        Directory.CreateDirectory(Data.Path);
        await using var squatter = Squat();

        var ex = Assert.Throws<MusicSpinException>(() => PlayerHost.TryStart(Options()));

        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Contains(Endpoint.Display, ex.Message, StringComparison.Ordinal);
        Assert.False(PlayerHost.IsLockHeld(Endpoint)); // a later start must not think a player runs
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
    }

    /// <summary>Occupies the endpoint so listening fails: a non-empty folder at the socket path, or the only pipe instance.</summary>
    private IAsyncDisposable Squat()
    {
        if (OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(Endpoint.Address, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        Directory.CreateDirectory(Path.Combine(Endpoint.Address, "not-empty"));
        return new Cleanup(() => Directory.Delete(Endpoint.Address, recursive: true));
    }

    [Fact]
    public async Task A_log_that_throws_never_takes_the_player_down()
    {
        var host = StartHost(Options() with { Log = new BrokenWriter() });
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        Assert.Equal(3, (await client.StatusAsync(Ct)).QueueLength);
        await client.SendAsync("quit", null, Ct);
        Assert.Equal("quit", await host.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task A_failure_while_shutting_down_is_logged_and_the_lock_is_still_released()
    {
        var host = StartHost(Options(new FailingDisposeOutputs()));
        await host.StopAsync();

        await host.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Contains("shutdown error: audio backend refused to close", Log.ToString(), StringComparison.Ordinal);
        Assert.False(PlayerHost.IsLockHeld(Endpoint)); // the next player can start
        var next = StartHost(Options());
        await next.StopAsync();
    }

    private sealed class FailingDisposeOutputs : MusicSpin.Core.Audio.IAudioOutputProvider, IDisposable
    {
        private readonly MusicSpin.Core.Output.NullAudioOutputProvider _inner = new(realtime: false);

        public string Name => _inner.Name;

        public IReadOnlyList<MusicSpin.Core.Audio.AudioDeviceInfo> GetDevices() => _inner.GetDevices();

        public MusicSpin.Core.Audio.IAudioOutput Create(MusicSpin.Core.Audio.AudioOutputOptions options) => _inner.Create(options);

        public void Dispose() => throw new InvalidOperationException("audio backend refused to close");
    }

    [Fact]
    public void Player_info_is_null_when_missing_garbled_or_a_folder()
    {
        Directory.CreateDirectory(Data.Path);
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
        File.WriteAllText(Endpoint.InfoFile, "{ not json");
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
        File.WriteAllText(Endpoint.InfoFile, "[]");
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
        File.Delete(Endpoint.InfoFile);
        Directory.CreateDirectory(Endpoint.InfoFile);
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
    }

    [Fact]
    public async Task Player_info_describes_the_running_player_and_is_removed_on_shutdown()
    {
        var host = StartHost(Options(idle: TimeSpan.FromMinutes(5)));
        var info = PlayerHost.ReadInfo(Endpoint);
        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessId, info.Pid);
        Assert.Equal(PlayerProtocol.Version, info.Protocol);
        Assert.Equal(300, info.IdleTimeoutSeconds);
        Assert.Equal(Data.Path, info.DataDir);
        await host.StopAsync();
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
        Assert.False(PlayerHost.IsLockHeld(Endpoint));
    }

    [Fact]
    public void Lock_probe_on_a_missing_or_free_lock_file_says_not_held()
    {
        Assert.False(PlayerHost.IsLockHeld(Endpoint));
        Directory.CreateDirectory(Data.Path);
        File.WriteAllText(Endpoint.LockFile, "");
        Assert.False(PlayerHost.IsLockHeld(Endpoint));
    }

    [Fact]
    public void A_second_host_on_the_same_data_folder_returns_null_instead_of_starting()
    {
        StartHost();
        Assert.Null(PlayerHost.TryStart(Options()));
        Assert.True(PlayerHost.IsLockHeld(Endpoint));
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("disk full");

        public override void Flush() => throw new ObjectDisposedException("log");
    }

    private sealed class Cleanup(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
