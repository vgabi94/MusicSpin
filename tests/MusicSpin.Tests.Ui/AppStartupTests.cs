using MusicSpin.App;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// <see cref="AppHost"/> startup: the settings it loads and the warning it hands to the banner, for a first run, a
/// corrupt settings file, a data folder that can't be written, and both at once.
/// </summary>
public sealed class AppStartupTests : IDisposable
{
    private readonly string _root = TempDir.Create("start");

    private DataDirectory Data => new(Path.Combine(_root, "data"), DataDirectorySource.Override);

    public void Dispose() => TempDir.TryDelete(_root);

    [Fact]
    public void A_first_run_creates_the_folder_and_saves_the_defaults_without_a_warning()
    {
        var (settings, warning) = AppHost.Start(Data);

        Assert.Null(warning);
        Assert.Equal(new AppSettings(), settings);
        Assert.True(File.Exists(Data.SettingsFile));
        Assert.Equal(new AppSettings(), new SettingsStore(Data).Load());
    }

    [Fact]
    public void Saved_settings_are_loaded_and_left_as_they_are()
    {
        new SettingsStore(Data).Save(new AppSettings { Volume = 0.25, Theme = "light" });
        var before = File.ReadAllText(Data.SettingsFile);

        var (settings, warning) = AppHost.Start(Data);

        Assert.Null(warning);
        Assert.Equal((0.25, "light"), (settings.Volume, settings.Theme));
        Assert.Equal(before, File.ReadAllText(Data.SettingsFile));
    }

    [Trait("Category", "Unhappy")]
    [Fact]
    public void A_corrupt_settings_file_warns_and_is_not_overwritten_with_the_defaults()
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllText(Data.SettingsFile, "{ not json");

        var (settings, warning) = AppHost.Start(Data);

        Assert.Equal(new AppSettings(), settings);
        Assert.NotNull(warning);
        Assert.Contains(Data.SettingsFile, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("not writable", warning, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(Data.SettingsFile)); // left for the user to inspect
    }

    [Trait("Category", "Unhappy")]
    [Fact]
    public void A_data_folder_that_cannot_be_written_warns_and_keeps_the_defaults()
    {
        RequirePosixNonRoot();
        Directory.CreateDirectory(Data.Path);
        MakeReadOnly(Data.Path);

        var (settings, warning) = AppHost.Start(Data);

        Assert.Equal(new AppSettings(), settings);
        Assert.NotNull(warning);
        Assert.Contains("not writable", warning, StringComparison.Ordinal);
        Assert.False(File.Exists(Data.SettingsFile));
    }

    [Trait("Category", "Unhappy")]
    [Fact]
    public void A_usrdata_file_in_place_of_the_portable_folder_warns_how_to_move_the_data()
    {
        var exe = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        File.WriteAllText(Path.Combine(exe, AppPaths.PortableDataFolderName), "oops");
        var portable = AppPaths.Resolve(null, _ => null, exe);

        var (settings, warning) = AppHost.Start(portable);

        Assert.Equal(new AppSettings(), settings);
        Assert.NotNull(warning);
        Assert.Contains(AppPaths.DataDirEnvironmentVariable, warning, StringComparison.Ordinal);
        Assert.Equal("oops", File.ReadAllText(Path.Combine(exe, AppPaths.PortableDataFolderName)));
    }

    [Trait("Category", "Unhappy")]
    [Fact]
    public void A_corrupt_settings_file_in_a_folder_that_cannot_be_written_reports_both_problems()
    {
        RequirePosixNonRoot();
        Directory.CreateDirectory(Data.Path);
        File.WriteAllText(Data.SettingsFile, "[1,2,3]");
        MakeReadOnly(Data.Path);

        var (settings, warning) = AppHost.Start(Data);

        Assert.Equal(new AppSettings(), settings);
        Assert.NotNull(warning);
        var lines = warning.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Ignoring unreadable settings file", lines[0], StringComparison.Ordinal);
        Assert.Contains(Data.SettingsFile, lines[0], StringComparison.Ordinal);
        Assert.Contains("not writable", lines[1], StringComparison.Ordinal);
    }

    private static void MakeReadOnly(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void RequirePosixNonRoot()
        => Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs POSIX permissions and a non-root user");
}
