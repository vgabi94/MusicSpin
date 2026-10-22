using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output.SoundFlow;
using SoundFlow.Interfaces;

namespace MusicSpin.Core.Output.Wasapi;

/// <summary>Why WASAPI would not give an exclusive stream; the caller falls back to shared mode and says why.</summary>
internal sealed class ExclusiveUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A WASAPI exclusive-mode, event-driven render stream opened at exactly the track's rate, channels and bit depth.
/// miniaudio (0.11.x, inside SoundFlow) opens exclusive WASAPI streams at the device's "Default Format" from the
/// Windows sound settings and resamples to it, ignoring the requested rate (#148); this path asks WASAPI for the
/// source format itself, so a 44.1 kHz track runs the DAC at 44.1 kHz or fails loudly.
/// All COM objects live on the stream's own MTA render thread.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Native WASAPI; needs Windows and a real device. Format choice and conversion are tested in DeviceSamples.")]
internal sealed class WasapiExclusiveStream : IPlaybackStream
{
    private const int ClsctxAll = 0x17;
    private const int ShareModeExclusive = 1;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const int BufferFlagsSilent = 0x2;
    private const int SBufferSizeNotAligned = unchecked((int)0x88890019);

    private readonly string? _endpointId;
    private readonly PcmFormat _source;
    private readonly ISoundDataProvider _data;
    private readonly string? _deviceName;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly AutoResetEvent _audioEvent = new(false);
    private readonly AutoResetEvent _control = new(false);
    private volatile bool _quit;
    private volatile bool _startRequested;
    private volatile bool _paused;
    private Exception? _initError;

