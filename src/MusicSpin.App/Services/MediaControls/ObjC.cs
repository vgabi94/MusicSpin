using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The few Objective-C runtime calls the macOS media controls need (#35): classes, selectors, <c>objc_msgSend</c> with
/// the exact signature of each call (cast function pointers, so arm64 and x64 both pass arguments right), NSString and
/// autorelease pools. No binding library: MediaPlayer.framework is reached through the runtime directly.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly IntPtr MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(LibObjC), "objc_msgSend");

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGSize(double Width, double Height);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr sel_registerName(string name);

    [LibraryImport(LibObjC)]
    public static partial IntPtr sel_getName(IntPtr selector);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

    [LibraryImport(LibObjC)]
    public static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [LibraryImport(LibObjC)]
    public static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(LibObjC)]
    public static partial void objc_autoreleasePoolPop(IntPtr pool);

    public static IntPtr Class(string name)
    {
        var cls = objc_getClass(name);
        return cls != IntPtr.Zero ? cls : throw new InvalidOperationException($"Objective-C class {name} not found.");
    }

    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Send(IntPtr receiver, string selector)
        => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector));

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr arg)
        => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector), arg);

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr arg1, IntPtr arg2)
        => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector), arg1, arg2);

    public static IntPtr Send(IntPtr receiver, string selector, double arg)
        => ((delegate* unmanaged<IntPtr, IntPtr, double, IntPtr>)MsgSend)(receiver, Sel(selector), arg);

    public static IntPtr Send(IntPtr receiver, string selector, CGSize size, IntPtr arg)
        => ((delegate* unmanaged<IntPtr, IntPtr, CGSize, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector), size, arg);

    public static IntPtr Send(IntPtr receiver, string selector, CGSize size)
        => ((delegate* unmanaged<IntPtr, IntPtr, CGSize, IntPtr>)MsgSend)(receiver, Sel(selector), size);

    public static void SendBool(IntPtr receiver, string selector, bool value)
        => ((delegate* unmanaged<IntPtr, IntPtr, byte, void>)MsgSend)(receiver, Sel(selector), value ? (byte)1 : (byte)0);

    public static void SendUInt(IntPtr receiver, string selector, nuint value)
        => ((delegate* unmanaged<IntPtr, IntPtr, nuint, void>)MsgSend)(receiver, Sel(selector), value);

    public static nuint GetUInt(IntPtr receiver, string selector)
        => ((delegate* unmanaged<IntPtr, IntPtr, nuint>)MsgSend)(receiver, Sel(selector));

    public static double GetDouble(IntPtr receiver, string selector)
        => ((delegate* unmanaged<IntPtr, IntPtr, double>)MsgSend)(receiver, Sel(selector));

    public static CGSize GetSize(IntPtr receiver, string selector)
        => ((delegate* unmanaged<IntPtr, IntPtr, CGSize>)MsgSend)(receiver, Sel(selector));

    public static nint SendForStatus(IntPtr receiver, IntPtr selector, IntPtr arg)
        => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint>)MsgSend)(receiver, selector, arg);

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr String(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes)
        {
            var allocated = Send(Class("NSString"), "alloc");
            var created = ((delegate* unmanaged<IntPtr, IntPtr, byte*, IntPtr>)MsgSend)(allocated, Sel("initWithUTF8String:"), p);
            return Send(created, "autorelease");
        }
    }

    /// <summary>The text of an NSString (null for nil).</summary>
    public static string? StringOf(IntPtr nsString)
        => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    /// <summary>An autoreleased NSNumber.</summary>
    public static IntPtr Number(double value) => Send(Class("NSNumber"), "numberWithDouble:", value);

    /// <summary>The value of an <c>extern NSString *const</c> in <paramref name="library"/>.</summary>
    public static IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
}
