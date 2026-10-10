using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>Reads of files another process is replacing (settings.json, queue.json, player.json).</summary>
public sealed class SharedFileTests : IDisposable
{
    private readonly string _root = TempDir.Create("shared-file");

    public void Dispose() => TempDir.TryDelete(_root);

    [Fact]
    public void A_file_held_exclusively_for_a_moment_is_read_once_it_is_released()
    {
        var path = Path.Combine(_root, "queue.json");
        File.WriteAllText(path, "{}");
        var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // like a replace in progress
        var retries = 0;
        var text = SharedFile.ReadAllText(path, attempt =>
        {
            retries = attempt;
            holder.Dispose(); // the writer finishes
        });
        Assert.Equal("{}", text);
        Assert.Equal(1, retries);
    }

    [Fact]
    public void A_file_that_stays_busy_fails_after_a_bounded_number_of_attempts()
    {
        var path = Path.Combine(_root, "busy.json");
        File.WriteAllText(path, "{}");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var retries = 0;
        Assert.ThrowsAny<IOException>(() => SharedFile.ReadAllText(path, _ => retries++));
        Assert.Equal(SharedFile.Attempts - 1, retries);
    }

    [Fact]
    public void A_missing_file_is_not_retried()
    {
        var retries = 0;
        Assert.Throws<FileNotFoundException>(() => SharedFile.ReadAllText(Path.Combine(_root, "nope.json"), _ => retries++));
        Assert.Equal(0, retries);
    }

    [Fact]
    public void Readers_do_not_block_a_writer_that_replaces_the_file()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "old");
        var reader = SharedFile.OpenRead(path);
        var temp = path + ".tmp";
        File.WriteAllText(temp, "new");

        // The atomic save while a reader has the old file open. Unix replaces at once; Windows refuses until the reader
        // closes (even with FileShare.Delete), so the writer retries.
        var retries = 0;
        SharedFile.Replace(temp, path, _ =>
        {
            retries++;
            reader.Dispose();
        });
        reader.Dispose();
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, retries);

        Assert.Equal("new", SharedFile.ReadAllText(path));
    }

    [Fact]
    public void Settings_being_saved_by_another_process_are_loaded_not_replaced_by_defaults()
    {
        var data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        var store = new SettingsStore(data);
        store.Save(new AppSettings { Repeat = "all" });
        using (new FileStream(data.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Held for longer than the retries: a warning, as before.
            Assert.Equal("off", store.Load().Repeat);
            Assert.NotNull(store.LoadWarning);
        }

        Assert.Equal("all", store.Load().Repeat);
        Assert.Null(store.LoadWarning);
    }
}
