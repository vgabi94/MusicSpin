using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Radio;

/// <summary>The radio lookahead feeds the decoder's next-track logic: same-format picks play back to back, sample-exact.</summary>
public sealed class RadioGaplessTests : IDisposable
{
    private readonly RampDecoderFactory _decoders = new();
    private readonly ManualOutputProvider _outputs = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    [Fact]
    public void Radio_tracks_with_the_same_format_play_gaplessly_on_one_device_open()
    {
        var specs = Enumerable.Range(1, 6).Select(i => new RampSpec(i, 300 + (i * 70))).ToList();
        var pool = specs.Select(s =>
        {
            var path = $"/g/{s.Id}.flac";
            _decoders.Add(path, s);
            return new RadioCandidate($"id{s.Id}", path, $"artist{s.Id}", $"album{s.Id}");
        }).ToList();
        var player = new Player(_decoders, _outputs, new PlayerOptions());
        var queue = new QueuePlayer(player) { RadioPool = _ => pool, RadioRandom = () => new Random(3) };
        _owned.Add(player);
        _owned.Add(queue);

        queue.StartRadio(new RadioFilter());
        var order = queue.List(0, 8).Items.Select(e => specs[int.Parse(e.Item.TrackId![2..], System.Globalization.CultureInfo.InvariantCulture) - 1]).ToList();
        var expected = order.SelectMany(s => s.Pcm()).ToArray();

        var output = new List<float>();
        while (output.Count < expected.Length)
        {
            output.AddRange(_outputs.Output.Pump(Math.Min(64, expected.Length - output.Count)));
        }

        Assert.Equal(expected, output);
        Assert.Equal(1, _outputs.Output.OpenCount);
        Assert.Equal(true, queue.Status().Gapless);
    }
}
