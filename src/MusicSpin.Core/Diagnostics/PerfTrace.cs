#if PERF_MARKERS
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicSpin.Core.Diagnostics;

/// <summary>
/// The in-process trace <see cref="Perf.AttachTrace"/> writes: one JSON object per line, spans when they stop and a
/// sample of the <see cref="Perf.Meter"/> gauges. This is the capture path tests run. It does not write paths or titles;
/// tag values that are not integers are omitted.
/// </summary>
internal static class PerfTrace
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;
    private static MeterListener? _meters;

    public static string Resolve(string spec)
        => spec == "1"
            ? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "musicspin-trace.jsonl"))
            : Path.GetFullPath(spec);

    public static void Start(string path)
    {
        lock (Gate)
        {
            if (_writer is not null)
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _writer = new StreamWriter(new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

                WriteLocked(Meta());
                ActivitySource.AddActivityListener(new ActivityListener
            {
                ShouldListenTo = static source => source.Name == Perf.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = WriteSpan,
            });
            var meters = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == Perf.SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meters.SetMeasurementEventCallback<long>(static (instrument, value, _, _) => WriteMetric(instrument.Name, value));
            meters.SetMeasurementEventCallback<int>(static (instrument, value, _, _) => WriteMetric(instrument.Name, value));
            meters.Start();
            _meters = meters;
        }
    }

    private static string Meta()
        => "{\"kind\":\"meta\",\"framework\":" + Quote(RuntimeInformation.FrameworkDescription)
            + ",\"rid\":" + Quote(RuntimeInformation.RuntimeIdentifier)
            + ",\"arch\":" + Quote(RuntimeInformation.OSArchitecture.ToString())
            + ",\"cores\":" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
            + ",\"markers\":1}";

    private static void WriteSpan(Activity activity)
    {
        // Gauges are sampled when a root span stops, not on every child, so a scan does not write a sample per phase.
        if (activity.ParentSpanId == default)
        {
            _meters?.RecordObservableInstruments();
        }
        var tags = new StringBuilder();
        var first = true;
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Value is not int and not long and not bool)
            {
                continue;
            }

            if (!first)
            {
                tags.Append(',');
            }

            first = false;
            var number = tag.Value is bool flag
                ? flag ? 1 : 0
                : Convert.ToInt64(tag.Value, CultureInfo.InvariantCulture);
            tags.Append(Quote(tag.Key)).Append(':').Append(number.ToString(CultureInfo.InvariantCulture));
        }

        var parent = activity.ParentSpanId == default ? "null" : Quote(activity.ParentSpanId.ToString());
        Write("{\"kind\":\"span\",\"name\":" + Quote(activity.OperationName)
            + ",\"trace\":" + Quote(activity.TraceId.ToString())
            + ",\"span\":" + Quote(activity.SpanId.ToString())
            + ",\"parent\":" + parent
            + ",\"durationUs\":" + activity.Duration.TotalMicroseconds.ToString("0.###", CultureInfo.InvariantCulture)
            + ",\"tags\":{" + tags + "}}");
    }

    private static void WriteMetric(string name, long value)
        => Write("{\"kind\":\"metric\",\"name\":" + Quote(name) + ",\"value\":" + value.ToString(CultureInfo.InvariantCulture) + "}");

    private static void Write(string line)
    {
        lock (Gate)
        {
            WriteLocked(line);
        }
    }

    private static void WriteLocked(string line) => _writer?.WriteLine(line);

    private static string Quote(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}
#endif
