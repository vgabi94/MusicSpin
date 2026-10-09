using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Decoding.FFmpeg;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Runs the CLI in-process with fakes and captures output.</summary>
internal sealed class CliHarness
{
    public const string Song = "/music/song.flac";

    public FakeDecoderFactory Decoders { get; init; } = new FakeDecoderFactory().With(TestAudio.Track(Song, sampleRate: 96_000, seconds: 0.2));

    public Func<string, IAudioOutputProvider> Outputs { get; init; } = _ => new NullAudioOutputProvider(realtime: false);

    public FFmpegStatus? FFmpeg { get; init; } = new(true, "9.0-test", "LGPL version 2.1 or later", "", new Dictionary<string, int>(), null);

    public string Input { get; init; } = string.Empty;

    public async Task<CliResult> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var env = new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            In = new StringReader(Input),
            Decoders = Decoders,
            OutputProviders = Outputs,
            FFmpegStatus = () => FFmpeg,
        };

        var exitCode = await CliApp.RunAsync(args, env, TestContext.Current.CancellationToken);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }
}

internal sealed record CliResult(int ExitCode, string Out, string Err)
{
    /// <summary>Parses stdout as exactly one JSON document.</summary>
    public JsonElement Json()
    {
        var lines = Lines();
        Assert.True(lines.Length == 1, $"Expected one JSON line, got {lines.Length}:\n{Out}");
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    public string[] Lines() => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public JsonElement[] JsonLines() => [.. Lines().Select(l => JsonDocument.Parse(l).RootElement.Clone())];
}
