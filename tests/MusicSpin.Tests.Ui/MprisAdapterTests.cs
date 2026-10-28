using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services.MediaControls;
using MusicSpin.Tests.Shared;
using Tmds.DBus.Protocol;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The Linux MPRIS server (#36) on a private <c>dbus-daemon</c> (started per test class, so nothing touches the
/// desktop's bus), driven by a real D-Bus client the way <c>playerctl</c> and the desktop shells drive it. Linux only;
/// on CI a missing <c>dbus-daemon</c> is a failure, not a skip.
/// </summary>
public sealed class MprisAdapterTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Channel<MediaCommand> _commands = Channel.CreateUnbounded<MediaCommand>();
    private readonly Channel<BusSignal> _signals = Channel.CreateUnbounded<BusSignal>();
    private Process? _daemon;
    private string _address = "";
    private DBusConnection? _client;
    private MprisAdapter? _adapter;

    private MprisAdapter Adapter => _adapter!;

    private DBusConnection Client => _client!;

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!File.Exists("/usr/bin/dbus-daemon") && Environment.GetEnvironmentVariable("CI") is null)
        {
            return; // skipped in each test below
        }

        _daemon = Process.Start(new ProcessStartInfo("dbus-daemon", "--session --nofork --print-address")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("dbus-daemon didn't start");
        using var cts = new CancellationTokenSource(Wait);
        _address = (await _daemon.StandardOutput.ReadLineAsync(cts.Token))?.Trim() ?? throw new InvalidOperationException("dbus-daemon printed no address");

        _client = new DBusConnection(_address);
        await _client.ConnectAsync();
        await _client.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = MprisAdapter.ObjectPath },
            static (message, _) => BusSignal.Read(message),
            notification =>
            {
                if (notification.HasValue)
                {
                    _signals.Writer.TryWrite(notification.Value);
                }
            },
            false,
            ObserverFlags.None,
            null);

        _adapter = await MprisAdapter.StartAsync(_address, _time);
        _adapter.CommandReceived += (_, command) => _commands.Writer.TryWrite(command);
    }

    public ValueTask DisposeAsync()
    {
        _adapter?.Dispose();
        _client?.Dispose();
        if (_daemon is { HasExited: false })
        {
            _daemon.Kill();
        }

        _daemon?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SkipUnlessBus()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "MPRIS is Linux only");
        Assert.SkipWhen(_adapter is null, "dbus-daemon isn't installed");
    }

    [Fact]
    public async Task Starts_on_the_bus_with_the_well_known_name_and_nothing_playing()
    {
        SkipUnlessBus();

        Assert.Equal(MprisAdapter.BusName, Adapter.OwnedName);
        Assert.True(await NameHasOwnerAsync(MprisAdapter.BusName));
        var root = await GetAllAsync(MprisAdapter.RootInterface);
        Assert.Equal("MusicSpin", root["Identity"].GetString());
        Assert.False(root["CanQuit"].GetBool());
        Assert.False(root["CanRaise"].GetBool());
        Assert.False(root["HasTrackList"].GetBool());

        var player = await GetAllAsync(MprisAdapter.PlayerInterface);
        Assert.Equal("Stopped", player["PlaybackStatus"].GetString());
        Assert.Equal(MprisAdapter.NoTrack, player["Metadata"].GetDictionary<string, VariantValue>()["mpris:trackid"].GetObjectPathAsString());
        Assert.False(player["CanPlay"].GetBool());
        Assert.False(player["CanGoNext"].GetBool());
        Assert.True(player["CanControl"].GetBool());
        Assert.Equal(0L, player["Position"].GetInt64());
    }

    [Fact]
    public async Task A_track_is_announced_with_its_metadata_and_cover_as_a_file_url()
    {
        SkipUnlessBus();
        var cover = Path.Combine(TempDir.Create("mpris"), "cover 1.jpg");

        Adapter.Update(Info() with { CoverFile = cover });

        var signal = await NextSignalAsync("PropertiesChanged");
        Assert.Equal(MprisAdapter.PlayerInterface, signal.Interface);
        Assert.Equal("Playing", signal.Changed!["PlaybackStatus"].GetString());
        Assert.True(signal.Changed["CanGoNext"].GetBool());
        var metadata = signal.Changed["Metadata"].GetDictionary<string, VariantValue>();
        Assert.Equal(MprisAdapter.TrackPath("t-1"), metadata["mpris:trackid"].GetObjectPathAsString());
        Assert.Equal("Song", metadata["xesam:title"].GetString());
        Assert.Equal(["Artist"], metadata["xesam:artist"].GetArray<string>());
        Assert.Equal("Album", metadata["xesam:album"].GetString());
        Assert.Equal(180_000_000L, metadata["mpris:length"].GetInt64());
        Assert.Equal(new Uri(cover).AbsoluteUri, metadata["mpris:artUrl"].GetString());
        Assert.StartsWith("file:///", metadata["mpris:artUrl"].GetString(), StringComparison.Ordinal);

        Assert.Equal("Song", (await GetAsync("Metadata")).GetDictionary<string, VariantValue>()["xesam:title"].GetString());
        Assert.Equal(0.5, (await GetAsync("Volume")).GetDouble());
    }

    [Fact]
    public async Task Missing_tags_are_left_out_of_the_metadata()
    {
        SkipUnlessBus();

        Adapter.Update(Info() with { Title = null, Artist = null, Album = null, Duration = TimeSpan.Zero });

        var metadata = (await NextSignalAsync("PropertiesChanged")).Changed!["Metadata"].GetDictionary<string, VariantValue>();
        Assert.Equal(["mpris:trackid"], metadata.Keys);
    }

    [Fact]
    public async Task Only_what_changed_is_signalled()
    {
        SkipUnlessBus();
        Adapter.Update(Info());
        await NextSignalAsync("PropertiesChanged");

        Adapter.Update(Info() with { Playback = MediaPlayback.Paused, Position = TimeSpan.FromSeconds(30) });

        var signal = await NextSignalAsync("PropertiesChanged");
        Assert.Equal(["PlaybackStatus"], signal.Changed!.Keys);
        Assert.Equal("Paused", signal.Changed["PlaybackStatus"].GetString());

        Adapter.Update(Info() with { Playback = MediaPlayback.Paused, Volume = 0.8 });
        Assert.Equal(["Volume"], (await NextSignalAsync("PropertiesChanged")).Changed!.Keys);
    }

    [Fact]
    public async Task Position_runs_on_with_the_clock_only_while_playing_and_stops_at_the_end()
    {
        SkipUnlessBus();
        Adapter.Update(Info() with { Position = TimeSpan.FromSeconds(10) });

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(15_000_000L, (await GetAsync("Position")).GetInt64());

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(180_000_000L, (await GetAsync("Position")).GetInt64());

        Adapter.Update(Info() with { Playback = MediaPlayback.Paused, Position = TimeSpan.FromSeconds(42) });
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(42_000_000L, (await GetAsync("Position")).GetInt64());
    }

    [Fact]
    public async Task A_jump_sends_Seeked_with_the_new_position()
    {
        SkipUnlessBus();
        Adapter.Update(Info());
        await NextSignalAsync("PropertiesChanged");

        Adapter.Update(Info() with { Position = TimeSpan.FromSeconds(95), Seeked = true });

        Assert.Equal(95_000_000L, (await NextSignalAsync("Seeked")).Position);
    }

    [Theory]
    [InlineData("Next", MediaCommandKind.Next)]
    [InlineData("Previous", MediaCommandKind.Previous)]
    [InlineData("Play", MediaCommandKind.Play)]
    [InlineData("Pause", MediaCommandKind.Pause)]
    [InlineData("PlayPause", MediaCommandKind.PlayPause)]
    [InlineData("Stop", MediaCommandKind.Stop)]
    public async Task Each_transport_method_becomes_its_command(string method, MediaCommandKind kind)
    {
        SkipUnlessBus();

        await CallAsync(MprisAdapter.PlayerInterface, method);

        Assert.Equal(new MediaCommand(kind), await NextCommandAsync());
    }

    [Fact]
    public async Task Seek_carries_the_offset_both_ways()
    {
        SkipUnlessBus();

        await CallAsync(MprisAdapter.PlayerInterface, "Seek", "x", (ref MessageWriter w) => w.WriteInt64(-5_000_000));
        await CallAsync(MprisAdapter.PlayerInterface, "Seek", "x", (ref MessageWriter w) => w.WriteInt64(10_000_000));

        Assert.Equal(new MediaCommand(MediaCommandKind.SeekBy, TimeSpan.FromSeconds(-5)), await NextCommandAsync());
        Assert.Equal(new MediaCommand(MediaCommandKind.SeekBy, TimeSpan.FromSeconds(10)), await NextCommandAsync());
    }

    [Fact]
    public async Task SetPosition_applies_to_the_current_track_and_ignores_stale_or_out_of_range_requests()
    {
        SkipUnlessBus();
        Adapter.Update(Info());
        await CallAsync(MprisAdapter.PlayerInterface, "SetPosition", "ox", (ref MessageWriter w) => { w.WriteObjectPath(MprisAdapter.TrackPath("other")); w.WriteInt64(1_000_000); });
        await CallAsync(MprisAdapter.PlayerInterface, "SetPosition", "ox", (ref MessageWriter w) => { w.WriteObjectPath(MprisAdapter.TrackPath("t-1")); w.WriteInt64(-1); });
        await CallAsync(MprisAdapter.PlayerInterface, "SetPosition", "ox", (ref MessageWriter w) => { w.WriteObjectPath(MprisAdapter.TrackPath("t-1")); w.WriteInt64(181_000_000); });
        await CallAsync(MprisAdapter.PlayerInterface, "SetPosition", "ox", (ref MessageWriter w) => { w.WriteObjectPath(MprisAdapter.TrackPath("t-1")); w.WriteInt64(60_000_000); });

        // The ignored requests sent nothing: the first command out is the valid one.
        Assert.Equal(new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromMinutes(1), TrackId: "t-1"), await NextCommandAsync());
    }

    [Fact]
    public async Task SetPosition_with_nothing_playing_is_ignored()
    {
        SkipUnlessBus();

        await CallAsync(MprisAdapter.PlayerInterface, "SetPosition", "ox", (ref MessageWriter w) => { w.WriteObjectPath(MprisAdapter.NoTrack); w.WriteInt64(0); });
        await CallAsync(MprisAdapter.PlayerInterface, "Next");

        Assert.Equal(MediaCommandKind.Next, (await NextCommandAsync()).Kind);
    }

    [Fact]
    public async Task Volume_can_be_set_and_other_properties_are_read_only()
    {
        SkipUnlessBus();

        await SetAsync("Volume", 0.25);
        var error = await Assert.ThrowsAsync<DBusErrorReplyException>(() => SetAsync("PlaybackStatus", "Playing"));
        Assert.Equal("org.freedesktop.DBus.Error.PropertyReadOnly", error.ErrorName);
        error = await Assert.ThrowsAsync<DBusErrorReplyException>(() => SetAsync("Volume", "loud"));
        Assert.Equal("org.freedesktop.DBus.Error.PropertyReadOnly", error.ErrorName);

        Assert.Equal(new MediaCommand(MediaCommandKind.SetVolume, Value: 0.25), await NextCommandAsync());
    }

    [Fact]
    public async Task Unknown_properties_methods_and_uris_are_refused_with_dbus_errors()
    {
        SkipUnlessBus();

        var error = await Assert.ThrowsAsync<DBusErrorReplyException>(() => GetAsync("Shuffle"));
        Assert.Equal("org.freedesktop.DBus.Error.UnknownProperty", error.ErrorName);
        error = await Assert.ThrowsAsync<DBusErrorReplyException>(() => CallAsync(MprisAdapter.PlayerInterface, "OpenUri", "s", (ref MessageWriter w) => w.WriteString("file:///x.mp3")));
        Assert.Equal("org.freedesktop.DBus.Error.NotSupported", error.ErrorName);
        error = await Assert.ThrowsAsync<DBusErrorReplyException>(() => CallAsync(MprisAdapter.PlayerInterface, "Explode"));
        Assert.Equal("org.freedesktop.DBus.Error.UnknownMethod", error.ErrorName);
        Assert.Empty(await GetAllAsync("org.example.Nothing"));

        // Raise and Quit answer but do nothing (CanRaise and CanQuit are false).
        await CallAsync(MprisAdapter.RootInterface, "Raise");
        await CallAsync(MprisAdapter.RootInterface, "Quit");
        await CallAsync(MprisAdapter.PlayerInterface, "Next");
        Assert.Equal(MediaCommandKind.Next, (await NextCommandAsync()).Kind);
    }

    [Fact]
    public async Task Introspection_describes_both_interfaces()
    {
        SkipUnlessBus();

        var xml = await CallAsync("org.freedesktop.DBus.Introspectable", "Introspect", read: static (m, _) => m.GetBodyReader().ReadString());

        Assert.Contains("org.mpris.MediaPlayer2.Player", xml, StringComparison.Ordinal);
        Assert.Contains("<method name=\"SetPosition\">", xml, StringComparison.Ordinal);
        Assert.Contains("org.freedesktop.DBus.Properties", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_announces_stopped_with_no_track_once()
    {
        SkipUnlessBus();
        Adapter.Update(Info());
        await NextSignalAsync("PropertiesChanged");

        Adapter.Clear();
        Adapter.Clear();
        Adapter.Update(Info() with { Title = "Again" });

        var cleared = await NextSignalAsync("PropertiesChanged");
        Assert.Equal("Stopped", cleared.Changed!["PlaybackStatus"].GetString());
        Assert.False(cleared.Changed["CanPlay"].GetBool());
        Assert.Equal(MprisAdapter.NoTrack, cleared.Changed["Metadata"].GetDictionary<string, VariantValue>()["mpris:trackid"].GetObjectPathAsString());
        // The second Clear sent nothing: the next signal is the new track.
        Assert.Equal("Again", (await NextSignalAsync("PropertiesChanged")).Changed!["Metadata"].GetDictionary<string, VariantValue>()["xesam:title"].GetString());
    }

    [Fact]
    public async Task A_second_instance_takes_an_instance_name_and_disposing_leaves_the_bus()
    {
        SkipUnlessBus();

        var second = await MprisAdapter.StartAsync(_address, _time);
        var name = second.OwnedName!;
        Assert.Equal($"{MprisAdapter.BusName}.instance{Environment.ProcessId}", name);
        Assert.True(await NameHasOwnerAsync(name));

        second.Dispose();
        second.Dispose();
        second.Update(Info()); // a disposed adapter ignores updates
        second.Clear();

        // Name release is asynchronous on the bus: wait for the name to drop (timeout is a failure cap only).
        var released = Stopwatch.StartNew();
        while (await NameHasOwnerAsync(name))
        {
            if (released.Elapsed > Wait)
            {
                Assert.Fail($"Timed out after {Wait.TotalMilliseconds:0} ms waiting for: second instance name released");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(await NameHasOwnerAsync(MprisAdapter.BusName));
    }

    [Fact]
    public async Task No_bus_faults_Ready_and_the_adapter_stays_harmless()
    {
        SkipUnlessBus();
        var missing = "unix:path=" + Path.Combine(TempDir.Create("mpris-nobus"), "no-bus");

        using var adapter = MprisAdapter.Start(missing, _time);
        adapter.Update(Info());
        adapter.Clear();

        await Assert.ThrowsAnyAsync<Exception>(() => adapter.Ready);
        Assert.Null(adapter.OwnedName);
        await Assert.ThrowsAnyAsync<Exception>(() => MprisAdapter.StartAsync(missing, _time));
    }

    [Fact]
    public void TrackPath_is_a_valid_object_path_for_any_id()
    {
        Assert.Equal(MprisAdapter.NoTrack, MprisAdapter.TrackPath(null));
        Assert.Equal("/org/musicspin/track/3f2a_b9", MprisAdapter.TrackPath("3f2a-b9"));
        Assert.Matches("^/org/musicspin/track/[A-Za-z0-9_]+$", MprisAdapter.TrackPath("é/x y.z"));
    }

    private static MediaInfo Info() => new(
        "Song", "Artist", "Album", null, TimeSpan.FromMinutes(3), TimeSpan.Zero, MediaPlayback.Playing, 0.5, "t-1", false);

    private async Task<MediaCommand> NextCommandAsync()
    {
        using var cts = new CancellationTokenSource(Wait);
        return await _commands.Reader.ReadAsync(cts.Token);
    }

    private async Task<BusSignal> NextSignalAsync(string member)
    {
        using var cts = new CancellationTokenSource(Wait);
        var signal = await _signals.Reader.ReadAsync(cts.Token);
        Assert.Equal(member, signal.Member);
        return signal;
    }

    private Task<bool> NameHasOwnerAsync(string name)
        => CallAsync("org.freedesktop.DBus", "NameHasOwner", "s", (ref MessageWriter w) => w.WriteString(name), static (m, _) => m.GetBodyReader().ReadBool(), "org.freedesktop.DBus", "/org/freedesktop/DBus");

    private Task<VariantValue> GetAsync(string property)
        => CallAsync("org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter w) => { w.WriteString(MprisAdapter.PlayerInterface); w.WriteString(property); }, static (m, _) => m.GetBodyReader().ReadVariantValue());

    private Task<Dictionary<string, VariantValue>> GetAllAsync(string iface)
        => CallAsync("org.freedesktop.DBus.Properties", "GetAll", "s", (ref MessageWriter w) => w.WriteString(iface), static (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue());

    private Task<bool> SetAsync(string property, VariantValue value)
        => CallAsync("org.freedesktop.DBus.Properties", "Set", "ssv", (ref MessageWriter w) => { w.WriteString(MprisAdapter.PlayerInterface); w.WriteString(property); w.WriteVariant(value); }, static (_, _) => true);

    private Task<bool> CallAsync(string iface, string member, string? signature = null, BodyWriter? body = null)
        => CallAsync(iface, member, signature, body, static (_, _) => true);

    private async Task<T> CallAsync<T>(string iface, string member, string? signature = null, BodyWriter? body = null, MessageValueReader<T>? read = null, string destination = MprisAdapter.BusName, string path = MprisAdapter.ObjectPath)
    {
        MessageBuffer message;
        var writer = Client.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
            body?.Invoke(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        using var cts = new CancellationTokenSource(Wait);
        return await Client.CallMethodAsync(message, read!, null).WaitAsync(cts.Token);
    }

    private delegate void BodyWriter(ref MessageWriter writer);

    private sealed record BusSignal(string Member, string? Interface, Dictionary<string, VariantValue>? Changed, long Position)
    {
        public static BusSignal Read(Message message)
        {
            var reader = message.GetBodyReader();
            return message.MemberAsString switch
            {
                "PropertiesChanged" => new BusSignal("PropertiesChanged", reader.ReadString(), reader.ReadDictionaryOfStringToVariantValue(), 0),
                "Seeked" => new BusSignal("Seeked", null, null, reader.ReadInt64()),
                var other => new BusSignal(other ?? "", null, null, 0),
            };
        }
    }
}
