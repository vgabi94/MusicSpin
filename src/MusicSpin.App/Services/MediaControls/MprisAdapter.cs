using System.Text;
using Tmds.DBus.Protocol;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The Linux media controls (#36): an MPRIS server on the session bus (<c>org.mpris.MediaPlayer2.musicspin</c>, or
/// <c>…musicspin.instance&lt;pid&gt;</c> when another MusicSpin has the name) with the <c>org.mpris.MediaPlayer2</c> and
/// <c>.Player</c> interfaces, which GNOME, KDE, <c>playerctl</c> and the media keys use. It only translates: state comes
/// from <see cref="Update"/>, commands go out through <see cref="CommandReceived"/>; <see cref="MediaControlsBridge"/>
/// decides the rest. Spec: https://specifications.freedesktop.org/mpris-spec/latest/
/// </summary>
public sealed class MprisAdapter : IMediaControlsAdapter, IPathMethodHandler
{
    public const string BusName = "org.mpris.MediaPlayer2.musicspin";
    public const string ObjectPath = "/org/mpris/MediaPlayer2";
    public const string RootInterface = "org.mpris.MediaPlayer2";
    public const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    public const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly Lock _gate = new();
    private readonly DBusConnection _connection;
    private readonly bool _ownsConnection;
    private readonly TimeProvider _time;
    private MediaInfo? _info;
    private long _infoAt;
    private int _disposed;

    private MprisAdapter(DBusConnection connection, bool ownsConnection, TimeProvider time)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _time = time;
    }

    public string Name => "MPRIS";

    /// <summary>The bus name this server got (null until <see cref="StartAsync"/> finished).</summary>
    public string? OwnedName { get; private set; }

    public event EventHandler<MediaCommand>? CommandReceived;

    string IPathMethodHandler.Path => ObjectPath;

    bool IPathMethodHandler.HandlesChildPaths => false;

    /// <summary>
    /// Connects to <paramref name="address"/> (default: the session bus), publishes the object and takes the bus name,
    /// in the background: the adapter is usable at once and <see cref="Ready"/> tells how the connection went. Without a
    /// bus (a bare TTY, a container) <see cref="Ready"/> faults and the adapter stays silent; the app is unaffected.
    /// </summary>
    public static MprisAdapter Start(string? address = null, TimeProvider? time = null, string busName = BusName)
    {
        address ??= DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            throw new InvalidOperationException("No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
        }

        var adapter = new MprisAdapter(new DBusConnection(address), ownsConnection: true, time ?? TimeProvider.System);
        adapter.Ready = adapter.ConnectAsync(busName);
        return adapter;
    }

    /// <summary><see cref="Start"/>, waiting until the server is on the bus (or failed, which throws and disposes it).</summary>
    public static async Task<MprisAdapter> StartAsync(string? address = null, TimeProvider? time = null, string busName = BusName)
    {
        var adapter = Start(address, time, busName);
        try
        {
            await adapter.Ready.ConfigureAwait(false);
            return adapter;
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    /// <summary>Completes once the server owns its bus name; faults when it couldn't connect.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    private async Task ConnectAsync(string busName)
    {
        await _connection.ConnectAsync().ConfigureAwait(false);
        _connection.AddMethodHandler(this);
        var instance = $"{busName}.instance{Environment.ProcessId}";
        OwnedName = await _connection.TryRequestNameAsync(busName, RequestNameOptions.None).ConfigureAwait(false) ? busName
            : await _connection.TryRequestNameAsync(instance, RequestNameOptions.None).ConfigureAwait(false) ? instance
            : throw new InvalidOperationException($"The D-Bus names {busName} and {instance} are taken.");
    }

    /// <summary>The MPRIS track id of a library track (an object path; ids are GUIDs, so their hex digits are kept).</summary>
    public static string TrackPath(string? trackId)
        => trackId is null ? NoTrack : "/org/musicspin/track/" + new string([.. trackId.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);

    public void Update(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        MediaInfo? before;
        lock (_gate)
        {
            before = _info;
            _info = info;
            _infoAt = _time.GetTimestamp();
        }

        var changed = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
        if (before?.Playback != info.Playback)
        {
            changed["PlaybackStatus"] = Status(info);
        }

        if (before is null || before.Title != info.Title || before.Artist != info.Artist || before.Album != info.Album
            || before.CoverFile != info.CoverFile || before.Duration != info.Duration || before.TrackId != info.TrackId)
        {
            changed["Metadata"] = Metadata(info);
        }

        if (before is null || Math.Abs(before.Volume - info.Volume) > 1e-9)
        {
            changed["Volume"] = info.Volume;
        }

        if (before is null)
        {
            foreach (var (name, value) in Capabilities(true))
            {
                changed[name] = value;
            }
        }

        if (changed.Count > 0)
        {
            PropertiesChanged(PlayerInterface, changed);
        }

        if (info.Seeked)
        {
            Signal(PlayerInterface, "Seeked", "x", (ref MessageWriter w) => w.WriteInt64(Micros(info.Position)));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_info is null)
            {
                return;
            }

            _info = null;
        }

        var changed = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["PlaybackStatus"] = "Stopped",
            ["Metadata"] = Metadata(null),
        };
        foreach (var (name, value) in Capabilities(false))
        {
            changed[name] = value;
        }

        PropertiesChanged(PlayerInterface, changed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _connection.RemoveMethodHandler(ObjectPath);
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }

        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        var iface = request.InterfaceAsString;
        var member = request.MemberAsString;
        try
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([IntrospectXml], []);
            }
            else if (iface == PropertiesInterface)
            {
                HandleProperties(context, member);
            }
            else if (iface == PlayerInterface || (iface is null && PlayerMethod(member)))
            {
                HandlePlayer(context, member);
            }
            else if (iface == RootInterface && member is "Raise" or "Quit")
            {
                Ok(context); // CanRaise and CanQuit are false: nothing to do
            }
            else
            {
                context.ReplyUnknownMethodError();
            }
        }
        catch (DBusReadException)
        {
            context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Invalid arguments.");
        }

        return ValueTask.CompletedTask;
    }

    private static bool PlayerMethod(string? member) => member is "Next" or "Previous" or "Pause" or "PlayPause" or "Stop" or "Play" or "Seek" or "SetPosition" or "OpenUri";

    private void HandlePlayer(MethodContext context, string? member)
    {
        var reader = context.Request.GetBodyReader();
        MediaCommand? command = member switch
        {
            "Next" => new MediaCommand(MediaCommandKind.Next),
            "Previous" => new MediaCommand(MediaCommandKind.Previous),
            "Pause" => new MediaCommand(MediaCommandKind.Pause),
            "PlayPause" => new MediaCommand(MediaCommandKind.PlayPause),
            "Stop" => new MediaCommand(MediaCommandKind.Stop),
            "Play" => new MediaCommand(MediaCommandKind.Play),
            "Seek" => new MediaCommand(MediaCommandKind.SeekBy, TimeSpan.FromMicroseconds(reader.ReadInt64())),
            "SetPosition" => SetPosition(reader.ReadObjectPathAsString(), reader.ReadInt64()),
            _ => null,
        };
        if (member == "OpenUri")
        {
            context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "MusicSpin doesn't open URIs.");
            return;
        }

        if (member is null || (command is null && member != "SetPosition"))
        {
            context.ReplyUnknownMethodError();
            return;
        }

        Ok(context);
        if (command is not null)
        {
            CommandReceived?.Invoke(this, command);
        }
    }

    /// <summary>MPRIS: a position for another track, or outside the track, is ignored.</summary>
    private MediaCommand? SetPosition(string trackPath, long micros)
    {
        MediaInfo? info;
        lock (_gate)
        {
            info = _info;
        }

        return info is null || trackPath != TrackPath(info.TrackId) || micros < 0 || (info.Duration > TimeSpan.Zero && micros > Micros(info.Duration))
            ? null
            : new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromMicroseconds(micros), TrackId: info.TrackId);
    }

    private void HandleProperties(MethodContext context, string? member)
    {
        var reader = context.Request.GetBodyReader();
        switch (member)
        {
            case "Get":
                {
                    var iface = reader.ReadString();
                    var name = reader.ReadString();
                    if (!All(iface).TryGetValue(name, out var value))
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {iface}.{name}.");
                        return;
                    }

                    using var writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value);
                    context.Reply(writer.CreateMessage());
                    return;
                }

            case "GetAll":
                {
                    var iface = reader.ReadString();
                    using var writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(All(iface));
                    context.Reply(writer.CreateMessage());
                    return;
                }

            case "Set":
                {
                    var iface = reader.ReadString();
                    var name = reader.ReadString();
                    var value = reader.ReadVariantValue();
                    if (iface == PlayerInterface && name == "Volume" && value.Type == VariantValueType.Double)
                    {
                        Ok(context);
                        CommandReceived?.Invoke(this, new MediaCommand(MediaCommandKind.SetVolume, Value: value.GetDouble()));
                    }
                    else
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", $"{iface}.{name} can't be set.");
                    }

                    return;
                }

            default:
                context.ReplyUnknownMethodError();
                return;
        }
    }

    private Dictionary<string, VariantValue> All(string iface)
    {
        MediaInfo? info;
        long at;
        lock (_gate)
        {
            (info, at) = (_info, _infoAt);
        }

        if (iface == RootInterface)
        {
            return new(StringComparer.Ordinal)
            {
                ["CanQuit"] = false,
                ["CanRaise"] = false,
                ["HasTrackList"] = false,
                ["Identity"] = "MusicSpin",
                ["DesktopEntry"] = "musicspin",
                ["SupportedUriSchemes"] = VariantValue.Array(Array.Empty<string>()),
                ["SupportedMimeTypes"] = VariantValue.Array(Array.Empty<string>()),
            };
        }

        if (iface != PlayerInterface)
        {
            return new(StringComparer.Ordinal);
        }

        var properties = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["PlaybackStatus"] = info is null ? "Stopped" : Status(info),
            ["Rate"] = 1.0,
            ["MinimumRate"] = 1.0,
            ["MaximumRate"] = 1.0,
            ["Metadata"] = Metadata(info),
            ["Volume"] = info?.Volume ?? 1.0,
            ["Position"] = info is null ? 0L : Micros(Position(info, at)),
        };
        foreach (var (name, value) in Capabilities(info is not null))
        {
            properties[name] = value;
        }

        return properties;
    }

    /// <summary>Where the track is now: the last position, run on by the clock while playing (clamped to the track).</summary>
    private TimeSpan Position(MediaInfo info, long at)
    {
        var position = info.Playback == MediaPlayback.Playing ? info.Position + _time.GetElapsedTime(at) : info.Position;
        return info.Duration > TimeSpan.Zero && position > info.Duration ? info.Duration : position;
    }

    private static IEnumerable<(string, VariantValue)> Capabilities(bool hasTrack)
    {
        yield return ("CanControl", true);
        yield return ("CanPlay", hasTrack);
        yield return ("CanPause", hasTrack);
        yield return ("CanGoNext", hasTrack);
        yield return ("CanGoPrevious", hasTrack);
        yield return ("CanSeek", hasTrack);
    }

    private static string Status(MediaInfo info) => info.Playback switch
    {
        MediaPlayback.Playing => "Playing",
        MediaPlayback.Paused => "Paused",
        _ => "Stopped",
    };

    private static VariantValue Metadata(MediaInfo? info)
    {
        var metadata = new Dict<string, VariantValue> { { "mpris:trackid", new Tmds.DBus.Protocol.ObjectPath(TrackPath(info?.TrackId)) } };
        if (info is null)
        {
            return metadata.AsVariantValue();
        }

        if (info.Duration > TimeSpan.Zero)
        {
            metadata.Add("mpris:length", Micros(info.Duration));
        }

        if (info.Title is { } title)
        {
            metadata.Add("xesam:title", title);
        }

        if (info.Artist is { } artist)
        {
            metadata.Add("xesam:artist", VariantValue.Array(new[] { artist }));
        }

        if (info.Album is { } album)
        {
            metadata.Add("xesam:album", album);
        }

        if (info.CoverFile is { } cover)
        {
            metadata.Add("mpris:artUrl", new Uri(System.IO.Path.GetFullPath(cover)).AbsoluteUri);
        }

        return metadata.AsVariantValue();
    }

    private static long Micros(TimeSpan time) => (long)Math.Round(time.TotalMicroseconds);

    private static void Ok(MethodContext context)
    {
        if (!context.NoReplyExpected)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
    }

    private void PropertiesChanged(string iface, Dictionary<string, VariantValue> changed)
        => Signal(PropertiesInterface, "PropertiesChanged", "sa{sv}as", (ref MessageWriter w) =>
        {
            w.WriteString(iface);
            w.WriteDictionary(changed);
            w.WriteArray(Array.Empty<string>());
        });

    private void Signal(string iface, string member, string signature, BodyWriter body)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var writer = _connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(null, ObjectPath, iface, member, signature);
                body(ref writer);
                _connection.TrySendMessage(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }
        catch (Exception ex) when (ex is DBusConnectionException or ObjectDisposedException or InvalidOperationException)
        {
            // Not connected (yet, or any more): clients read the state with GetAll once they see us.
        }
    }

    /// <summary>Writes a message body; MessageWriter is a struct, so it must be passed by reference.</summary>
    private delegate void BodyWriter(ref MessageWriter writer);

    private static readonly ReadOnlyMemory<byte> IntrospectXml = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/><method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/><property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/><property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/><property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/><method name="Previous"/><method name="Pause"/><method name="PlayPause"/><method name="Stop"/><method name="Play"/>
          <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
          <method name="SetPosition"><arg name="TrackId" type="o" direction="in"/><arg name="Position" type="x" direction="in"/></method>
          <method name="OpenUri"><arg name="Uri" type="s" direction="in"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/><property name="Rate" type="d" access="read"/>
          <property name="Metadata" type="a{sv}" access="read"/><property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/><property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/><property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/><property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/><property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        <interface name="org.freedesktop.DBus.Properties">
          <method name="Get"><arg name="interface_name" type="s" direction="in"/><arg name="property_name" type="s" direction="in"/><arg name="value" type="v" direction="out"/></method>
          <method name="GetAll"><arg name="interface_name" type="s" direction="in"/><arg name="props" type="a{sv}" direction="out"/></method>
          <method name="Set"><arg name="interface_name" type="s" direction="in"/><arg name="property_name" type="s" direction="in"/><arg name="value" type="v" direction="in"/></method>
          <signal name="PropertiesChanged"><arg name="interface_name" type="s"/><arg name="changed_properties" type="a{sv}"/><arg name="invalidated_properties" type="as"/></signal>
        </interface>
        """);
}
