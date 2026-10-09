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

    /// <summary>Pretend executable directory (portable data goes to &lt;BaseDirectory&gt;/.usrdata). Unique per harness.</summary>
    public string BaseDirectory { get; init; } = TempDir.Create("exe");

    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    public string DataDir => Path.Combine(BaseDirectory, ".usrdata");

    /// <summary>Stdout of the command currently running (thread-safe snapshot via <see cref="LockedStringWriter.Snapshot"/>).</summary>
    public LockedStringWriter? LiveOut { get; private set; }

    public Task<CliResult> RunAsync(params string[] args) => RunAsync(TestContext.Current.CancellationToken, args);

    public async Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new LockedStringWriter();
        using var stderr = new LockedStringWriter();
        LiveOut = stdout;
        var env = new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            In = new StringReader(Input),
            Decoders = Decoders,
            OutputProviders = Outputs,
            FFmpegStatus = () => FFmpeg,
            BaseDirectory = BaseDirectory,
            GetEnvironmentVariable = name => EnvironmentVariables.GetValueOrDefault(name),
            LibraryLockTimeout = LockTimeout,
        };

        var exitCode = await CliApp.RunAsync(args, env, cancellationToken);
        return new CliResult(exitCode, stdout.Snapshot(), stderr.Snapshot());
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

/// <summary>StringWriter safe for concurrent writes (watch events) and reads (test polling).</summary>
internal sealed class LockedStringWriter : StringWriter
{
    private readonly Lock _gate = new();

    public override void Write(char value)
    {
        lock (_gate)
        {
            base.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            base.Write(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            base.Write(buffer, index, count);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            base.WriteLine(value);
        }
    }

    public string Snapshot()
    {
        lock (_gate)
        {
            return ToString();
        }
    }
}
