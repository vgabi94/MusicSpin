using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>
/// The raw WinRT calls the Windows media controls need (#34): activation, HSTRINGs and vtable calls by slot (the slot
/// numbers come from the Windows SDK metadata; IInspectable's own six come first). No projection package, so the app
/// keeps its plain <c>net10.0</c> target on every OS.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinRT
{
    public static readonly Guid IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    public static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string? source, int length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [LibraryImport("shcore.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateRandomAccessStreamOnFile(string filePath, uint accessMode, in Guid iid, out IntPtr stream);

    /// <summary>A new instance of a runtime class (its default interface).</summary>
    public static IntPtr Activate(string className)
    {
        var name = HString(className);
        try
        {
            Check(RoActivateInstance(name, out var instance));
            return instance;
        }
        finally
        {
            _ = WindowsDeleteString(name);
        }
    }

    /// <summary>A runtime class's activation factory or statics interface.</summary>
    public static IntPtr Factory(string className, Guid iid)
    {
        var name = HString(className);
        try
        {
            Check(RoGetActivationFactory(name, iid, out var factory));
            return factory;
        }
        finally
        {
            _ = WindowsDeleteString(name);
        }
    }

    /// <summary>A read-only <c>IRandomAccessStream</c> over a file (shcore; synchronous, unlike StorageFile).</summary>
    public static IntPtr FileStream(string path, Guid iid)
    {
        Check(CreateRandomAccessStreamOnFile(path, 0, iid, out var stream));
        return stream;
    }

    public static IntPtr As(IntPtr instance, Guid iid)
    {
        Check(Marshal.QueryInterface(instance, in iid, out var result));
        return result;
    }

    public static void Release(ref IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            Marshal.Release(instance);
            instance = IntPtr.Zero;
        }
    }

    public static IntPtr HString(string? value)
    {
        Check(WindowsCreateString(value, value?.Length ?? 0, out var hstring));
        return hstring;
    }

    public static string StringOf(IntPtr hstring)
    {
        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return new string(buffer, 0, (int)length);
    }

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

    /// <summary>A method with no arguments (<c>Update</c>, <c>ClearAll</c>, <c>Close</c>).</summary>
    public static void Call(IntPtr instance, int slot)
        => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(instance, slot))(instance));

    /// <summary>A getter of an interface pointer.</summary>
    public static IntPtr GetObject(IntPtr instance, int slot)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    public static int GetInt32(IntPtr instance, int slot)
    {
        int result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    public static bool GetBool(IntPtr instance, int slot)
    {
        byte result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(instance, slot))(instance, &result));
        return result != 0;
    }

    public static long GetInt64(IntPtr instance, int slot)
    {
        long result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    public static string GetString(IntPtr instance, int slot)
    {
        IntPtr hstring;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, &hstring));
        try
        {
            return StringOf(hstring);
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    /// <summary>A setter of an enum or Int32.</summary>
    public static void SetInt32(IntPtr instance, int slot, int value)
        => Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(instance, slot))(instance, value));

    public static void SetBool(IntPtr instance, int slot, bool value)
        => Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(instance, slot))(instance, value ? (byte)1 : (byte)0));

    /// <summary>A setter of a TimeSpan (100 ns ticks) or Int64.</summary>
    public static void SetInt64(IntPtr instance, int slot, long value)
        => Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(instance, slot))(instance, value));

    public static void SetObject(IntPtr instance, int slot, IntPtr value)
        => Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(instance, slot))(instance, value));

    /// <summary>A method taking one interface pointer and returning one (a factory's Create…).</summary>
    public static IntPtr Create(IntPtr instance, int slot, IntPtr argument)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, argument, &result));
        return result;
    }

    public static void SetString(IntPtr instance, int slot, string? value)
    {
        var hstring = HString(value);
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(instance, slot))(instance, hstring));
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    /// <summary><c>add_X(handler, out token)</c>.</summary>
    public static long AddHandler(IntPtr instance, int slot, IntPtr handler)
    {
        long token;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(instance, slot))(instance, handler, &token));
        return token;
    }

    /// <summary><c>remove_X(token)</c>.</summary>
    public static void RemoveHandler(IntPtr instance, int slot, long token)
        => Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(instance, slot))(instance, token));
}

/// <summary>
/// A WinRT <c>TypedEventHandler&lt;TSender, TArgs&gt;</c> implemented by hand: IUnknown (answering its own IID,
/// IUnknown and IAgileObject, so it is called on any thread) and <c>Invoke(sender, args)</c>, which calls
/// <see cref="Handler"/> with the args pointer. Ref-counted; the managed callback lives until the last Release.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WinRTHandler
{
    private const int SOk = 0;
    private const int ENoInterface = unchecked((int)0x80004002);

    private static readonly IntPtr* Vtable = CreateVtable();

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public IntPtr Vtable;
        public int RefCount;
        public Guid Iid;
        public IntPtr Callback;
    }

    public delegate void Handler(IntPtr args);

    /// <summary>A handler object with one reference (the caller's), for the delegate interface <paramref name="iid"/>.</summary>
    public static IntPtr Create(Guid iid, Handler handler)
    {
        var instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
        *instance = new Instance { Vtable = (IntPtr)Vtable, RefCount = 1, Iid = iid, Callback = GCHandle.ToIntPtr(GCHandle.Alloc(handler)) };
        return (IntPtr)instance;
    }

    private static IntPtr* CreateVtable()
    {
        var vtable = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, Guid*, IntPtr*, int>)&QueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, uint>)&Release;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<Instance*, IntPtr, IntPtr, int>)&Invoke;
        return vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int QueryInterface(Instance* self, Guid* iid, IntPtr* result)
    {
        if (*iid == self->Iid || *iid == WinRT.IUnknown || *iid == WinRT.IAgileObject)
        {
            Interlocked.Increment(ref self->RefCount);
            *result = (IntPtr)self;
            return SOk;
        }

        *result = IntPtr.Zero;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint Release(Instance* self)
    {
        var count = Interlocked.Decrement(ref self->RefCount);
        if (count == 0)
        {
            GCHandle.FromIntPtr(self->Callback).Free();
            NativeMemory.Free(self);
        }

        return (uint)count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int Invoke(Instance* self, IntPtr sender, IntPtr args)
    {
        try
        {
            ((Handler)GCHandle.FromIntPtr(self->Callback).Target!)(args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let an exception cross into the OS; a lost key press is the worst outcome.
        }

        return SOk;
    }
}
