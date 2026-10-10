using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>
/// Ctrl+C (exit 130) and unexpected failures. Cancellation is the token CliApp.RunAsync receives, fired by the fakes at a
/// precise point (device start, a file being probed), so nothing depends on timing.
/// </summary>
public sealed class CliCancellationTests : IAsyncDisposable
{
    private readonly List<CliHarness> _harnesses = [];
    private readonly CancellationTokenSource _ctrlC = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _harnesses)
        {
            await h.DisposeAsync();
        }

        _ctrlC.Dispose();
    }

    private CliHarness Harness(CliHarness h)
    {
        _harnesses.Add(h);
        return h;
    }

    private static JsonElement Error(CliResult result, int exitCode, string code)
    {
        Assert.True(result.ExitCode == exitCode, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(code, json.GetProperty("error").GetProperty("code").GetString());
        return json;
    }

    [Fact]
    public async Task Ctrl_c_during_foreground_playback_is_exit_130()
    {
        var cli = Harness(new CliHarness { Outputs = _ => new CancelOnStart(_ctrlC) });

        var result = await cli.RunAsync(_ctrlC.Token, "play", CliHarness.Song, "--foreground", "--backend", "null", "--json");

        var json = Error(result, ExitCodes.Cancelled, "cancelled");
        Assert.Equal("play", json.GetProperty("command").GetString());
        Assert.Equal("Playback cancelled.", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Ctrl_c_in_text_mode_reports_on_stderr()
    {
        var cli = Harness(new CliHarness { Outputs = _ => new CancelOnStart(_ctrlC) });

        var result = await cli.RunAsync(_ctrlC.Token, "play", CliHarness.Song, "--foreground", "--backend", "null", "--start", "0.05");

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.StartsWith("Playing Fake Artist - Fake Song", result.Out, StringComparison.Ordinal);
        Assert.Contains(" via ", result.Out, StringComparison.Ordinal);
        Assert.Equal("music-spin play: Playback cancelled.", result.Err.Trim());
    }

    [Fact]
    public async Task Foreground_playback_to_the_end_in_text_mode_says_how_long_it_played()
    {
        var cli = Harness(new CliHarness());

        var result = await cli.RunAsync("play", CliHarness.Song, "--foreground", "--backend", "null");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Playing Fake Artist - Fake Song", "Stopped (ended) after 0.2s."], [result.Lines()[0], result.Lines()[^1]]);
    }

    [Fact]
    public async Task Ctrl_c_during_a_scan_keeps_the_work_done_and_is_exit_130()
    {
        var cli = Harness(new CliHarness { DecoderOverride = new ProbeHook(() => _ctrlC.Cancel()) });
        var music = Directory.CreateDirectory(Path.Combine(cli.BaseDirectory, "music")).FullName;
        LibraryTestData.Write(Path.Combine(music, "a.flac"), LibraryTestData.Tiny16, "A", "Artist");
        await File.WriteAllTextAsync(Path.Combine(music, "broken.flac"), "not audio", TestContext.Current.CancellationToken); // TagLib fails -> probe -> Ctrl+C
        Assert.Equal(0, (await cli.RunAsync("library", "add", music, "--json")).ExitCode);

        var result = await cli.RunAsync(_ctrlC.Token, "library", "scan", "--parallel", "1", "--json", "--quiet");

        var json = Error(result, ExitCodes.Cancelled, "cancelled");
        Assert.Equal("library scan", json.GetProperty("command").GetString());
        Assert.Equal("Scan cancelled; files processed so far were saved.", json.GetProperty("error").GetProperty("message").GetString());
        var data = json.GetProperty("data");
        Assert.True(data.GetProperty("cancelled").GetBoolean());
        Assert.Equal(130, json.GetProperty("error").GetProperty("exitCode").GetInt32());
        Assert.Empty(result.Err);
    }

    [Fact]
    public async Task Text_scan_progress_names_each_phase_on_stderr()
    {
        var cli = Harness(new CliHarness());
        var music = Directory.CreateDirectory(Path.Combine(cli.BaseDirectory, "music")).FullName;
        LibraryTestData.Write(Path.Combine(music, "a.flac"), LibraryTestData.Tiny16, "A", "Artist");
        await cli.RunAsync("library", "add", music);

        var result = await cli.RunAsync("library", "scan");

        Assert.Equal(0, result.ExitCode);
        var progress = result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(progress, l => l.StartsWith("scan: found ", StringComparison.Ordinal));
        Assert.Contains("scan: indexing albums and artists...", progress);
        Assert.Equal("scan: done", progress[^1]);
        Assert.StartsWith("Scanned 1 folder(s): 1 audio file(s), 1 added", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_exception_is_exit_1_with_its_type_and_no_stack_trace()
    {
        var cli = Harness(new CliHarness { DecoderOverride = new ProbeHook(() => throw new InvalidOperationException("decoder bug")) });

        var json = Error(await cli.RunAsync("info", CliHarness.Song, "--json"), ExitCodes.Error, "error");
        Assert.Equal("InvalidOperationException: decoder bug", json.GetProperty("error").GetProperty("message").GetString());

        var text = await cli.RunAsync("info", CliHarness.Song);
        Assert.Equal(ExitCodes.Error, text.ExitCode);
        Assert.Equal("music-spin info: InvalidOperationException: decoder bug", text.Err.Trim());
        Assert.DoesNotContain(" at ", text.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_surfacing_from_a_command_is_exit_130()
    {
        var cli = Harness(new CliHarness { DecoderOverride = new ProbeHook(() => throw new OperationCanceledException()) });

        var json = Error(await cli.RunAsync("info", CliHarness.Song, "--json"), ExitCodes.Cancelled, "cancelled");

        Assert.Equal("Cancelled.", json.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>Starting the device is the user pressing Ctrl+C; the device never finishes on its own.</summary>
    private sealed class CancelOnStart(CancellationTokenSource ctrlC) : IAudioOutputProvider, IAudioOutput
    {
        public string Name => "cancel-on-start";

        public bool IsOpen { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

        public IAudioOutput Create(AudioOutputOptions options) => this;

        public void Open(IPcmSource source) => IsOpen = true;

        public void Start() => ctrlC.Cancel();

        public void Pause()
        {
        }

        public void Stop() => IsOpen = false;

        public void Dispose() => Stop();
    }

    /// <summary>Fake decoders that run <paramref name="onProbe"/> whenever a file is probed.</summary>
    private sealed class ProbeHook(Action onProbe) : IAudioDecoderFactory
    {
        private readonly FakeDecoderFactory _inner = new FakeDecoderFactory().With(TestAudio.Track(CliHarness.Song, sampleRate: 96_000, seconds: 0.2));

        public string Name => "hook";

        public bool IsAvailable => true;

        public TrackInfo Probe(string path)
        {
            onProbe();
            return _inner.Probe(path);
        }

        public IAudioDecoder Open(string path) => _inner.Open(path);

        public IReadOnlyList<AudioFormatSupport> GetSupportedFormats() => _inner.GetSupportedFormats();
    }
}
