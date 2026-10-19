using System.Runtime.Versioning;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The Windows media controls (#34): the System Media Transport Controls (the volume flyout, the lock screen, the media
/// keys, Bluetooth headsets). The SMTC comes from a <c>Windows.Media.Playback.MediaPlayer</c> that never plays (its
/// own command handling off), the documented way for a desktop app to get one without handing over a window. Values
/// come from <see cref="WindowsSmtc"/>; this class only writes them through raw WinRT (<see cref="WinRT"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMediaControlsAdapter : IMediaControlsAdapter
{
    private static readonly Guid IMediaPlayer2 = new("3c841218-2123-4fc5-9082-2f883f77bdf5");
    private static readonly Guid IMediaPlayer3 = new("ee0660da-031b-4feb-bd9b-92e0a0a8d299");
    private static readonly Guid IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid ISmtc2 = new("ea98d2f6-7f3c-4af2-a586-72889808efb1");
    private static readonly Guid IMusicDisplayProperties2 = new("00368462-97d3-44b9-b00f-008afcefaf18");
    private static readonly Guid ITimelineProperties = new("5125316a-c3a2-475b-8507-93534dc88f15");
    private static readonly Guid IButtonPressedEventArgs = new("b7f47116-a56f-4dc8-9e11-92031f4a87c2");
    private static readonly Guid IPositionChangeRequestedEventArgs = new("b4493f88-eb28-4961-9c14-335e44f3e125");
    private static readonly Guid IRandomAccessStreamReferenceStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");
    private static readonly Guid IRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");

    /// <summary>TypedEventHandler&lt;SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs&gt;.</summary>
    internal static readonly Guid ButtonPressedHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");

    /// <summary>TypedEventHandler&lt;SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs&gt;.</summary>
    internal static readonly Guid PositionRequestedHandler = new("44e34f15-bdc0-50a7-ace4-39e91fb753f1");

    private readonly Lock _gate = new();
    private IntPtr _player;
    private IntPtr _smtc;
    private IntPtr _smtc2;
    private IntPtr _display;
    private IntPtr _music;
    private IntPtr _music2;
    private IntPtr _timeline;
    private IntPtr _buttonHandler;
    private IntPtr _positionHandler;
    private long _buttonToken;
    private long _positionToken;
    private string? _trackId;
    private long _endTicks;
    private string? _coverFile = "";
    private bool _enabled;
    private bool _disposed;

    private WindowsMediaControlsAdapter()
    {
        _player = WinRT.Activate("Windows.Media.Playback.MediaPlayer");
        var player3 = WinRT.As(_player, IMediaPlayer3);
        var commands = WinRT.GetObject(player3, 17); // CommandManager
        WinRT.SetBool(commands, 7, false); // IsEnabled: we handle the buttons, the MediaPlayer doesn't
        WinRT.Release(ref commands);
        WinRT.Release(ref player3);

        var player2 = WinRT.As(_player, IMediaPlayer2);
        _smtc = WinRT.GetObject(player2, 6);
        WinRT.Release(ref player2);
        _smtc2 = WinRT.As(_smtc, ISmtc2);
        _display = WinRT.GetObject(_smtc, 8);
        WinRT.SetInt32(_display, 7, 1); // Type = Music
        _music = WinRT.GetObject(_display, 12);
        _music2 = WinRT.As(_music, IMusicDisplayProperties2);
        _timeline = WinRT.Activate("Windows.Media.SystemMediaTransportControlsTimelineProperties");
        var timeline = WinRT.As(_timeline, ITimelineProperties);
        WinRT.Release(ref _timeline);
        _timeline = timeline;

        _buttonHandler = WinRTHandler.Create(ButtonPressedHandler, OnButton);
        _positionHandler = WinRTHandler.Create(PositionRequestedHandler, OnPositionRequested);
        _buttonToken = WinRT.AddHandler(_smtc, 32, _buttonHandler);
        _positionToken = WinRT.AddHandler(_smtc2, 13, _positionHandler);
    }

    public string Name => "Windows SMTC";

    public event EventHandler<MediaCommand>? CommandReceived;

    /// <summary>The handler objects registered with the SMTC (for the smoke test, which invokes them as Windows would).</summary>
    internal (IntPtr Button, IntPtr Position) Handlers => (_buttonHandler, _positionHandler);

    /// <summary>Gets the SMTC. Throws (a COMException) where Windows has none, e.g. a Server edition without Media Foundation.</summary>
    public static WindowsMediaControlsAdapter Create() => new();

    public void Update(MediaInfo info)
    {
        var fields = WindowsSmtc.Describe(info);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _trackId = info.TrackId;
            _endTicks = fields.EndTicks;
            if (!_enabled)
            {
                WinRT.SetBool(_smtc, 11, true); // IsEnabled
                foreach (var slot in new[] { 13, 15, 17, 25, 27 }) // IsPlay/Stop/Pause/Previous/NextEnabled
                {
                    WinRT.SetBool(_smtc, slot, true);
                }

                _enabled = true;
            }

            WinRT.SetInt32(_smtc, 7, (int)fields.Status);
            WinRT.SetInt32(_display, 7, 1);
            WinRT.SetString(_music, 7, fields.Title);
            WinRT.SetString(_music, 11, fields.Artist);
            WinRT.SetString(_music2, 7, fields.Album);
            if (fields.CoverFile != _coverFile)
            {
                SetThumbnail(fields.CoverFile);
            }

            WinRT.Call(_display, 17); // Update

            WinRT.SetInt64(_timeline, 7, 0); // StartTime
            WinRT.SetInt64(_timeline, 9, fields.EndTicks); // EndTime
            WinRT.SetInt64(_timeline, 11, 0); // MinSeekTime
            WinRT.SetInt64(_timeline, 13, fields.EndTicks); // MaxSeekTime
            WinRT.SetInt64(_timeline, 15, fields.PositionTicks); // Position
            WinRT.SetObject(_smtc2, 12, _timeline); // UpdateTimelineProperties
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                ClearCore();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                WinRT.RemoveHandler(_smtc, 33, _buttonToken);
                WinRT.RemoveHandler(_smtc2, 14, _positionToken);
                ClearCore();
                var closable = WinRT.As(_player, IClosable);
                WinRT.Call(closable, 6);
                WinRT.Release(ref closable);
            }
            finally
            {
                WinRT.Release(ref _buttonHandler);
                WinRT.Release(ref _positionHandler);
                WinRT.Release(ref _timeline);
                WinRT.Release(ref _music2);
                WinRT.Release(ref _music);
                WinRT.Release(ref _display);
                WinRT.Release(ref _smtc2);
                WinRT.Release(ref _smtc);
                WinRT.Release(ref _player);
            }
        }
    }

    /// <summary>What the SMTC shows now (for the smoke test): status, enabled, title and album.</summary>
    internal (WindowsSmtc.Status Status, bool Enabled, string Title, string Album) ReadBack()
    {
        lock (_gate)
        {
            return ((WindowsSmtc.Status)WinRT.GetInt32(_smtc, 6), WinRT.GetBool(_smtc, 10), WinRT.GetString(_music, 6), WinRT.GetString(_music2, 6));
        }
    }

    private void ClearCore()
    {
        _trackId = null;
        _endTicks = 0;
        WinRT.SetInt32(_smtc, 7, (int)WindowsSmtc.Status.Closed);
        WinRT.Call(_display, 16); // ClearAll
        WinRT.Call(_display, 17); // Update
        _coverFile = "";
        WinRT.SetBool(_smtc, 11, false); // IsEnabled: gone from the flyout
        _enabled = false;
    }

    private void SetThumbnail(string? coverFile)
    {
        _coverFile = coverFile;
        if (coverFile is null)
        {
            WinRT.SetObject(_display, 11, IntPtr.Zero);
            return;
        }

        IntPtr stream = IntPtr.Zero, statics = IntPtr.Zero, reference = IntPtr.Zero;
        try
        {
            stream = WinRT.FileStream(coverFile, IRandomAccessStream);
            statics = WinRT.Factory("Windows.Storage.Streams.RandomAccessStreamReference", IRandomAccessStreamReferenceStatics);
            reference = WinRT.Create(statics, 8, stream); // CreateFromStream
            WinRT.SetObject(_display, 11, reference); // Thumbnail
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            WinRT.SetObject(_display, 11, IntPtr.Zero); // an unreadable cover: no thumbnail, the rest still shows
        }
        finally
        {
            WinRT.Release(ref reference);
            WinRT.Release(ref statics);
            WinRT.Release(ref stream);
        }
    }

    private void OnButton(IntPtr args)
    {
        var buttonArgs = WinRT.As(args, IButtonPressedEventArgs);
        int button;
        try
        {
            button = WinRT.GetInt32(buttonArgs, 6);
        }
        finally
        {
            WinRT.Release(ref buttonArgs);
        }

        if (WindowsSmtc.CommandFor(button) is { } command)
        {
            CommandReceived?.Invoke(this, command);
        }
    }

    private void OnPositionRequested(IntPtr args)
    {
        var positionArgs = WinRT.As(args, IPositionChangeRequestedEventArgs);
        long ticks;
        try
        {
            ticks = WinRT.GetInt64(positionArgs, 6);
        }
        finally
        {
            WinRT.Release(ref positionArgs);
        }

        string? trackId;
        long end;
        lock (_gate)
        {
            (trackId, end) = (_trackId, _endTicks);
        }

        if (WindowsSmtc.PositionRequest(ticks, trackId, end) is { } command)
        {
            CommandReceived?.Invoke(this, command);
        }
    }
}
