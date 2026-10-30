using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

public sealed class DataFolderTests : LibraryTestBase
{
    [Fact]
    public void Portable_data_folder_is_usrdata_next_to_the_executable()
    {
        var exe = Path.Combine(Root, "app");
        var dir = AppPaths.Resolve(null, _ => null, exe);

        Assert.Equal(Path.Combine(exe, ".usrdata"), dir.Path);
        Assert.Equal(DataDirectorySource.Portable, dir.Source);
        Assert.Equal(Path.Combine(exe, ".usrdata", "settings.json"), dir.SettingsFile);
        Assert.Equal(Path.Combine(exe, ".usrdata", "library.db"), dir.LibraryDatabaseFile);
    }

    [Fact]
    public void Trailing_separator_in_base_directory_is_ignored()
    {
        var exe = Path.Combine(Root, "app");
        Assert.Equal(Path.Combine(exe, ".usrdata"), AppPaths.Resolve(null, _ => null, exe + Path.DirectorySeparatorChar).Path);
    }

    [Fact]
    public void Mac_app_bundle_puts_data_next_to_the_app_not_inside_it()
    {
        var apps = Path.Combine(Root, "Applications");
        var macos = Path.Combine(apps, "MusicSpin.app", "Contents", "MacOS");

        var dir = AppPaths.Resolve(null, _ => null, macos);

        Assert.Equal(Path.Combine(apps, ".usrdata"), dir.Path);
        Assert.Equal(Path.Combine(apps, "MusicSpin.app"), AppPaths.FindMacAppBundle(macos));
    }

    [Theory]
    [InlineData("MacOS")]
    [InlineData("Contents/MacOS")]
    [InlineData("NotAnApp/Contents/MacOS")]
    public void Folders_that_only_look_like_bundles_are_not_treated_as_bundles(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Null(AppPaths.FindMacAppBundle(path));
        Assert.Equal(Path.Combine(path, ".usrdata"), AppPaths.Resolve(null, _ => null, path).Path);
    }

    [Fact]
    public void Environment_variable_overrides_portable_folder()
    {
        var custom = Path.Combine(Root, "custom");
        var dir = AppPaths.Resolve(null, n => n == AppPaths.DataDirEnvironmentVariable ? custom : null, Path.Combine(Root, "app"));

        Assert.Equal(custom, dir.Path);
        Assert.Equal(DataDirectorySource.Environment, dir.Source);
    }

