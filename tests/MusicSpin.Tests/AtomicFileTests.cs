using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>
/// AtomicFile's link(2)-or-move decision, with link(2) simulated so the fallbacks (file systems without hard links,
/// no libc) are exercised on every OS; plus data-folder errors around settings files.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private const int EEXIST = 17;
    private const int EPERM = 1; // what FAT/exFAT return for link(2)

    private readonly string _dir = TempDir.Create("atomic");

    public void Dispose() => TempDir.TryDelete(_dir);

    private string Temp(string content = "new")
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, content);
        return path;
    }

    private string Destination => Path.Combine(_dir, "settings.json");

    [Fact]
    public void A_successful_link_publishes_and_removes_the_temp_file()
    {
        var temp = Temp();
        var calls = new List<(string, string)>();

        var published = AtomicFile.TryPublish(temp, Destination, (from, to) =>
        {
            calls.Add((from, to));
            File.Copy(from, to); // what a hard link looks like to readers
            return 0;
        });

        Assert.True(published);
        Assert.Equal([(temp, Destination)], calls);
        Assert.Equal("new", File.ReadAllText(Destination));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Eexist_from_link_loses_the_race_and_leaves_both_files_alone()
    {
        File.WriteAllText(Destination, "winner");
        var temp = Temp();

        Assert.False(AtomicFile.TryPublish(temp, Destination, (_, _) => EEXIST));

        Assert.Equal("winner", File.ReadAllText(Destination));
        Assert.True(File.Exists(temp)); // the caller deletes it
    }

    [Fact]
    public void Without_hard_link_support_the_move_fallback_publishes_when_the_name_is_free()
    {
        var temp = Temp();

        Assert.True(AtomicFile.TryPublish(temp, Destination, (_, _) => EPERM));

        Assert.Equal("new", File.ReadAllText(Destination));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Without_hard_link_support_an_existing_file_is_never_overwritten()
    {
        File.WriteAllText(Destination, "winner");
        var temp = Temp();

        Assert.False(AtomicFile.TryPublish(temp, Destination, (_, _) => EPERM));

        Assert.Equal("winner", File.ReadAllText(Destination));
        Assert.True(File.Exists(temp));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_libc_link_the_move_is_used(bool entryPointMissing)
    {
        var temp = Temp();
        Exception missing = entryPointMissing ? new EntryPointNotFoundException("link") : new DllNotFoundException("libc");

        Assert.True(AtomicFile.TryPublish(temp, Destination, (_, _) => throw missing));
        Assert.Equal("new", File.ReadAllText(Destination));
    }

    [Fact]
    public void A_folder_squatting_on_the_name_counts_as_taken_on_the_move_path()
    {
        Directory.CreateDirectory(Destination);
        var temp = Temp();

        Assert.False(AtomicFile.TryPublish(temp, Destination, link: null));
        Assert.True(Directory.Exists(Destination));
    }

    [Fact]
    public void A_move_failure_for_another_reason_propagates()
    {
        var temp = Path.Combine(_dir, "missing.tmp");

        Assert.ThrowsAny<IOException>(() => AtomicFile.TryPublish(temp, Destination, link: null));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Creating_settings_where_the_data_folder_is_a_file_is_not_writable()
    {
        var path = Path.Combine(_dir, "data");
        File.WriteAllText(path, "not a folder");
        var store = new SettingsStore(new DataDirectory(path, DataDirectorySource.Environment));

        var ex = Assert.Throws<MusicSpinException>(() => store.TryCreate(new AppSettings()));

        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
        Assert.EndsWith($"Fix or unset {AppPaths.DataDirEnvironmentVariable}.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["data"], Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName));
        Assert.Throws<MusicSpinException>(() => store.Save(new AppSettings()));
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
        Assert.Throws<ArgumentNullException>(() => store.TryCreate(null!));
    }
}
