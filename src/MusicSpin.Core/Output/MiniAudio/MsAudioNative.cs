using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>
/// P/Invoke declarations for native/msaudio (miniaudio plus a flat C API, built per RID by eng/build-native.*).
/// The structs mirror native/msaudio/msaudio.h field for field; MsAudioLayoutTests checks them against msa_layout().
/// </summary>
internal static unsafe partial class MsAudioNative
{
    public const string Library = "msaudio";
    public const int AbiVersion = 3;
    public const int IdBytes = 256;
    public const int NameBytes = 256;
    public const int MaxNativeFormats = 64;

    // ma_result values the managed side tells apart.
    public const int Success = 0;
    public const int InvalidArgs = -2;

    public const int FormatU8 = 1;
    public const int FormatS16 = 2;
    public const int FormatS24 = 3;
    public const int FormatS32 = 4;
    public const int FormatF32 = 5;

    public const int NotifyStopped = 1;
    public const int NotifyRerouted = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeFormat
    {
        public int Format;
        public int Channels;
        public int SampleRate;
        public int Flags;
    }

    [InlineArray(MaxNativeFormats)]
    public struct NativeFormats
    {
        private NativeFormat _first;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public fixed byte Id[IdBytes];
        public fixed byte Name[NameBytes];
        public int IsDefault;
        public int NativeFormatCount;
        public NativeFormats Formats;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenParams
    {
        public int Format;
        public int Channels;
        public int SampleRate;
        public int Exclusive;
        public int PeriodFrames;
        public int HasDeviceId;
        public fixed byte DeviceId[IdBytes];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Opened
    {
        public int Format;
        public int Channels;
        public int SampleRate;
        public int Exclusive;
        public int PeriodFrames;
        public fixed byte Name[NameBytes];
    }

    [LibraryImport(Library, EntryPoint = "msa_abi_version")]
    public static partial int AbiVersionOf();

    [LibraryImport(Library, EntryPoint = "msa_layout")]
    public static partial int Layout(int which, int field);

    [LibraryImport(Library, EntryPoint = "msa_result_description")]
    public static partial nint ResultDescription(int result);

    [LibraryImport(Library, EntryPoint = "msa_context_init")]
    public static partial int ContextInit(int nullBackend, out nint context);

    [LibraryImport(Library, EntryPoint = "msa_context_uninit")]
    public static partial void ContextUninit(nint context);

    [LibraryImport(Library, EntryPoint = "msa_context_backend")]
    public static partial nint ContextBackend(nint context);

    [LibraryImport(Library, EntryPoint = "msa_context_refresh")]
    public static partial int ContextRefresh(nint context, out int count);

    [LibraryImport(Library, EntryPoint = "msa_device_info_at")]
    public static partial int DeviceInfoAt(nint context, int index, DeviceInfo* info);

    [LibraryImport(Library, EntryPoint = "msa_device_open")]
    public static partial int DeviceOpen(
        nint context,
        OpenParams* parameters,
        delegate* unmanaged[Cdecl]<nint, nint, uint, void> data,
        delegate* unmanaged[Cdecl]<nint, int, void> notify,
        nint user,
        out nint device,
        Opened* opened);

    [LibraryImport(Library, EntryPoint = "msa_device_start")]
    public static partial int DeviceStart(nint device);

    [LibraryImport(Library, EntryPoint = "msa_device_stop")]
    public static partial int DeviceStop(nint device);

    [LibraryImport(Library, EntryPoint = "msa_device_close")]
    public static partial void DeviceClose(nint device);

    [LibraryImport(Library, EntryPoint = "msa_device_opened")]
    public static partial int DeviceOpened(nint device, Opened* opened);

    [LibraryImport(Library, EntryPoint = "msa_device_render")]
    public static partial int DeviceRender(nint device, void* output, uint frames);

    [LibraryImport(Library, EntryPoint = "msa_converter_init")]
    public static partial int ConverterInit(int inChannels, int inRate, int outChannels, int outRate, out nint converter);

    [LibraryImport(Library, EntryPoint = "msa_converter_uninit")]
    public static partial void ConverterUninit(nint converter);

    [LibraryImport(Library, EntryPoint = "msa_converter_process")]
    public static partial int ConverterProcess(nint converter, float* input, ref ulong inFrames, float* output, ref ulong outFrames);

    [LibraryImport(Library, EntryPoint = "msa_converter_reset")]
    public static partial int ConverterReset(nint converter);

    /// <summary>miniaudio's text for a result code, e.g. "Device type or share mode not supported".</summary>
    public static string Describe(int result) => Marshal.PtrToStringUTF8(ResultDescription(result)) ?? $"error {result}";

    public static string Utf8(byte* text, int capacity)
    {
        var span = new ReadOnlySpan<byte>(text, capacity);
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public static DeviceSample? ToSample(int format) => format switch
    {
        FormatS16 => DeviceSample.S16,
        FormatS24 => DeviceSample.S24,
        FormatS32 => DeviceSample.S32,
        FormatF32 => DeviceSample.F32,
        _ => null,
    };

    public static int FromSample(DeviceSample sample) => sample switch
    {
        DeviceSample.S16 => FormatS16,
        DeviceSample.S24 => FormatS24,
        DeviceSample.S32 => FormatS32,
        DeviceSample.F32 => FormatF32,
        _ => throw new ArgumentOutOfRangeException(nameof(sample), sample, "miniaudio has no 24-in-32 format."),
    };

    /// <summary>The name for a miniaudio format, "u8" included (some devices list it natively).</summary>
    public static string FormatName(int format)
        => format == FormatU8 ? "u8" : ToSample(format) is { } s ? DeviceSamples.Name(s) : "unknown";
}
