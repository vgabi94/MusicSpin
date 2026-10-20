using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MusicSpin.App.Services.MediaControls;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The Windows media controls (#34). <see cref="WindowsSmtc"/> (what the SMTC shows, which command a button or the
/// scrubber sends) is tested on every OS; the adapter itself gets a smoke test on Windows against the real System Media
/// Transport Controls, invoking its registered handlers the way Windows does, with fake event args.
/// </summary>
public sealed unsafe class WindowsMediaControlsTests
{
    private static readonly MediaInfo Playing = new(
        "Song", "Artist", "Album", null, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(42), MediaPlayback.Playing, 0.5, "t-1");

    [Fact]
    public void The_controls_show_the_tags_and_the_timeline_in_ticks()
    {
        Assert.Equal(
            new WindowsSmtc.Fields(WindowsSmtc.Status.Playing, "Song", "Artist", "Album", null, TimeSpan.FromMinutes(3).Ticks, TimeSpan.FromSeconds(42).Ticks),
            WindowsSmtc.Describe(Playing));
    }

    [Theory]
    [InlineData(MediaPlayback.Paused, WindowsSmtc.Status.Paused)]
    [InlineData(MediaPlayback.Stopped, WindowsSmtc.Status.Stopped)]
    public void The_status_follows_the_player(MediaPlayback playback, WindowsSmtc.Status status)
        => Assert.Equal(status, WindowsSmtc.Describe(Playing with { Playback = playback }).Status);

    [Fact]
    public void Missing_tags_are_empty_and_the_position_is_clamped_to_the_track()
    {
        var fields = WindowsSmtc.Describe(Playing with { Title = null, Artist = null, Album = null, Position = TimeSpan.FromMinutes(9) });
        Assert.Equal(("", "", ""), (fields.Title, fields.Artist, fields.Album));
        Assert.Equal(fields.EndTicks, fields.PositionTicks);

        var unknown = WindowsSmtc.Describe(Playing with { Duration = TimeSpan.Zero, Position = TimeSpan.FromSeconds(-1) });
        Assert.Equal((0L, 0L), (unknown.EndTicks, unknown.PositionTicks));
    }

    [Fact]
    public void Only_a_cover_file_that_exists_becomes_the_thumbnail()
    {
        var cover = Path.Combine(TempDir.Create("win-cover"), "cover.jpg");
        Assert.Null(WindowsSmtc.Describe(Playing with { CoverFile = cover }).CoverFile);
        File.WriteAllBytes(cover, [1]);
        Assert.Equal(cover, WindowsSmtc.Describe(Playing with { CoverFile = cover }).CoverFile);
    }

    [Theory]
    [InlineData(WindowsSmtc.Button.Play, MediaCommandKind.Play)]
    [InlineData(WindowsSmtc.Button.Pause, MediaCommandKind.Pause)]
    [InlineData(WindowsSmtc.Button.Stop, MediaCommandKind.Stop)]
    [InlineData(WindowsSmtc.Button.Next, MediaCommandKind.Next)]
    [InlineData(WindowsSmtc.Button.Previous, MediaCommandKind.Previous)]
    public void Each_offered_button_sends_its_command(WindowsSmtc.Button button, MediaCommandKind kind)
        => Assert.Equal(new MediaCommand(kind), WindowsSmtc.CommandFor((int)button));

    [Theory]
    [InlineData(WindowsSmtc.Button.Record)]
    [InlineData(WindowsSmtc.Button.FastForward)]
    [InlineData(WindowsSmtc.Button.Rewind)]
    [InlineData(WindowsSmtc.Button.ChannelUp)]
    [InlineData(WindowsSmtc.Button.ChannelDown)]
    [InlineData((WindowsSmtc.Button)42)]
    public void Buttons_MusicSpin_does_not_offer_send_nothing(WindowsSmtc.Button button)
        => Assert.Null(WindowsSmtc.CommandFor((int)button));

    [Fact]
    public void The_scrubber_moves_within_the_current_track_only()
    {
        var end = TimeSpan.FromMinutes(3).Ticks;
        Assert.Equal(new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromMinutes(1), TrackId: "t-1"), WindowsSmtc.PositionRequest(TimeSpan.FromMinutes(1).Ticks, "t-1", end));
        Assert.Null(WindowsSmtc.PositionRequest(TimeSpan.FromMinutes(1).Ticks, null, end));
        Assert.Null(WindowsSmtc.PositionRequest(-1, "t-1", end));
        Assert.Null(WindowsSmtc.PositionRequest(end + 1, "t-1", end));
        Assert.NotNull(WindowsSmtc.PositionRequest(TimeSpan.FromMinutes(10).Ticks, "t-1", 0)); // unknown length: no upper bound
    }

    [Fact]
    public void Smoke_on_Windows_the_real_SMTC_shows_the_track_and_the_handlers_send_commands()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Smoke();
    }

    [SupportedOSPlatform("windows")]
    private static void Smoke()
    {
        WindowsMediaControlsAdapter adapter;
        try
        {
            adapter = WindowsMediaControlsAdapter.Create();
        }
        catch (COMException ex)
        {
            Assert.Skip($"This Windows has no System Media Transport Controls (0x{ex.HResult:x8}).");
            return;
        }

        var cover = Path.Combine(TempDir.Create("win-smoke"), "cover.png");
        File.WriteAllBytes(cover, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var received = new List<MediaCommand>();
        using (adapter)
        {
            adapter.CommandReceived += (_, command) => received.Add(command);

            adapter.Update(Playing with { CoverFile = cover });
            Assert.Equal((WindowsSmtc.Status.Playing, true, "Song", "Album"), adapter.ReadBack());
            adapter.Update(Playing with { Title = "Other", Album = null, Playback = MediaPlayback.Paused });
            Assert.Equal((WindowsSmtc.Status.Paused, true, "Other", ""), adapter.ReadBack());

            var (button, position) = adapter.Handlers;
            Assert.Equal(0, QueryInterface(button, WindowsMediaControlsAdapter.ButtonPressedHandler));
            Assert.Equal(0, QueryInterface(button, new Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"))); // IAgileObject
            Assert.Equal(unchecked((int)0x80004002), QueryInterface(button, Guid.NewGuid()));

            Invoke(button, FakeArgs.Button((int)WindowsSmtc.Button.Next));
            Invoke(button, FakeArgs.Button((int)WindowsSmtc.Button.Record)); // not offered: nothing
            Invoke(button, FakeArgs.Button((int)WindowsSmtc.Button.Pause));
            Invoke(position, FakeArgs.Position(TimeSpan.FromMinutes(1).Ticks));
            Assert.Equal(
                [new MediaCommand(MediaCommandKind.Next), new MediaCommand(MediaCommandKind.Pause), new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromMinutes(1), TrackId: "t-1")],
                received);

            adapter.Clear();
            Assert.Equal((WindowsSmtc.Status.Closed, false, "", ""), adapter.ReadBack());
            Invoke(position, FakeArgs.Position(0)); // nothing playing: ignored
            Assert.Equal(3, received.Count);
        }

        adapter.Dispose(); // twice is fine
        adapter.Update(Playing); // and a disposed adapter ignores updates
    }

    private static int QueryInterface(IntPtr instance, Guid iid)
    {
        var hr = Marshal.QueryInterface(instance, in iid, out var result);
        if (result != IntPtr.Zero)
        {
            Marshal.Release(result);
        }

        return hr;
    }

    /// <summary>Calls <c>Invoke(sender, args)</c> on a TypedEventHandler, as Windows does when a button is pressed.</summary>
    private static void Invoke(IntPtr handler, IntPtr args)
    {
        var invoke = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)(*(IntPtr**)handler)[3];
        Assert.Equal(0, invoke(handler, IntPtr.Zero, args));
    }

    /// <summary>
    /// Minimal stand-ins for SystemMediaTransportControlsButtonPressedEventArgs and
    /// PlaybackPositionChangeRequestedEventArgs: IUnknown answering anything, three IInspectable slots and the one getter.
    /// They live for the test run (a few bytes each).
    /// </summary>
    private static class FakeArgs
    {
        public static IntPtr Button(int button) => Create(&GetButton, button);

        public static IntPtr Position(long ticks) => Create(&GetPosition, ticks);

        private static IntPtr Create(delegate* unmanaged[Stdcall]<IntPtr*, long*, int> getter, long value)
        {
            var vtable = (IntPtr*)NativeMemory.Alloc((nuint)(7 * sizeof(IntPtr)));
            vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
            vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefOrRelease;
            vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefOrRelease;
            vtable[3] = vtable[4] = vtable[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&NotImplemented;
            vtable[6] = (IntPtr)getter;
            var instance = (IntPtr*)NativeMemory.Alloc((nuint)(2 * sizeof(IntPtr)));
            instance[0] = (IntPtr)vtable;
            instance[1] = checked((IntPtr)value);
            return (IntPtr)instance;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
        {
            *result = self;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRefOrRelease(IntPtr self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int NotImplemented(IntPtr self) => unchecked((int)0x80004001);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetButton(IntPtr* self, long* result)
        {
            *(int*)result = (int)self[1];
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetPosition(IntPtr* self, long* result)
        {
            *result = self[1];
            return 0;
        }
    }
}
