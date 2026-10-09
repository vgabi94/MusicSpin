using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Decoding.FFmpeg;

namespace MusicSpin.Tests.Cli;

public class CliTests
{
    private readonly CliHarness _cli = new();

    [Fact]
    public async Task Help_lists_all_commands_and_exit_codes()
    {
        var result = await _cli.RunAsync("--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        foreach (var command in new[] { "info", "formats", "devices", "play", "serve", "doctor", "--json" })
        {
            Assert.Contains(command, result.Out, StringComparison.Ordinal);
        }

        Assert.Contains("Exit codes:", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_succeeds()
    {
        var result = await _cli.RunAsync("--version");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Out));
    }

    [Fact]
    public async Task Unknown_command_is_usage_error_in_text_mode()
    {
        var result = await _cli.RunAsync("bogus");
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("bogus", result.Err, StringComparison.Ordinal);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Usage_error_with_json_writes_json_error_to_stdout()
    {
        var result = await _cli.RunAsync("info", "--json"); // missing <file>
        var json = result.Json();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, json.GetProperty("error").GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Info_json_has_hires_metadata()
    {
        var result = await _cli.RunAsync("info", CliHarness.Song, "--json");
        var json = result.Json();
        var data = json.GetProperty("data");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("info", json.GetProperty("command").GetString());
        Assert.Equal("flac", data.GetProperty("codec").GetString());
        Assert.Equal(96_000, data.GetProperty("sampleRate").GetInt32());
        Assert.Equal(24, data.GetProperty("bitsPerSample").GetInt32());
        Assert.Equal(2, data.GetProperty("channels").GetInt32());
        Assert.True(data.GetProperty("hiRes").GetBoolean());
        Assert.Equal("Fake Song", data.GetProperty("title").GetString());
        Assert.Equal(0.2, data.GetProperty("durationSeconds").GetDouble());
    }

    [Fact]
    public async Task Info_text_is_human_readable()
    {
        var result = await _cli.RunAsync("info", CliHarness.Song);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("24-bit / 96 kHz", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_missing_file_exits_3_with_json_error()
    {
        var result = await _cli.RunAsync("info", "/nope.flac", "--json");
        var error = result.Json().GetProperty("error");

        Assert.Equal(ExitCodes.FileNotFound, result.ExitCode);
        Assert.Equal("file_not_found", error.GetProperty("code").GetString());
        Assert.Contains("/nope.flac", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_missing_file_text_goes_to_stderr()
    {
        var result = await _cli.RunAsync("info", "/nope.flac");
        Assert.Equal(ExitCodes.FileNotFound, result.ExitCode);
        Assert.Empty(result.Out);
        Assert.Contains("File not found", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decoder_unavailable_exits_6()
    {
        var cli = new CliHarness { Decoders = new Shared.FakeDecoderFactory { IsAvailable = false } };
        var result = await cli.RunAsync("info", "/x.flac", "--json");
        Assert.Equal(ExitCodes.DecoderUnavailable, result.ExitCode);
        Assert.Equal("decoder_unavailable", result.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Formats_json_lists_decoder_and_formats()
    {
        var result = await _cli.RunAsync("formats", "--json");
        var data = result.Json().GetProperty("data");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(data.GetProperty("decoder").GetProperty("available").GetBoolean());
        Assert.True(data.GetProperty("decoder").GetProperty("lgpl").GetBoolean());
        var flac = data.GetProperty("formats").EnumerateArray().Single(f => f.GetProperty("id").GetString() == "flac");
        Assert.True(flac.GetProperty("supported").GetBoolean());
        Assert.True(flac.GetProperty("hiResCapable").GetBoolean());
    }

    [Fact]
    public async Task Devices_json_with_null_backend()
    {
        var result = await _cli.RunAsync("devices", "--backend", "null", "--json");
        var data = result.Json().GetProperty("data");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("null", Assert.Single(data.GetProperty("devices").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Invalid_backend_is_usage_error()
    {
        var result = await _cli.RunAsync("devices", "--backend", "nope");
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Play_to_end_on_null_backend()
    {
        var result = await _cli.RunAsync("play", CliHarness.Song, "--backend", "null", "--json");
        var data = result.Json().GetProperty("data");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("ended", data.GetProperty("stopReason").GetString());
        Assert.Equal(0.2, data.GetProperty("playedSeconds").GetDouble(), 3);
    }

    [Fact]
    public async Task Play_rejects_out_of_range_volume()
    {
        var result = await _cli.RunAsync("play", CliHarness.Song, "--volume", "1.5", "--json");
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Doctor_reports_missing_ffmpeg_with_exit_6_and_data()
    {
        var cli = new CliHarness
        {
            FFmpeg = new FFmpegStatus(false, null, null, "", new Dictionary<string, int>(), "not found"),
        };

        var result = await cli.RunAsync("doctor", "--backend", "null", "--json");
        var json = result.Json();

        Assert.Equal(ExitCodes.DecoderUnavailable, result.ExitCode);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("data").GetProperty("decoder").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Doctor_healthy_exits_0()
    {
        var result = await _cli.RunAsync("doctor", "--backend", "null", "--json");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(result.Json().GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Every_json_output_is_single_line_valid_json()
    {
        foreach (var args in new[]
                 {
                     new[] { "info", CliHarness.Song, "--json" },
                     ["formats", "--json"],
                     ["devices", "--backend", "null", "--json"],
                     ["info", "/missing", "--json"],
                 })
        {
            var result = await _cli.RunAsync(args);
            var line = Assert.Single(result.Lines());
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("ok", out _));
        }
    }

    [Theory]
    [InlineData(Core.MusicSpinErrorCode.FileNotFound, 3)]
    [InlineData(Core.MusicSpinErrorCode.UnsupportedFormat, 4)]
    [InlineData(Core.MusicSpinErrorCode.DecodeFailed, 5)]
    [InlineData(Core.MusicSpinErrorCode.DecoderUnavailable, 6)]
    [InlineData(Core.MusicSpinErrorCode.AudioDeviceError, 7)]
    [InlineData(Core.MusicSpinErrorCode.InvalidState, 8)]
    [InlineData(Core.MusicSpinErrorCode.Unknown, 1)]
    public void Error_codes_map_to_documented_exit_codes(Core.MusicSpinErrorCode code, int exitCode)
        => Assert.Equal(exitCode, ExitCodes.From(code));

    [Fact]
    public async Task Real_ffmpeg_info_on_generated_wav()
    {
        Assert.SkipUnless(FFmpegRuntime.IsAvailable, $"FFmpeg {FFmpegRuntime.ExpectedFFmpegVersion} not installed");
        var path = Path.Combine(Path.GetTempPath(), $"musicspin-cli-{Guid.NewGuid():N}.wav");
        Shared.TestAudio.WriteWav24(path, 192_000, 2, 19_200);
        try
        {
            var env = new CliEnvironment
            {
                Out = new StringWriter(),
                Error = new StringWriter(),
                In = TextReader.Null,
                Decoders = new FFmpegDecoderFactory(),
                OutputProviders = _ => new Core.Output.NullAudioOutputProvider(false),
            };

            var exit = await CliApp.RunAsync(["info", path, "--json"], env, TestContext.Current.CancellationToken);
            var data = JsonDocument.Parse(env.Out.ToString()!).RootElement.GetProperty("data");

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal("pcm_s24le", data.GetProperty("codec").GetString());
            Assert.Equal(192_000, data.GetProperty("sampleRate").GetInt32());
            Assert.Equal(24, data.GetProperty("bitsPerSample").GetInt32());
            Assert.True(data.GetProperty("hiRes").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