    [Fact]
    public void Explicit_override_wins_over_environment_and_is_made_absolute()
    {
        var dir = AppPaths.Resolve("relative-data", _ => Path.Combine(Root, "env"), Path.Combine(Root, "app"));

        Assert.Equal(DataDirectorySource.Override, dir.Source);
        Assert.True(Path.IsPathFullyQualified(dir.Path));
        Assert.EndsWith("relative-data", dir.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_environment_variable_is_ignored()
    {
        var dir = AppPaths.Resolve(null, _ => "  ", Path.Combine(Root, "app"));
        Assert.Equal(DataDirectorySource.Portable, dir.Source);
    }

    [Fact]
    public void Data_folder_is_created_on_first_use()
    {
        Assert.False(Directory.Exists(Data.Path));

        AppPaths.EnsureWritable(Data);

        Assert.True(Directory.Exists(Data.Path));
        Assert.Empty(Directory.GetFiles(Data.Path)); // the write probe cleans up after itself
    }

    [Fact]
    public void Resolving_does_not_touch_the_file_system()
    {
        var exe = Path.Combine(Root, "never-created");
        _ = AppPaths.Resolve(null, _ => null, exe);
        Assert.False(Directory.Exists(exe));
    }

    [Fact]
    public void Settings_round_trip_and_defaults_when_missing()
    {
        var store = new SettingsStore(Data);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.Null(store.LoadWarning);

        store.Save(new AppSettings { Volume = 0.5, OutputDevice = "dac", ExclusiveMode = true });
        var loaded = new SettingsStore(Data).Load();

        Assert.Equal(0.5, loaded.Volume);
        Assert.Equal("dac", loaded.OutputDevice);
        Assert.True(loaded.ExclusiveMode);
        Assert.Empty(Directory.GetFiles(Data.Path, "*.tmp"));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_settings_file_from_before_the_vinyl_transition_switch_keeps_it_on_and_off_is_saved_as_vinylTransition()
    {
        var store = new SettingsStore(Data);
        store.Save(new AppSettings { Volume = 0.5 });
        var path = Path.Combine(Data.Path, "settings.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("vinylTransition")); // as an older app wrote it
        File.WriteAllText(path, json.ToJsonString());

        Assert.True(new SettingsStore(Data).Load().VinylTransition);

        store.Save(new AppSettings { VinylTransition = false });
        Assert.False(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["vinylTransition"]!.GetValue<bool>());
        Assert.False(new SettingsStore(Data).Load().VinylTransition);
    }

    [Fact]
    public void Concurrent_first_starts_create_the_settings_file_once_and_never_fail()
    {
        // Racing `play` commands and players on a fresh data folder all try to write the default settings.
        var results = new bool[8];
        Parallel.For(0, results.Length, i => results[i] = new SettingsStore(Data).TryCreate(new AppSettings { Volume = 0.5 }));
        Assert.Single(results, created => created);
        Assert.Equal(0.5, new SettingsStore(Data).Load().Volume);
        Assert.Empty(Directory.GetFiles(Data.Path, "*.tmp"));
        Assert.False(new SettingsStore(Data).TryCreate(new AppSettings { Volume = 0.1 }));
        Assert.Equal(0.5, new SettingsStore(Data).Load().Volume);
    }
}

[Trait("Category", "Unhappy")]
public sealed class DataFolderUnhappyTests : LibraryTestBase
{
    [Fact]
    public void Usrdata_existing_as_a_file_is_reported_not_writable()
    {
        var exe = Directory.CreateDirectory(Path.Combine(Root, "app")).FullName;
        File.WriteAllText(Path.Combine(exe, ".usrdata"), "oops");
        var dir = AppPaths.Resolve(null, _ => null, exe);

        Assert.False(AppPaths.IsWritable(dir, out var error));
        Assert.Contains("file", error, StringComparison.OrdinalIgnoreCase);
        var ex = Assert.Throws<MusicSpinException>(() => AppPaths.EnsureWritable(dir));
        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
        Assert.Contains(".usrdata", ex.Message, StringComparison.Ordinal);
        Assert.Contains(AppPaths.DataDirEnvironmentVariable, ex.Message, StringComparison.Ordinal);
        Assert.Equal("oops", File.ReadAllText(Path.Combine(exe, ".usrdata"))); // never clobbered
    }

    [Fact]
    public void Usrdata_as_a_file_fails_library_open_for_write_and_read()
    {
        var exe = Directory.CreateDirectory(Path.Combine(Root, "app")).FullName;
        File.WriteAllText(Path.Combine(exe, ".usrdata"), "oops");
        var dir = AppPaths.Resolve(null, _ => null, exe);

        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable,
            Assert.Throws<MusicSpinException>(() => Core.Library.MusicLibrary.OpenWritable(dir)).Code);
        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable,
            Assert.Throws<MusicSpinException>(() => Core.Library.MusicLibrary.OpenForRead(dir)).Code);
    }

    [Fact]
    public void Executable_folder_not_writable_is_reported()
    {
        RequirePosixNonRoot();
        var exe = Directory.CreateDirectory(Path.Combine(Root, "readonly-app")).FullName;
        Chmod(exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var dir = AppPaths.Resolve(null, _ => null, exe);

        Assert.False(AppPaths.IsWritable(dir, out var error));
        Assert.NotNull(error);
        var ex = Assert.Throws<MusicSpinException>(() => AppPaths.EnsureWritable(dir));
        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
        Assert.Contains("--data-dir", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_data_folder_not_writable_is_reported()
    {
        RequirePosixNonRoot();
        Directory.CreateDirectory(Data.Path);
        Chmod(Data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.False(AppPaths.IsWritable(Data, out _));
        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable,
            Assert.Throws<MusicSpinException>(() => new SettingsStore(Data).Save(new AppSettings())).Code);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"volume\": \"loud\"}")]
    public void Corrupt_settings_file_falls_back_to_defaults_with_a_warning(string content)
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllText(Data.SettingsFile, content);
        var store = new SettingsStore(Data);

        var settings = store.Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.NotNull(store.LoadWarning);
        Assert.Contains(Data.SettingsFile, store.LoadWarning, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(Data.SettingsFile)); // left alone for the user to inspect
    }

    [Fact]
    public void Binary_garbage_settings_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllBytes(Data.SettingsFile, [0xFF, 0xFE, 0x00, 0x01, 0x80, 0x90]);
        var store = new SettingsStore(Data);

        Assert.Equal(new AppSettings(), store.Load());
        Assert.NotNull(store.LoadWarning);
    }

    [Fact]
    public void Settings_path_being_a_folder_falls_back_to_defaults()
    {
        Directory.CreateDirectory(Data.SettingsFile);
        var store = new SettingsStore(Data);

        Assert.Equal(new AppSettings(), store.Load());
        Assert.NotNull(store.LoadWarning);
    }

    [Fact]
    public void Unreadable_settings_file_falls_back_to_defaults()
    {
        RequirePosixNonRoot();
        new SettingsStore(Data).Save(new AppSettings { Volume = 0.2 });
        Chmod(Data.SettingsFile, UnixFileMode.None);
        var store = new SettingsStore(Data);

        Assert.Equal(new AppSettings(), store.Load());
        Assert.NotNull(store.LoadWarning);
    }
}
