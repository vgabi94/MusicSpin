using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The macOS media controls (#35): Now Playing in Control Center and the menu bar through
/// <c>MPNowPlayingInfoCenter</c>, and the media keys, AirPods and the Touch Bar through <c>MPRemoteCommandCenter</c>.
/// Values come from <see cref="MacNowPlaying"/>; this class only writes them through the Objective-C runtime
/// (<see cref="ObjC"/>). One adapter at a time (the app has one); the handlers are methods of a small Objective-C
/// class registered once, which call the live adapter.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacMediaControlsAdapter : IMediaControlsAdapter
{
    private const string MediaPlayerPath = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string TargetClassName = "MusicSpinMediaCommandTarget";
    private const nint HandlerSuccess = 0;
    private const nint HandlerNoNowPlayingItem = 110;
    private const nint HandlerFailed = 200;

    private static readonly Lock Gate = new();
    private static MacMediaControlsAdapter? s_current;
    private static IntPtr s_artworkBlock;
    private static IntPtr s_artworkImage;

    private readonly IntPtr _mediaPlayer;
    private readonly IntPtr _infoCenter;
    private readonly IntPtr _commandCenter;
    private string? _trackId;
    private string? _coverFile;
    private IntPtr _artwork;
    private bool _disposed;

    private MacMediaControlsAdapter()
    {
        _mediaPlayer = NativeLibrary.Load(MediaPlayerPath);
        NativeLibrary.Load(AppKitPath);
        _infoCenter = ObjC.Send(ObjC.Class("MPNowPlayingInfoCenter"), "defaultCenter");
        _commandCenter = ObjC.Send(ObjC.Class("MPRemoteCommandCenter"), "sharedCommandCenter");
        Target = ObjC.Send(ObjC.Send(TargetClass(), "alloc"), "init");
        foreach (var (command, selector) in MacNowPlaying.Commands)
        {
            var remote = ObjC.Send(_commandCenter, command);
            ObjC.Send(remote, "addTarget:action:", Target, ObjC.Sel(selector));
            ObjC.SendBool(remote, "setEnabled:", true);
        }
    }

    public string Name => "macOS Now Playing";

    public event EventHandler<MediaCommand>? CommandReceived;

    /// <summary>The Objective-C object the remote commands call (for the smoke test).</summary>
    internal IntPtr Target { get; }

    /// <summary>Takes over Now Playing and the remote commands. Throws when another adapter is live.</summary>
    public static MacMediaControlsAdapter Create()
    {
        lock (Gate)
        {
            if (s_current is not null)
            {
                throw new InvalidOperationException("The macOS media controls are already in use.");
            }

            s_current = new MacMediaControlsAdapter();
            return s_current;
        }
    }

    public void Update(MediaInfo info)
    {
        var fields = MacNowPlaying.Describe(info);
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _trackId = info.TrackId;
            var pool = ObjC.objc_autoreleasePoolPush();
            try
            {
                var dictionary = ObjC.Send(ObjC.Class("NSMutableDictionary"), "dictionary");
                Put(dictionary, "MPMediaItemPropertyTitle", fields.Title is { } title ? ObjC.String(title) : IntPtr.Zero);
                Put(dictionary, "MPMediaItemPropertyArtist", fields.Artist is { } artist ? ObjC.String(artist) : IntPtr.Zero);
                Put(dictionary, "MPMediaItemPropertyAlbumTitle", fields.Album is { } album ? ObjC.String(album) : IntPtr.Zero);
                Put(dictionary, "MPMediaItemPropertyPlaybackDuration", fields.Duration is { } duration ? ObjC.Number(duration) : IntPtr.Zero);
                Put(dictionary, "MPNowPlayingInfoPropertyElapsedPlaybackTime", ObjC.Number(fields.Elapsed));
                Put(dictionary, "MPNowPlayingInfoPropertyPlaybackRate", ObjC.Number(fields.Rate));
                Put(dictionary, "MPMediaItemPropertyArtwork", Artwork(fields.CoverFile));
                ObjC.Send(_infoCenter, "setNowPlayingInfo:", dictionary);
                ObjC.SendUInt(_infoCenter, "setPlaybackState:", (nuint)fields.State);
            }
            finally
            {
                ObjC.objc_autoreleasePoolPop(pool);
            }
        }
    }

    public void Clear()
    {
        lock (Gate)
        {
            if (!_disposed)
            {
                ClearCore();
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var (command, _) in MacNowPlaying.Commands)
            {
                var remote = ObjC.Send(_commandCenter, command);
                ObjC.Send(remote, "removeTarget:", Target);
                ObjC.SendBool(remote, "setEnabled:", false);
            }

            ClearCore();
            ObjC.Send(Target, "release");
            if (s_current == this)
            {
                s_current = null;
            }
        }
    }

    /// <summary>What <c>MPNowPlayingInfoCenter</c> shows now (for the smoke test): the title and the playback state.</summary>
    internal (string? Title, MacNowPlaying.State State, bool HasArtwork) ReadBack()
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var info = ObjC.Send(_infoCenter, "nowPlayingInfo");
            var title = info == IntPtr.Zero ? null : ObjC.StringOf(ObjC.Send(info, "objectForKey:", Key("MPMediaItemPropertyTitle")));
            var artwork = info == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(info, "objectForKey:", Key("MPMediaItemPropertyArtwork"));
            var image = artwork == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(artwork, "imageWithSize:", new ObjC.CGSize(64, 64));
            return (title, (MacNowPlaying.State)ObjC.GetUInt(_infoCenter, "playbackState"), image != IntPtr.Zero);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private void ClearCore()
    {
        ObjC.Send(_infoCenter, "setNowPlayingInfo:", IntPtr.Zero);
        ObjC.SendUInt(_infoCenter, "setPlaybackState:", (nuint)MacNowPlaying.State.Stopped);
        _trackId = null;
        SetArtwork(null);
    }

    private IntPtr Key(string name) => ObjC.Constant(_mediaPlayer, name);

    private void Put(IntPtr dictionary, string key, IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            ObjC.Send(dictionary, "setObject:forKey:", value, Key(key));
        }
    }

    /// <summary>The MPMediaItemArtwork for the cover (kept while the cover stays the same), or nil.</summary>
    private IntPtr Artwork(string? coverFile)
    {
        if (coverFile != _coverFile)
        {
            SetArtwork(coverFile);
        }

        return _artwork;
    }

    private void SetArtwork(string? coverFile)
    {
        _coverFile = coverFile;
        if (_artwork != IntPtr.Zero)
        {
            ObjC.Send(_artwork, "release");
            _artwork = IntPtr.Zero;
        }

        if (s_artworkImage != IntPtr.Zero)
        {
            ObjC.Send(s_artworkImage, "release");
            s_artworkImage = IntPtr.Zero;
        }

        if (coverFile is null)
        {
            return;
        }

        var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), "alloc"), "initWithContentsOfFile:", ObjC.String(coverFile));
        if (image == IntPtr.Zero)
        {
            return; // not an image macOS can read: no artwork
        }

        s_artworkImage = image;
        var size = ObjC.GetSize(image, "size");
        _artwork = ObjC.Send(ObjC.Send(ObjC.Class("MPMediaItemArtwork"), "alloc"), "initWithBoundsSize:requestHandler:", size, ArtworkBlock());
    }

    /// <summary>
    /// The artwork request handler: a global block (no captures, so it needs no copy helpers) that hands back the current
    /// cover image. Built by hand as the Blocks ABI lays it out.
    /// </summary>
    private static IntPtr ArtworkBlock()
    {
        if (s_artworkBlock != IntPtr.Zero)
        {
            return s_artworkBlock;
        }

        var system = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var signature = Marshal.StringToHGlobalAnsi("@32@?0{CGSize=dd}8");
        var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        *descriptor = new BlockDescriptor { Reserved = 0, Size = (ulong)sizeof(BlockLiteral), Signature = signature };
        var block = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        *block = new BlockLiteral
        {
            Isa = NativeLibrary.GetExport(system, "_NSConcreteGlobalBlock"),
            Flags = (1 << 28) | (1 << 30), // BLOCK_IS_GLOBAL | BLOCK_HAS_SIGNATURE
            Reserved = 0,
            Invoke = (IntPtr)(delegate* unmanaged<IntPtr, ObjC.CGSize, IntPtr>)&ArtworkImage,
            Descriptor = (IntPtr)descriptor,
        };
        s_artworkBlock = (IntPtr)block;
        return s_artworkBlock;
    }

    [UnmanagedCallersOnly]
    private static IntPtr ArtworkImage(IntPtr block, ObjC.CGSize size)
    {
        lock (Gate)
        {
            return s_artworkImage;
        }
    }

    private static IntPtr TargetClass()
    {
        var existing = ObjC.objc_getClass(TargetClassName);
        if (existing != IntPtr.Zero)
        {
            return existing;
        }

        var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), TargetClassName, 0);
        var handler = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint>)&Handle;
        foreach (var (_, selector) in MacNowPlaying.Commands)
        {
            ObjC.class_addMethod(cls, ObjC.Sel(selector), handler, "q@:@");
        }

        ObjC.objc_registerClassPair(cls);
        return cls;
    }

    /// <summary>Every remote command lands here (on the main thread); returns an MPRemoteCommandHandlerStatus.</summary>
    [UnmanagedCallersOnly]
    private static nint Handle(IntPtr self, IntPtr selector, IntPtr commandEvent)
    {
        try
        {
            MacMediaControlsAdapter? adapter;
            lock (Gate)
            {
                adapter = s_current;
            }

            if (adapter is null)
            {
                return HandlerNoNowPlayingItem;
            }

            var name = Marshal.PtrToStringUTF8(ObjC.sel_getName(selector)) ?? "";
            var position = name == "musicSpinPosition:" && commandEvent != IntPtr.Zero ? ObjC.GetDouble(commandEvent, "positionTime") : 0;
            string? trackId;
            lock (Gate)
            {
                trackId = adapter._trackId;
            }

            if (MacNowPlaying.CommandFor(name, position, trackId) is not { } command)
            {
                return HandlerNoNowPlayingItem;
            }

            adapter.CommandReceived?.Invoke(adapter, command);
            return HandlerSuccess;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HandlerFailed; // never let an exception cross into Objective-C
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public ulong Reserved;
        public ulong Size;
        public IntPtr Signature;
    }
}
