using System.CommandLine;
using MusicSpin.Cli.Output;
using MusicSpin.Core.Decoding.FFmpeg;

namespace MusicSpin.Cli.Commands;

internal static class FormatsCommand
{
    public static Command Create(CommandRunner runner)
    {
        var all = new Option<bool>("--all") { Description = "Also list every audio decoder in the loaded FFmpeg build." };
        var command = new Command("formats", "List music formats and whether they can be decoded on this machine.") { all };

        runner.SetAction(command, (parseResult, json, _) =>
        {
            var env = runner.Env;
            var formats = env.Decoders.GetSupportedFormats();
            var decoder = DecoderDto.From(env.FFmpegStatus());
            CodecDto[]? allDecoders = parseResult.GetValue(all) && env.Decoders.IsAvailable
                ? FFmpegDecoderFactory.GetAllAudioDecoders().Select(c => new CodecDto(c.Name, c.LongName)).ToArray()
                : null;

            if (json)
            {
                JsonOutput.WriteSuccess(env.Out, "formats", new FormatsDto(decoder, formats, allDecoders));
                return Task.FromResult(ExitCodes.Success);
            }

            env.Out.WriteLine(decoder.Available
                ? $"Decoder: FFmpeg {decoder.Version} ({decoder.License})"
                : $"Decoder: FFmpeg NOT available - {decoder.Error}");
            foreach (var f in formats)
            {
                var flags = string.Join(", ", new[] { f.Lossless ? "lossless" : "lossy", f.HiResCapable ? "hi-res" : null }.OfType<string>());
                env.Out.WriteLine($"  [{(f.Supported ? "x" : " ")}] {f.Name,-24} .{string.Join(" .", f.Extensions),-18} {flags}");
            }

            if (allDecoders is not null)
            {
                env.Out.WriteLine($"All audio decoders ({allDecoders.Length}):");
                foreach (var c in allDecoders)
                {
                    env.Out.WriteLine($"  {c.Name,-20} {c.LongName}");
                }
            }

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }
}
