using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output.Wasapi;

namespace MusicSpin.Core.Output.MiniAudio;

/// <summary>
/// The real backend: miniaudio's low-level device API through native/msaudio. No mixer, no pan law, no gain: the
/// device callback pulls our float samples and converts them to the device format (bit-exact for integer formats;
/// volume is applied upstream by the playback buffer). On Windows, exclusive mode goes through
/// <see cref="WasapiExclusiveStream"/>, because miniaudio opens exclusive WASAPI streams at the endpoint's Default
/// Format and resamples (#148).
/// </summary>
internal sealed unsafe class MiniAudioBackend : IAudioBackend
{
    private readonly Lock _lock = new();
    private nint _context;

    /// <param name="nullBackend">true: miniaudio's silent Null backend only (tests, machines without audio).</param>
    public MiniAudioBackend(bool nullBackend = false)
    {
        var abi = MsAudioNative.AbiVersionOf();
        if (abi != MsAudioNative.AbiVersion)
        {
            throw new InvalidOperationException($"native msaudio ABI {abi}, expected {MsAudioNative.AbiVersion}: rebuild it with eng/build-native.");
        }

        var result = MsAudioNative.ContextInit(nullBackend ? 1 : 0, out _context);
        if (result != MsAudioNative.Success)
        {
            throw new InvalidOperationException($"miniaudio context: {MsAudioNative.Describe(result)}");
        }

        ActiveBackend = Marshal.PtrToStringUTF8(MsAudioNative.ContextBackend(_context)) ?? "unknown";
    }

    public string ActiveBackend { get; }

