using System.Diagnostics;

namespace MusicSpin.Tests.Shared;

/// <summary>Records <c>MusicSpin</c> activities stopped while it is alive. Safe when the product was built without markers: nothing is emitted.</summary>
internal sealed class MarkerCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly Lock _gate = new();
    private readonly List<Mark> _marks = [];

    private MarkerCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "MusicSpin",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnStopped,
        };
    }

    public static MarkerCapture Start()
    {
        var capture = new MarkerCapture();
        ActivitySource.AddActivityListener(capture._listener);
        return capture;
    }

    public IReadOnlyList<Mark> Marks
    {
        get
        {
            lock (_gate)
            {
                return [.. _marks];
            }
        }
    }

    public IReadOnlyList<Mark> Named(string name) => [.. Marks.Where(mark => mark.Name == name)];

    public void Dispose() => _listener.Dispose();

    private void OnStopped(Activity activity)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            tags[tag.Key] = tag.Value switch
            {
                null => "",
                bool flag => flag ? "1" : "0",
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                _ => tag.Value.ToString() ?? "",
            };
        }

        lock (_gate)
        {
            _marks.Add(new Mark(activity.OperationName, activity.Parent?.OperationName, activity.Duration, tags));
        }
    }

    internal readonly record struct Mark(string Name, string? Parent, TimeSpan Duration, Dictionary<string, string> Tags);
}
