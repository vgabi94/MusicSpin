using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Diagnostics;

/// <summary>
/// On-demand timings of an optimized, instrumented Release build. Not part of CI: set MUSICSPIN_PERF=1.
/// Numbers are reported, not asserted. They are operation time and managed allocations, not paint or native memory.
/// </summary>
[Trait("Category", "Perf")]
public sealed class ScanPerfTests : LibraryTestBase
{
    private const int Files = 48;
    private const int Repetitions = 3;

    public ScanPerfTests(ITestOutputHelper output) => Output = output;

    private ITestOutputHelper Output { get; }

    [Fact]
    public void Cold_and_warm_scans_report_phase_times()
    {
        RequireInstrumentedRelease();
        for (var i = 0; i < Files; i++)
        {
            LibraryTestData.Write(Path.Combine(Music, "album", $"{i:00}.flac"), LibraryTestData.Tiny16, $"T{i}", "Artist", "Album", track: (uint)i + 1);
        }

        using var library = Open();
        library.AddFolder(Music);
        Output.WriteLine(Describe());
        Output.WriteLine("cold includes JIT of the scan path. warm is an unchanged rescan. bytes are managed allocations from GC.GetTotalAllocatedBytes, including marker recording.");

        using var capture = MarkerCapture.Start();
        Report("cold", Measure(() => library.Scan(cancellationToken: Ct)));
        for (var i = 0; i < Repetitions; i++)
        {
            Report($"warm-{i + 1}", Measure(() => library.Scan(cancellationToken: Ct)));
        }

        Touch(Path.Combine(Music, "album", "00.flac"));
        Report("incremental-one-file", Measure(() => library.Scan(cancellationToken: Ct)));

#if PERF_MARKERS
        Assert.Equal(2 + Repetitions, capture.Named("library.scan").Count);
        foreach (var name in new[] { "library.scan", "library.scan.walk", "library.scan.classify", "library.scan.read", "library.scan.index", "library.scan.checkpoint", "library.scan.prune" })
        {
            var times = string.Join(",", capture.Named(name).Select(mark => mark.Duration.TotalMicroseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            Output.WriteLine($"span {name} durationUs={times}");
        }
#endif
    }

    private static void RequireInstrumentedRelease()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("MUSICSPIN_PERF"), "1", StringComparison.Ordinal),
            "Set MUSICSPIN_PERF=1 to run on-demand perf tests.");
#if !PERF_MARKERS
        Assert.Skip("Markers are compiled out. Use -c Release -p:EnablePerfMarkers=true.");
#else
        var debuggable = typeof(MusicLibrary).Assembly.GetCustomAttributes<DebuggableAttribute>().FirstOrDefault();
        Assert.SkipUnless(debuggable is { IsJITOptimizerDisabled: false },
            "This assembly was not optimized. Perf numbers are for -c Release -p:EnablePerfMarkers=true.");
#endif
    }

    private static string Describe()
        => $"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.RuntimeIdentifier}; cores={Environment.ProcessorCount}; markers=on";

    private static Sample Measure(Func<ScanResult> scan)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = scan();
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        return new Sample(clock.Elapsed.TotalMilliseconds, allocated, result);
    }

    private void Report(string label, Sample sample)
        => Output.WriteLine(
            $"{label}: {sample.Milliseconds:0.###} ms, managedBytes={sample.Allocated}, tracks={sample.Result.Tracks}, unchanged={sample.Result.Unchanged}, added={sample.Result.Added}, updated={sample.Result.Updated}");

    private readonly record struct Sample(double Milliseconds, long Allocated, ScanResult Result);
}