    public IReadOnlyList<BackendDevice> RefreshPlaybackDevices()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_context == 0, this);
            Check(MsAudioNative.ContextRefresh(_context, out var count), "list devices");
            var devices = new List<BackendDevice>(count);
            var info = new MsAudioNative.DeviceInfo();
            for (var i = 0; i < count; i++)
            {
                Check(MsAudioNative.DeviceInfoAt(_context, i, &info), "read device info");
                devices.Add(ToDevice(ref info));
            }

            return devices;
        }
    }

    private BackendDevice ToDevice(ref MsAudioNative.DeviceInfo info)
    {
        var formats = new List<(string, int, int)>(info.NativeFormatCount);
        for (var f = 0; f < Math.Min(info.NativeFormatCount, MsAudioNative.MaxNativeFormats); f++)
        {
            var nf = info.Formats[f];
            formats.Add((MsAudioNative.FormatName(nf.Format), nf.Channels, nf.SampleRate));
        }

        var id = new byte[MsAudioNative.IdBytes];
        fixed (byte* p = info.Id)
        {
            new ReadOnlySpan<byte>(p, id.Length).CopyTo(id);
        }

        string name;
        fixed (byte* p = info.Name)
        {
            name = MsAudioNative.Utf8(p, MsAudioNative.NameBytes);
        }

        return new BackendDevice(name, info.IsDefault != 0, id, formats, EndpointIdOf(id));
    }

    /// <summary>On WASAPI miniaudio's device id is the UTF-16 endpoint id (wchar_t[64]).</summary>
    private string? EndpointIdOf(byte[] id)
    {
        if (!OperatingSystem.IsWindows() || !ActiveBackend.Equals("WASAPI", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var chars = MemoryMarshal.Cast<byte, char>(id.AsSpan(0, 128));
        var end = chars.IndexOf('\0');
        var text = new string(end < 0 ? chars : chars[..end]);
        return text.Length == 0 ? null : text;
    }

    public IPlaybackStream Open(BackendDevice? device, OpenRequest request, IPcmSource source)
    {
        var name = device?.Name ?? DefaultDeviceName();
        if (request.Exclusive && OperatingSystem.IsWindows() && ActiveBackend.Equals("WASAPI", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return WasapiExclusiveStream.Open(device?.EndpointId, name, new PcmFormat(request.SampleRate, request.Channels, request.SourceBits), source);
            }
            catch (ExclusiveUnavailableException ex)
            {
                var shared = OpenDevice(device, request with { Format = DeviceSample.F32, Exclusive = false }, source, name);
                shared.Notice = $"Exclusive mode unavailable: {ex.Message}. Playing in shared mode through the Windows mixer.";
                return shared;
            }
        }

        try
        {
            return OpenDevice(device, request, source, name);
        }
        catch (MiniAudioException ex) when (request.Exclusive && ex.Result != MsAudioNative.InvalidArgs)
        {
            // ALSA hw busy, CoreAudio hog refused...: play shared rather than not at all, and say so.
            var shared = OpenDevice(device, request with { Format = DeviceSample.F32, Exclusive = false }, source, name);
            shared.Notice = $"Exclusive mode unavailable: {ex.Message}. Playing in shared mode.";
            return shared;
        }
    }

    private string? DefaultDeviceName()
    {
        try
        {
            return RefreshPlaybackDevices().FirstOrDefault(d => d.IsDefault)?.Name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private MiniAudioStream OpenDevice(BackendDevice? device, OpenRequest request, IPcmSource source, string? name)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_context == 0, this);
            var parameters = new MsAudioNative.OpenParams
            {
                Format = MsAudioNative.FromSample(request.Format),
                Channels = request.Channels,
                SampleRate = request.SampleRate,
                Exclusive = request.Exclusive ? 1 : 0,
                HasDeviceId = device is null ? 0 : 1,
            };
            if (device is not null)
            {
                device.Id.AsSpan(0, Math.Min(device.Id.Length, MsAudioNative.IdBytes)).CopyTo(new Span<byte>(parameters.DeviceId, MsAudioNative.IdBytes));
            }

            return MiniAudioStream.Open(_context, &parameters, request, source, name);
        }
    }

    internal static void Check(int result, string what)
    {
        if (result != MsAudioNative.Success)
        {
            throw new InvalidOperationException($"Could not {what}: {MsAudioNative.Describe(result)}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_context != 0)
            {
                MsAudioNative.ContextUninit(_context);
                _context = 0;
            }
        }
    }
}

/// <summary>
/// One open miniaudio device. The native callback (on miniaudio's audio thread) calls back into <see cref="Render"/>
/// through a GCHandle; nothing in it throws or blocks.
/// </summary>
internal sealed unsafe class MiniAudioStream : IPlaybackStream
{
    private readonly IPcmSource _source;
    private readonly DeviceSample _format;
    private readonly int _channels;
    private readonly Lock _lock = new();
    private GCHandle _self;
    private nint _device;
    private volatile float[] _scratch;
    private volatile bool _paused = true;
    private volatile bool _stopping;
    private volatile bool _pausing;

    private MiniAudioStream(IPcmSource source, OpenRequest request)
    {
        _source = source;
        _format = request.Format;
        _channels = request.Channels;
        _scratch = new float[ScratchFrames(0) * Math.Max(1, request.Channels)];
    }

    /// <summary>The conversion buffer's frames: a period or two, allocated here and never on the audio thread.</summary>
    private static int ScratchFrames(int periodFrames) => Math.Max(4096, periodFrames * 2);

    public OpenedOutput Opened { get; private set; } = null!;

    /// <summary>Set by the backend when this shared stream replaced a refused exclusive one.</summary>
    public string? Notice
    {
        get => Opened.Notice;
        set => Opened = Opened with { Notice = value };
    }

    public event EventHandler<string>? Lost;

    public event EventHandler? Rerouted;

    /// <summary>The device handle, for <see cref="RenderForTest"/>.</summary>
    internal nint Handle => _device;

    internal static MiniAudioStream Open(nint context, MsAudioNative.OpenParams* parameters, OpenRequest request, IPcmSource source, string? name)
    {
        var stream = new MiniAudioStream(source, request);
        stream._self = GCHandle.Alloc(stream);
        var opened = new MsAudioNative.Opened();
        var result = MsAudioNative.DeviceOpen(context, parameters, &OnData, &OnNotify, GCHandle.ToIntPtr(stream._self), out stream._device, &opened);
        if (result != MsAudioNative.Success)
        {
            stream._self.Free();
            throw new MiniAudioException(result);
        }

        string deviceName = MsAudioNative.Utf8(opened.Name, MsAudioNative.NameBytes);
        var sample = MsAudioNative.ToSample(opened.Format);
        stream.Opened = new OpenedOutput(
            string.IsNullOrEmpty(deviceName) ? name : deviceName,
            opened.Exclusive != 0,
            opened.SampleRate,
            opened.Channels,
            MsAudioNative.FormatName(opened.Format),
            sample is { } s ? DeviceSamples.ValidBits(s) : 8,
            Verified: true);
        stream._scratch = new float[ScratchFrames(opened.PeriodFrames) * Math.Max(1, opened.Channels)];
        return stream;
    }

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_device == 0, this);
            _paused = false;
            try
            {
                MiniAudioBackend.Check(MsAudioNative.DeviceStart(_device), "start the device");
            }
            catch (Exception)
            {
                _paused = true;
                throw;
            }
        }
    }

    /// <summary>
    /// Stops reading the source, then stops the device: miniaudio plays out what it already holds and returns, so
    /// everything read has been heard and the source's position is where playback resumes. The device stays open
    /// (shared mode lets other apps keep using it; an exclusive stream is closed by the owner instead).
    /// </summary>
    public bool Pause()
    {
        lock (_lock)
        {
            _paused = true;
            if (_device == 0)
            {
                return true;
            }

            // The Stopped notification of a stop we asked for is not a lost device.
            _pausing = true;
            try
            {
                // Blocks until the device has stopped (already stopped is a success): nothing reads the source after that.
                return MsAudioNative.DeviceStop(_device) == 0;
            }
            finally
            {
                _pausing = false;
            }
        }
    }

    /// <summary>Tests: lets <see cref="RenderForTest"/> pull from the source without starting the device thread.</summary>
    internal void ResumeForTest() => _paused = false;

    /// <summary>Tests: renders <paramref name="frames"/> through miniaudio's real playback path into <paramref name="output"/>.</summary>
    internal int RenderForTest(Span<byte> output, int frames)
    {
        fixed (byte* p = output)
        {
            return MsAudioNative.DeviceRender(_device, p, (uint)frames);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnData(nint user, nint output, uint frames)
    {
        if (GCHandle.FromIntPtr(user).Target is MiniAudioStream stream)
        {
            stream.Render(output, (int)frames);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNotify(nint user, int type)
    {
        if (GCHandle.FromIntPtr(user).Target is not MiniAudioStream stream || stream._stopping)
        {
            return;
        }

        if (type == MsAudioNative.NotifyStopped)
        {
            if (stream._pausing)
            {
                return; // our own Pause
            }

            // Stopped without us asking: the device went away (unplugged, invalidated, the server died).
            stream.RaiseLost("The audio device stopped unexpectedly (disconnected or taken away).");
        }
        else if (type == MsAudioNative.NotifyRerouted)
        {
            stream.OnRerouted();
        }
    }

    /// <summary>miniaudio moved a default-device stream to the new default: read back what it runs at now.</summary>
    internal void OnRerouted()
    {
        try
        {
            var opened = new MsAudioNative.Opened();
            if (_device != 0 && MsAudioNative.DeviceOpened(_device, &opened) == MsAudioNative.Success)
            {
                _scratch = new float[ScratchFrames(opened.PeriodFrames) * Math.Max(1, opened.Channels)]; // here, off the audio thread
                var name = MsAudioNative.Utf8(opened.Name, MsAudioNative.NameBytes);
                var sample = MsAudioNative.ToSample(opened.Format);
                Opened = Opened with
                {
                    Device = string.IsNullOrEmpty(name) ? Opened.Device : name,
                    Exclusive = opened.Exclusive != 0,
                    SampleRate = opened.SampleRate,
                    Channels = opened.Channels,
                    SampleFormat = MsAudioNative.FormatName(opened.Format),
                    BitsPerSample = sample is { } s ? DeviceSamples.ValidBits(s) : 8,
                    Notice = null,
                };
            }

            Rerouted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Never unwind into native code.
        }
    }

    internal void RaiseLost(string reason)
    {
        try
        {
            Lost?.Invoke(this, reason);
        }
        catch (Exception)
        {
            // Never unwind into native code.
        }
    }

    private void Render(nint output, int frames)
    {
        var samples = frames * _channels;
        var bytes = new Span<byte>((void*)output, samples * DeviceSamples.ContainerBytes(_format));
        try
        {
            if (_paused)
            {
                bytes.Clear();
                return;
            }

            if (_format == DeviceSample.F32)
            {
                PcmReading.Fill(_source, MemoryMarshal.Cast<byte, float>(bytes));
                return;
            }

            var scratch = _scratch;
            if (scratch.Length < samples)
            {
                scratch = _scratch = new float[samples]; // only if the backend asks for more than two periods
            }

            var floats = scratch.AsSpan(0, samples);
            PcmReading.Fill(_source, floats);
            DeviceSamples.Convert(floats, bytes, _format);
        }
        catch (Exception)
        {
            bytes.Clear(); // never unwind into native code; silence instead
        }
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "Handle freed here.")]
    public void Dispose()
    {
        lock (_lock)
        {
            if (_device == 0)
            {
                return;
            }

            _stopping = true;
            MsAudioNative.DeviceClose(_device); // stops and joins the audio thread first
            _device = 0;
            _self.Free();
        }
    }
}

/// <summary>A miniaudio call failed; <see cref="Result"/> is the ma_result.</summary>
internal sealed class MiniAudioException(int result) : InvalidOperationException(MsAudioNative.Describe(result))
{
    public int Result { get; } = result;
}
