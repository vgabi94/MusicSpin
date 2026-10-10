using MusicSpin.Cli;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Tests.Shared;
using ExitCodes = MusicSpin.Cli.ExitCodes;

namespace MusicSpin.Tests.Cli;

/// <summary>doctor/devices/formats in text mode and with a failing audio backend.</summary>
public sealed class DiagnosticsCliTests : IAsyncDisposable
{
    private static readonly FFmpegStatus Gpl = new(true, "9.0-test", "GPL version 2 or later", "/opt/ff", new Dictionary<string, int>(), null);
    private static readonly FFmpegStatus Missing = new(false, null, null, "", new Dictionary<string, int>(), "libavcodec.so.62 not found");

    private readonly List<CliHarness> _harnesses = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _harnesses)
        {
            await h.DisposeAsync();
        }
    }

    private CliHarness Harness(CliHarness h)
    {
        _harnesses.Add(h);
        return h;
    }

    [Fact]
    public async Task Doctor_text_lists_each_check_as_ok()
    {
        var cli = Harness(new CliHarness { Outputs = _ => new ManualOutputProvider() });

        var result = await cli.RunAsync("doctor");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Lines();
        Assert.StartsWith("music-spin ", lines[0], StringComparison.Ordinal);
        Assert.Equal("[ok]   FFmpeg 9.0-test, license: LGPL version 2.1 or later", lines[1]);
        Assert.Equal("[ok]   Audio: manual, 1 playback device(s)", lines[2]);
        Assert.Equal($"[ok]   Data: {cli.DataDir} (portable)", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.True(File.Exists(Path.Combine(cli.DataDir, "settings.json"))); // first run creates the defaults
    }

    [Fact]
    public async Task Doctor_text_flags_a_gpl_ffmpeg_and_a_corrupt_settings_file()
    {
        var cli = Harness(new CliHarness { FFmpeg = Gpl });
        Directory.CreateDirectory(cli.DataDir);
        await File.WriteAllTextAsync(Path.Combine(cli.DataDir, "settings.json"), "{ nope", TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("doctor", "--backend", "null");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[ok]   FFmpeg 9.0-test, license: GPL version 2 or later (/opt/ff) (not LGPL: fine locally, don't redistribute)", result.Lines());
        Assert.Contains(result.Lines(), l => l.StartsWith("[warn] Settings: ", StringComparison.Ordinal) && l.EndsWith("(defaults in use)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Doctor_text_reports_every_failure_and_exits_with_the_first()
    {
        var cli = Harness(new CliHarness { FFmpeg = Missing, Outputs = _ => new BrokenBackend() });
        await File.WriteAllTextAsync(cli.DataDir, "a file where the data folder should be", TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("doctor");

        Assert.Equal(ExitCodes.DecoderUnavailable, result.ExitCode);
        var lines = result.Lines();
        Assert.Equal("[fail] FFmpeg: libavcodec.so.62 not found", lines[1]);
        Assert.Equal("[fail] Audio: no audio server", lines[2]);
        Assert.StartsWith($"[fail] Data: Data folder '{cli.DataDir}' is not writable", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_missing_audio_backend_is_exit_7_with_the_report_attached()
    {
        var backend = new BrokenBackend();
        var cli = Harness(new CliHarness { Outputs = _ => backend });

        var result = await cli.RunAsync("doctor", "--json");

        Assert.Equal(ExitCodes.AudioDeviceError, result.ExitCode);
        var json = result.Json();
        Assert.Equal("audio_device_error", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("no audio server", json.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("no audio server", json.GetProperty("data").GetProperty("audioError").GetString());
        Assert.True(json.GetProperty("data").GetProperty("decoder").GetProperty("available").GetBoolean());
        Assert.True(backend.Disposed);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Doctor_reads_the_settings_of_a_read_only_data_folder_without_writing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs POSIX permissions and a non-root user");
        var cli = Harness(new CliHarness());
        Directory.CreateDirectory(cli.DataDir);
        var settings = Path.Combine(cli.DataDir, "settings.json");
        await File.WriteAllTextAsync(settings, "{ broken", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            var result = await cli.RunAsync("doctor", "--backend", "null", "--json");

            Assert.Equal(ExitCodes.DataDirNotWritable, result.ExitCode);
            var data = result.Json().GetProperty("data").GetProperty("dataDir");
            Assert.False(data.GetProperty("writable").GetBoolean());
            Assert.Contains("settings.json", data.GetProperty("settingsWarning").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(cli.DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.Equal("{ broken", await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Devices_text_marks_the_default_device()
    {
        var cli = Harness(new CliHarness { Outputs = _ => new ListedBackend(
            new AudioDeviceInfo("0", "Speakers", false, "fake", [], [], []),
            new AudioDeviceInfo("1", "USB DAC", true, "fake", ["s24"], [96_000], [2])) });

        var result = await cli.RunAsync("devices");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Backend: listed", "0    Speakers", "1  * USB DAC"], result.Lines());
    }

    [Fact]
    public async Task Devices_text_says_when_there_are_none_and_failures_are_exit_7()
    {
        var empty = await Harness(new CliHarness { Outputs = _ => new ListedBackend() }).RunAsync("devices");
        Assert.Equal(["Backend: listed", "(no playback devices)"], empty.Lines());

        var broken = await Harness(new CliHarness { Outputs = _ => new BrokenBackend() }).RunAsync("devices", "--json");
        Assert.Equal(ExitCodes.AudioDeviceError, broken.ExitCode);
        Assert.Equal("audio_device_error", broken.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Formats_text_lists_each_format_with_its_flags()
    {
        var cli = Harness(new CliHarness());

        var result = await cli.RunAsync("formats");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Lines();
        Assert.Equal("Decoder: FFmpeg 9.0-test (LGPL version 2.1 or later)", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("[x] FLAC", StringComparison.Ordinal) && l.Contains(".flac", StringComparison.Ordinal) && l.EndsWith("lossless, hi-res", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[x] MP3", StringComparison.Ordinal) && l.EndsWith("lossy", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("All audio decoders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Formats_text_without_ffmpeg_marks_everything_unsupported()
    {
        var cli = Harness(new CliHarness { FFmpeg = Missing, Decoders = new FakeDecoderFactory { IsAvailable = false } });

        var result = await cli.RunAsync("formats", "--all");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Decoder: FFmpeg NOT available - libavcodec.so.62 not found", result.Lines()[0]);
        Assert.All(result.Lines().Skip(1), l => Assert.StartsWith("[ ]", l, StringComparison.Ordinal));
        Assert.DoesNotContain("All audio decoders", result.Out, StringComparison.Ordinal); // --all needs FFmpeg
    }

    [Fact]
    public async Task Formats_all_lists_every_decoder_of_the_loaded_ffmpeg()
    {
        Assert.SkipUnless(FFmpegRuntime.IsAvailable, "needs the FFmpeg shared libraries (eng/get-ffmpeg)");
        var cli = Harness(new CliHarness());

        var json = await cli.RunAsync("formats", "--all", "--json");
        var text = await cli.RunAsync("formats", "--all");

        var decoders = json.Json().GetProperty("data").GetProperty("allDecoders");
        Assert.Contains(decoders.EnumerateArray(), d => d.GetProperty("name").GetString() == "flac");
        Assert.Contains($"All audio decoders ({decoders.GetArrayLength()}):", text.Lines());
        Assert.Contains(text.Lines(), l => l.StartsWith("flac ", StringComparison.Ordinal));
    }

    private sealed class BrokenBackend : IAudioOutputProvider, IDisposable
    {
        public bool Disposed { get; private set; }

        public string Name => "broken";

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, "no audio server");

        public IAudioOutput Create(AudioOutputOptions options) => throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, "no audio server");

        public void Dispose() => Disposed = true;
    }

    private sealed class ListedBackend(params AudioDeviceInfo[] devices) : IAudioOutputProvider
    {
        public string Name => "listed";

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => devices;

        public IAudioOutput Create(AudioOutputOptions options) => throw new NotSupportedException();
    }
}
