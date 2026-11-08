using System.Diagnostics;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Diagnostics;

/// <summary>
/// Marker spans for a scan: phases, cancellation, a fatal read, and that paths and titles stay out of the tags.
/// With markers compiled out, the same actions emit nothing.
/// </summary>
public sealed class ScanMarkerTests : LibraryTestBase
{
    private const string SecretDirectory = "SECRETPATHTOKEN";
    private const string SecretTitle = "SECRETTITLETOKEN";

    [Fact]
    public void Core_assembly_matches_the_marker_compile_flag()
    {
        var assembly = typeof(MusicLibrary).Assembly;
        var literals = new[]
        {
            "library.scan.walk", "library.scan.classify", "library.scan.read", "library.scan.moves",
            "library.scan.index", "library.scan.checkpoint", "library.scan.prune", "cover.file", "cover.resize",
            "MUSICSPIN_TRACE", "musicspin.scan.batches", "musicspin.gc.total_memory_bytes",
        };
#if PERF_MARKERS
        Assert.True(MarkerMetadata.HasType(assembly, "MusicSpin.Core.Diagnostics", "Perf"));
        Assert.All(literals, literal => Assert.True(MarkerMetadata.HasUserString(assembly, literal), literal));
#else
        Assert.False(MarkerMetadata.HasType(assembly, "MusicSpin.Core.Diagnostics", "Perf"));
        Assert.All(literals, literal => Assert.False(MarkerMetadata.HasUserString(assembly, literal), literal));
#endif
    }

    [Fact]
    public void Scan_records_phases_without_private_library_text()
    {
        WriteSecretTrack();
        using var capture = MarkerCapture.Start();

        var result = AddAndScan();

        Assert.Equal(1, result.Tracks);
        AssertNoSecrets(capture);
#if PERF_MARKERS
        var scan = Assert.Single(capture.Named("library.scan"));
        Assert.Equal("1", scan.Tags["files"]);
        Assert.Equal("0", scan.Tags["cancelled"]);
        Assert.Equal(scan.Name, capture.Named("library.scan.walk").Single().Parent);
        Assert.Equal(scan.Name, capture.Named("library.scan.classify").Single().Parent);
        Assert.Equal(scan.Name, capture.Named("library.scan.read").Single().Parent);
        Assert.Equal(scan.Name, capture.Named("library.scan.moves").Single().Parent);
        Assert.Equal(scan.Name, capture.Named("library.scan.index").Single().Parent);
        Assert.Equal(scan.Name, capture.Named("library.scan.checkpoint").Single().Parent);
        Assert.Equal(scan.Name, capture.Named("library.scan.prune").Single().Parent);
        Assert.Equal("1", capture.Named("library.scan.read").Single().Tags["toRead"]);
        Assert.Equal("0", capture.Named("library.scan.classify").Single().Tags["unchanged"]);
#else
        Assert.Empty(capture.Marks);
#endif
    }

    [Fact]
    public void Second_scan_of_an_unchanged_file_skips_the_index_rebuild()
    {
        WriteSecretTrack();
        AddAndScan();
        using var capture = MarkerCapture.Start();

        var result = Rescan();

        Assert.Equal(1, result.Unchanged);
        AssertNoSecrets(capture);
#if PERF_MARKERS
        Assert.Equal("1", Assert.Single(capture.Named("library.scan.index")).Tags["skipped"]);
        Assert.Equal("0", Assert.Single(capture.Named("library.scan.read")).Tags["toRead"]);
#endif
    }

    [Fact]
    public void A_cancelled_scan_is_tagged_cancelled()
    {
        WriteSecretTrack();
        using var capture = MarkerCapture.Start();
        using var library = Open();
        library.AddFolder(Music);

        var result = library.Scan(cancellationToken: new CancellationToken(canceled: true));

        Assert.True(result.Cancelled);
        AssertNoSecrets(capture);
#if PERF_MARKERS
        Assert.Equal("1", Assert.Single(capture.Named("library.scan")).Tags["cancelled"]);
#else
        Assert.Empty(capture.Marks);
#endif
    }

    [Fact]
    public void A_fatal_read_still_closes_the_scan_span()
    {
        WriteSecretTrack();
        using var capture = MarkerCapture.Start();
        using var library = Open(new HookReader(_ => throw new MusicSpin.Core.MusicSpinException(MusicSpin.Core.MusicSpinErrorCode.Unknown, "TagLibSharp.dll is missing")));
        library.AddFolder(Music);

        Assert.Throws<MusicSpin.Core.MusicSpinException>(() => library.Scan(new ScanOptions { MaxParallelism = 2 }, Ct));
        AssertNoSecrets(capture);
#if PERF_MARKERS
        Assert.Single(capture.Named("library.scan"));
#else
        Assert.Empty(capture.Marks);
#endif
    }

    [Fact]
    public void Trace_file_records_the_scan_and_a_managed_heap_sample()
    {
        var path = Path.Combine(TempDir.Create("trace"), "musicspin-trace.jsonl");
        WriteSecretTrack();
#if PERF_MARKERS
        MusicSpin.Core.Diagnostics.Perf.AttachTrace(path);
#endif
        using var capture = MarkerCapture.Start();
        AddAndScan();

#if PERF_MARKERS
        var text = File.ReadAllText(path);
        Assert.Contains("\"kind\":\"meta\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"library.scan\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"library.scan.walk\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"musicspin.gc.total_memory_bytes\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDirectory, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretTitle, text, StringComparison.Ordinal);
#else
        Assert.False(File.Exists(path));
        Assert.Empty(capture.Marks);
#endif
    }

    private void WriteSecretTrack()
        => LibraryTestData.Write(Path.Combine(Music, SecretDirectory, "track.flac"), LibraryTestData.Tiny16, SecretTitle, "A", "Album");

    private static void AssertNoSecrets(MarkerCapture capture)
    {
        foreach (var mark in capture.Marks)
        {
            Assert.DoesNotContain(SecretDirectory, mark.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretTitle, mark.Name, StringComparison.Ordinal);
            foreach (var (key, value) in mark.Tags)
            {
                Assert.DoesNotContain(SecretDirectory, key, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretTitle, key, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretDirectory, value, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretTitle, value, StringComparison.Ordinal);
            }
        }
    }
}
