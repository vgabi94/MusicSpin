using MusicSpin.Core;
using MusicSpin.Core.Decoding.FFmpeg;
using static MusicSpin.Core.Decoding.FFmpeg.FFmpegRuntime;

namespace MusicSpin.Tests;

/// <summary>
/// How FFmpeg is located and how load failures are reported. The native load itself happens once per process, so the
/// failure outcomes are driven through <see cref="FFmpegRuntime.Evaluate"/> with a simulated loader.
/// </summary>
public class FFmpegRuntimeTests
{
    private static readonly Dictionary<string, int> Versions = new() { ["avcodec"] = 62, ["avformat"] = 62, ["avutil"] = 60, ["swresample"] = 6 };

    [Theory]
    [InlineData(0, "libavcodec.so.62, libavformat.so.62, libavutil.so.60, libswresample.so.6")]
    [InlineData(1, "avcodec-62.dll, avformat-62.dll, avutil-60.dll, swresample-6.dll")]
    [InlineData(2, "libavcodec.62.dylib, libavformat.62.dylib, libavutil.60.dylib, libswresample.6.dylib")]
    public void Required_library_file_names_follow_each_os_convention(int os, string expected)
        => Assert.Equal(expected, RequiredLibraryFiles(Versions, (Os)os));

    [Fact]
    public void The_environment_variable_comes_first_then_the_app_folder_then_the_user_install()
    {
        var app = Path.Combine("app");
        Assert.Equal(["/custom", Path.Combine(app, "ffmpeg"), "/user"], CandidateDirectories("/custom", app, "/user", macOS: false));
        Assert.Equal([Path.Combine(app, "ffmpeg"), "/user"], CandidateDirectories("  ", app, "/user", macOS: false));
    }

    [Fact]
    public void On_macos_homebrew_prefixes_are_searched_last()
    {
        var dirs = CandidateDirectories(null, "app", "/user", macOS: true);

        Assert.Equal(["/opt/homebrew/opt/ffmpeg/lib", "/usr/local/opt/ffmpeg/lib", "/opt/homebrew/lib", "/usr/local/lib"], dirs.Skip(2));
    }

    [Fact]
    public void The_first_candidate_holding_libavcodec_wins_including_lib_and_bin_subfolders()
    {
        var present = new HashSet<string> { Path.Combine("b", "bin", "avcodec-62.dll"), Path.Combine("c", "avcodec-62.dll") };

        Assert.Equal(Path.Combine("b", "bin"), ResolveLibraryPath(["a", "b", "c"], "avcodec-62.dll", present.Contains));
        Assert.Equal("c", ResolveLibraryPath(["a", "c", "b"], "avcodec-62.dll", present.Contains));
        Assert.Equal(Path.Combine("d", "lib"), ResolveLibraryPath(["d"], "x", p => p == Path.Combine("d", "lib", "x")));
        Assert.Equal(string.Empty, ResolveLibraryPath(["a"], "avcodec-62.dll", present.Contains)); // OS default search
    }

    [Fact]
    public void A_matching_build_is_available_and_reports_its_license()
    {
        var status = Evaluate(() => new LoadedLibraries("9.0.1", "LGPL version 2.1 or later", 62), "/ff", Versions, 62, "libs");

        Assert.True(status.Available);
        Assert.Null(status.Error);
        Assert.Equal(("9.0.1", "/ff"), (status.Version, status.LibraryPath));
        Assert.True(status.IsLgpl);
        Assert.Same(Versions, status.ExpectedLibraryVersions);
        EnsureAvailable(status); // does not throw
    }

    [Fact]
    public void A_gpl_build_works_but_is_not_lgpl()
    {
        var status = Evaluate(() => new LoadedLibraries("9.0", "GPL version 2 or later", 62), "", Versions, 62, "libs");

        Assert.True(status.Available);
        Assert.False(status.IsLgpl);
        Assert.False((status with { License = null }).IsLgpl);
    }

    [Fact]
    public void A_different_libavcodec_major_is_reported_as_an_abi_mismatch()
    {
        var status = Evaluate(() => new LoadedLibraries("7.1", "LGPL", 61), "/ff", Versions, 62, "libs");

        Assert.False(status.Available);
        Assert.Equal("7.1", status.Version);
        Assert.Equal($"FFmpeg 7.1 has libavcodec 61, but MusicSpin needs libavcodec 62 (FFmpeg {ExpectedFFmpegVersion}).", status.Error);
        var ex = Assert.Throws<MusicSpinException>(() => EnsureAvailable(status));
        Assert.Equal(MusicSpinErrorCode.DecoderUnavailable, ex.Code);
        Assert.Equal(status.Error, ex.Message);
    }

    public static TheoryData<Exception> LoaderFailures() => new()
    {
        new DllNotFoundException("libavcodec.so.62: cannot open shared object file"),
        new EntryPointNotFoundException("swresample_version"),
        new BadImageFormatException("wrong architecture"),
        new NotSupportedException("platform"),
    };

    [Theory]
    [MemberData(nameof(LoaderFailures))]
    public void Missing_or_broken_libraries_explain_where_was_searched_and_how_to_fix_it(Exception failure)
    {
        var inFolder = Evaluate(() => throw failure, "/opt/ff", Versions, 62, "libavcodec.so.62, libavutil.so.60");
        var onSystem = Evaluate(() => throw failure, "", Versions, 62, "libavcodec.so.62, libavutil.so.60");

        Assert.False(inFolder.Available);
        Assert.Null(inFolder.Version);
        Assert.Equal(
            $"FFmpeg {ExpectedFFmpegVersion} shared libraries (libavcodec.so.62, libavutil.so.60) were not found in '/opt/ff'. " +
            $"Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg {ExpectedFFmpegVersion}, or set {PathEnvironmentVariable}.",
            inFolder.Error);
        Assert.Contains("were not found in the system library path.", onSystem.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_exceptions_while_loading_are_not_mistaken_for_a_missing_install()
        => Assert.Throws<InvalidOperationException>(() => Evaluate(() => throw new InvalidOperationException("bug"), "", Versions, 62, "libs"));

    [Fact]
    public void An_unavailable_status_without_a_reason_still_throws_a_clear_error()
    {
        var ex = Assert.Throws<MusicSpinException>(() => EnsureAvailable(new FFmpegStatus(false, null, null, "", Versions, null)));

        Assert.Equal("FFmpeg is not available.", ex.Message);
    }

    [Fact]
    public void The_process_wide_status_lists_the_libraries_for_this_os()
    {
        Assert.Contains("avcodec", RequiredLibraries, StringComparison.Ordinal);
        Assert.Contains("swresample", RequiredLibraries, StringComparison.Ordinal);
        Assert.Equal(Status.Available, IsAvailable);
        Assert.Contains(UserInstallDirectory, FFmpegRuntime.CandidateDirectories());
    }
}
