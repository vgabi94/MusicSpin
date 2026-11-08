using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui.Diagnostics;

/// <summary>Page load, navigation, scene lifetime and decoded-cover cache. Operation spans, not paint time.</summary>
public sealed class UiMarkerTests
{
    [Fact]
    public void App_assembly_matches_the_marker_compile_flag()
    {
        var assembly = typeof(CoverService).Assembly;
        var literals = new[]
        {
            "cover.load", "cover.decode", "cover.accent", "covers.recheck",
            "ui.page.load", "ui.page.layout", "ui.navigate", "ui.library.reload",
            "ui.spin.session", "ui.spectrum.session", "ui.scene", "ui.nowplaying.show",
            "musicspin.covers.cached_bytes",
        };
#if PERF_MARKERS
        Assert.True(MarkerMetadata.HasType(assembly, "MusicSpin.App.Diagnostics", "CoverGauges"));
        Assert.All(literals, literal => Assert.True(MarkerMetadata.HasUserString(assembly, literal), literal));
#else
        Assert.False(MarkerMetadata.HasType(assembly, "MusicSpin.App.Diagnostics", "CoverGauges"));
        Assert.All(literals, literal => Assert.False(MarkerMetadata.HasUserString(assembly, literal), literal));
#endif
    }

    [AvaloniaFact]
    public async Task Opening_the_window_loads_the_page_and_a_scene_is_one_session()
    {
#if !PERF_MARKERS
        Assert.False(MarkerMetadata.HasUserString(typeof(CoverService).Assembly, "ui.page.load"));
        await Task.CompletedTask;
#else
        using var capture = MarkerCapture.Start();
        await using var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true,
            Library = SampleLibrary.Create(),
            Covers = SampleLibrary.Covers(accents: true),
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");

        Assert.Contains(capture.Named("ui.navigate"), mark => mark.Tags.GetValueOrDefault("page") == ((int)PageKind.Albums).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(capture.Named("ui.page.load"), mark => mark.Tags.GetValueOrDefault("page") == "1");
        Assert.NotEmpty(capture.Named("ui.page.layout"));
        Assert.NotEmpty(capture.Named("ui.library.reload"));

        app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        Gui.FinishAnimations();
        var source = Gui.All<Control>(app.Window).First(c => SceneSource.GetAlbumId(c) == SampleLibrary.AlbumId(4));
        var clickable = source as ShelfSleeve ?? (Control)source.GetVisualAncestors().OfType<Button>().First();
        Gui.Click(app.Window, clickable);
        await Gui.UntilAsync(() => app.ViewModel.Scene.IsRunning, "the scene started");
        app.ViewModel.Scene.Dispose();

        Assert.Single(capture.Named("ui.scene"));
        Assert.DoesNotContain(capture.Marks, mark => mark.Name.Contains("frame", StringComparison.Ordinal));
#endif
    }

    [AvaloniaFact]
    public async Task A_second_request_for_the_same_cover_does_not_decode_again()
    {
        var root = TempDir.Create("covermarkers");
        var music = Path.Combine(root, "music");
        var data = new DataDirectory(Path.Combine(root, "data"), DataDirectorySource.Override);
        try
        {
            TestImages.Album(music, "Album", "Artist", tracks: 1, cover: TestImages.Cover());
            string albumId;
            using (var library = MusicLibrary.OpenWritable(data, lockTimeout: TimeSpan.FromSeconds(5)))
            {
                library.AddFolder(music);
                library.Scan(cancellationToken: TestContext.Current.CancellationToken);
                albumId = library.Albums(new AlbumQuery()).Items.Single().Id;
            }

            var changes = new StubLibrary();
            var covers = new CoverService(data, new SkiaCoverResizer(), lockTimeout: TimeSpan.FromSeconds(5), library: changes);
            using var first = MarkerCapture.Start();
            var lease = await covers.CoverAsync(albumId, CoverSize.Small);
            Assert.NotNull(lease);
#if PERF_MARKERS
            Assert.Single(first.Named("cover.load"));
            Assert.Single(first.Named("cover.decode"));
            Assert.NotEmpty(first.Named("cover.file"));
#endif
            using var second = MarkerCapture.Start();
            var again = await covers.CoverAsync(albumId, CoverSize.Small);
            Assert.NotNull(again);
#if PERF_MARKERS
            Assert.Empty(second.Named("cover.load"));
            Assert.Empty(second.Named("cover.decode"));
#else
            Assert.Empty(second.Marks);
#endif
            lease.Dispose();
            again.Dispose();
        }
        finally
        {
            TempDir.TryDelete(root);
        }
    }
}