    private WasapiExclusiveStream(string? endpointId, string? deviceName, PcmFormat source, ISoundDataProvider data)
    {
        _endpointId = endpointId;
        _deviceName = deviceName;
        _source = source;
        _data = data;
        _thread = new Thread(Run) { IsBackground = true, Name = "MusicSpin WASAPI exclusive", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public OpenedOutput Opened { get; private set; } = null!;

    /// <summary>Opens the endpoint (null: the default render device) exclusively at the source's format.</summary>
    /// <exception cref="ExclusiveUnavailableException">WASAPI refused every exact format, or exclusive access.</exception>
    public static WasapiExclusiveStream Open(string? endpointId, string? deviceName, PcmFormat source, ISoundDataProvider data)
    {
        var stream = new WasapiExclusiveStream(endpointId, deviceName, source, data);
        stream._thread.Start();
        stream._ready.Wait(); // set by the render thread once Initialize succeeded or failed
        if (stream._initError is { } error)
        {
            stream.Dispose();
            throw error as ExclusiveUnavailableException ?? new ExclusiveUnavailableException(error.Message, error);
        }

        return stream;
    }

    public void Start()
    {
        _paused = false;
        _startRequested = true;
        _control.Set();
    }

    public void Pause() => _paused = true;

    public void Dispose()
    {
        _quit = true;
        _control.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _ready.Dispose();
        _audioEvent.Dispose();
        _control.Dispose();
    }

    private void Run()
    {
        object? enumeratorObj = null, deviceObj = null, clientObj = null, renderObj = null;
        var formatPtr = IntPtr.Zero;
        var mmcss = IntPtr.Zero;
        try
        {
            enumeratorObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
            IMMDevice device;
            var found = _endpointId is null
                ? enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device)
                : enumerator.GetDevice(_endpointId, out device);
            Check(found, "find the device");
            deviceObj = device;

            clientObj = Activate(device);
            var client = (IAudioClient)clientObj;

            DeviceSample? chosen = null;
            var refused = new List<string>();
            foreach (var candidate in DeviceSamples.ExclusiveCandidates(_source.SourceBitsPerSample))
            {
                var ptr = AllocFormat(candidate);
                var hr = client.IsFormatSupported(ShareModeExclusive, ptr, IntPtr.Zero);
                if (hr == 0)
                {
                    chosen = candidate;
                    formatPtr = ptr;
                    break;
                }

                Marshal.FreeHGlobal(ptr);
                refused.Add(DeviceSamples.Name(candidate));
            }

            if (chosen is not { } format)
            {
                throw new ExclusiveUnavailableException(string.Create(CultureInfo.InvariantCulture,
                    $"the device does not accept {_source.SampleRate / 1000.0:0.###} kHz, {_source.Channels} ch in exclusive mode (tried {string.Join(", ", refused)})"));
            }

            Check(client.GetDevicePeriod(out var period, out _), "read the device period");
            var init = client.Initialize(ShareModeExclusive, StreamFlagsEventCallback, period, period, formatPtr, IntPtr.Zero);
            if (init == SBufferSizeNotAligned)
            {
                // The documented retry: align the period to the buffer size WASAPI wants, on a fresh client.
                Check(client.GetBufferSize(out var aligned), "read the aligned buffer size");
                Marshal.ReleaseComObject(clientObj);
                period = (long)((10_000_000.0 * aligned / _source.SampleRate) + 0.5);
                clientObj = Activate(device);
                client = (IAudioClient)clientObj;
                init = client.Initialize(ShareModeExclusive, StreamFlagsEventCallback, period, period, formatPtr, IntPtr.Zero);
            }

            if (init != 0)
            {
                throw new ExclusiveUnavailableException(Describe(init));
            }

            Check(client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle()), "set the event handle");
            Check(client.GetBufferSize(out var bufferFrames), "read the buffer size");
            var renderIid = typeof(IAudioRenderClient).GUID;
            Check(client.GetService(ref renderIid, out renderObj), "get the render client");
            var render = (IAudioRenderClient)renderObj;

            Opened = new OpenedOutput(_deviceName, Exclusive: true, _source.SampleRate, _source.Channels,
                DeviceSamples.Name(format), DeviceSamples.ValidBits(format), Verified: true);

            var floats = new float[bufferFrames * _source.Channels];
            Fill(render, format, bufferFrames, floats); // pre-roll, as WASAPI requires before Start
            _ready.Set();

            mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", out _);
            WaitHandle[] waits = [_audioEvent, _control];
            var started = false;
            while (!_quit)
            {
                var which = WaitHandle.WaitAny(waits);
                if (_quit)
                {
                    break;
                }

                if (_startRequested && !started)
                {
                    Check(client.Start(), "start the stream");
                    started = true;
                }

                if (which == 0 && Fill(render, format, bufferFrames, floats) != 0)
                {
                    break; // device unplugged or invalidated: stop rendering
                }
            }

            if (started)
            {
                client.Stop();
            }
        }
        catch (Exception ex)
        {
            _initError ??= ex;
        }
        finally
        {
            if (mmcss != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(mmcss);
            }

            foreach (var obj in new[] { renderObj, clientObj, deviceObj, enumeratorObj })
            {
                if (obj is not null)
                {
                    Marshal.ReleaseComObject(obj);
                }
            }

            if (formatPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            if (!_ready.IsSet)
            {
                _ready.Set();
            }
        }
    }

    private int Fill(IAudioRenderClient render, DeviceSample format, uint frames, float[] floats)
    {
        var hr = render.GetBuffer(frames, out var buffer);
        if (hr != 0)
        {
            return hr;
        }

        if (_paused || !_startRequested)
        {
            return render.ReleaseBuffer(frames, BufferFlagsSilent);
        }

        var read = Math.Max(0, _data.ReadBytes(floats));
        floats.AsSpan(read).Clear(); // end of stream or underrun: silence
        unsafe
        {
            var bytes = new Span<byte>((void*)buffer, floats.Length * DeviceSamples.ContainerBytes(format));
            DeviceSamples.Convert(floats, bytes, format);
        }

        return render.ReleaseBuffer(frames, 0);
    }

    private static object Activate(IMMDevice device)
    {
        var iid = typeof(IAudioClient).GUID;
        Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var client), "activate the audio client");
        return client;
    }

    private IntPtr AllocFormat(DeviceSample format)
    {
        var container = DeviceSamples.ContainerBytes(format);
        var channels = (ushort)_source.Channels;
        var wf = new WaveFormatExtensible
        {
            FormatTag = 0xFFFE,
            Channels = channels,
            SamplesPerSec = (uint)_source.SampleRate,
            BlockAlign = (ushort)(container * channels),
            AvgBytesPerSec = (uint)(container * channels * _source.SampleRate),
            BitsPerSample = (ushort)(container * 8),
            Size = 22,
            ValidBitsPerSample = (ushort)DeviceSamples.ValidBits(format),
            ChannelMask = channels switch { 1 => 0x4u, 2 => 0x3u, 4 => 0x33u, 6 => 0x3Fu, 8 => 0x63Fu, _ => 0u },
            SubFormat = format == DeviceSample.F32
                ? new Guid("00000003-0000-0010-8000-00aa00389b71")
                : new Guid("00000001-0000-0010-8000-00aa00389b71"),
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
        Marshal.StructureToPtr(wf, ptr, false);
        return ptr;
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new ExclusiveUnavailableException($"could not {what} ({Describe(hr)})");
        }
    }

    internal static string Describe(int hr) => unchecked((uint)hr) switch
    {
        0x88890008 => "the device refused the format (AUDCLNT_E_UNSUPPORTED_FORMAT)",
        0x8889000A => "another app is using the device (AUDCLNT_E_DEVICE_IN_USE)",
        0x8889000E => "exclusive mode is not allowed for this device in Windows sound settings (AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED)",
        0x88890004 => "the device was unplugged or disabled (AUDCLNT_E_DEVICE_INVALIDATED)",
        0x80070490 => "the device was not found",
        var x => string.Create(CultureInfo.InvariantCulture, $"HRESULT 0x{x:X8}"),
    };

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, int flags);
    }
}
