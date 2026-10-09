using System.CommandLine;
using MusicSpin.Cli.Output;

namespace MusicSpin.Cli.Commands;

internal static class InfoCommand
{
    public static Command Create(CommandRunner runner)
    {
        var file = new Argument<string>("file") { Description = "Audio file to inspect." };
        var command = new Command("info", "Show codec, sample rate, bit depth, channels, duration and tags of a file.") { file };

        runner.SetAction(command, (parseResult, json, _) =>
        {
            var track = runner.Env.Decoders.Probe(parseResult.GetValue(file)!);
            var output = runner.Env.Out;
            if (json)
            {
                JsonOutput.WriteSuccess(output, "info", TrackDto.From(track));
                return Task.FromResult(ExitCodes.Success);
            }

            output.WriteLine(track.Path);
            output.WriteLine($"  {Human.Describe(track)}");
            output.WriteLine($"  container: {track.Container}, codec: {track.Codec} ({track.CodecLongName}), sample format: {track.SampleFormat}");
            if (track.BitRate is { } bitRate)
            {
                output.WriteLine($"  bit rate: {bitRate / 1000} kbps");
            }

            foreach (var (key, value) in track.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"  {key}: {value}");
            }

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }
}
