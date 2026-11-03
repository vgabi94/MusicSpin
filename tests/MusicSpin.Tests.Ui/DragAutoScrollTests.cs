using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.App.Views.Pages;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Auto-scroll while dragging near a long list's edges (#77): the playlist page and the queue drawer, driven by the
/// app's fake clock (no wall-clock waits).
/// </summary>
public sealed class DragAutoScrollTests
{
    private const int Slid = 500;
    private const int LongCount = 40;

    private static void LongPlaylist(MusicLibrary library, IReadOnlyList<string> paths)
    {
        var dir = Path.GetDirectoryName(paths[0])!;
        library.CreatePlaylist("Long", [.. Enumerable.Range(1, LongCount)
            .Select(i => new PlaylistInput(Path.Combine(dir, $"gone-{i:00}.flac"), Title: Title(i), Artist: "Nobody", DurationSeconds: 60))]);
    }

    private static string Title(int i) => $"Track {i:00}";

    private static async Task<AppHarness> OpenPlaylistAsync()
    {
        var app = await AppHarness.OpenAsync(JazzLibrary.Options(LongPlaylist));
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Any(p => p.Title == "Long"), "Long in the sidebar");
        await PlaylistTests.OpenPlaylistAsync(app, "Long", LongCount);
        return app;
    }

    private static async Task<AppHarness> OpenQueueAsync(int rows = LongCount)
    {
        var app = await AppHarness.OpenAsync(JazzLibrary.Options());
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the library");
        await QueueRowsAsync(app, rows);
        app.ViewModel.QueueDrawer.Open();
        Gui.FinishAnimations(Slid);
        return app;
    }

    private static async Task QueueRowsAsync(AppHarness app, int count)
    {
        IReadOnlyList<QueueItem> rows = [.. Enumerable.Range(1, count).Select(i => SampleLibrary.Item(((6 + ((i - 1) / 3)) % 10) + 1, ((i - 1) % 3) + 1))];
        app.Scripted!.SetQueue([SampleLibrary.Item(6, 1), .. rows, SampleLibrary.Item(6, 2)], 0, count);
        await app.Scripted.PushAsync("queueChanged");
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.QueueDrawer.Rows.Count == count, "the rows");
    }

    private static TracksPage PageView(AppHarness app) => Gui.All<TracksPage>(app.Window).Single();

    private static Control PlaylistRow(AppHarness app, int index)
        => (Control)Gui.Find<ItemsControl>(PageView(app), "TrackList").ContainerFromIndex(index)!;

    private static Control QueueRow(AppHarness app, int index)
        => (Control)Gui.Find<ItemsControl>(app.Window, "QueueRows").ContainerFromIndex(index)!;

    private static Border DropLine(AppHarness app, Visual root)
        => Gui.All<Border>(root).Single(b => b.Name == "DropLine");

    private static string[] Show(AppHarness app, string playlist) => app.Read(library => JazzLibrary.Show(library, playlist));

    private static async Task<JsonElement> SentAsync(AppHarness app, string cmd, int count = 1)
    {
        await Gui.UntilAsync(() => app.Scripted!.Count(cmd) >= count, $"'{cmd}' sent");
        return app.Scripted!.Sent(cmd)[count - 1];
    }

    /// <summary>Keeps the pointer in the edge zone while the fake clock advances until <paramref name="ready"/>, or fails.</summary>
    private static async Task HoldNearEdgeAsync(AppHarness app, Point at, Func<bool> ready, string what)
    {
        Mouse.MoveTo(app.Window, at);
        await Gui.UntilAsync(() =>
        {
            if (ready())
            {
                return true;
            }

            app.Advance(SpinClock.FrameInterval);
            Mouse.MoveTo(app.Window, at); // keep the target (and the timer) alive if layout moved the view
            return ready();
        }, what);
    }

    private static Point BottomEdge(Rect view) => new(view.Center.X, view.Bottom - 4);

    private static Point TopEdge(Rect view) => new(view.Center.X, view.Top + 4);

    private static Point MiddleOf(Rect view) => view.Center;

    // ---- playlist page ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Near_the_bottom_of_a_long_playlist_the_list_scrolls_down_and_the_middle_does_not()
    {
        await using var app = await OpenPlaylistAsync();
        var page = PageView(app);
        Assert.True(page.MaxScrollOffsetY > 0, "the playlist is longer than the view");

        Mouse.StartDrag(app.Window, PlaylistRow(app, 0));
        Gui.FinishAnimations(Slid);
        var view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        var before = page.ScrollOffsetY;

        Mouse.MoveTo(app.Window, MiddleOf(view));
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(before, page.ScrollOffsetY);

        await HoldNearEdgeAsync(app, BottomEdge(view), () => page.ScrollOffsetY > before + 20, "scrolled down");
        Assert.True(page.ScrollOffsetY > before + 20);
    }

    [AvaloniaFact]
    public async Task Near_the_top_of_a_long_playlist_the_list_scrolls_up()
    {
        await using var app = await OpenPlaylistAsync();
        var page = PageView(app);
        page.ScrollBy(page.MaxScrollOffsetY);
        Gui.Settle();
        Assert.True(page.ScrollOffsetY > 100);

        // a row near the bottom of the list (realized after scrolling down)
        var lastVisible = Enumerable.Range(0, LongCount).Last(i => Gui.Find<ItemsControl>(page, "TrackList").ContainerFromIndex(i) is not null);
        Mouse.StartDrag(app.Window, PlaylistRow(app, lastVisible));
        Gui.FinishAnimations(Slid);
        var view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        var before = page.ScrollOffsetY;

        await HoldNearEdgeAsync(app, TopEdge(view), () => page.ScrollOffsetY < before - 20, "scrolled up");
    }

    [AvaloniaFact]
    public async Task Closer_to_the_edge_scrolls_faster()
    {
        await using var app = await OpenPlaylistAsync();
        var page = PageView(app);
        Mouse.StartDrag(app.Window, PlaylistRow(app, 0));
        Gui.FinishAnimations(Slid);
        var view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");

        // inner part of the 48 px zone
        Mouse.MoveTo(app.Window, new Point(view.Center.X, view.Bottom - 40));
        var start = page.ScrollOffsetY;
        app.Advance(TimeSpan.FromMilliseconds(160));
        var slow = page.ScrollOffsetY - start;

        page.ScrollBy(-page.ScrollOffsetY);
        Gui.Settle();
        Mouse.MoveTo(app.Window, BottomEdge(view));
        start = page.ScrollOffsetY;
        app.Advance(TimeSpan.FromMilliseconds(160));
        var fast = page.ScrollOffsetY - start;

        Assert.True(fast > slow, $"fast {fast} should beat slow {slow}");
        Assert.True(slow > 0, "the inner edge still scrolls");
    }

    [AvaloniaFact]
    public async Task A_drop_after_scrolling_lands_where_the_drop_line_showed()
    {
        await using var app = await OpenPlaylistAsync();
        var page = PageView(app);
        var line = DropLine(app, page);
        Mouse.StartDrag(app.Window, PlaylistRow(app, 0));
        Gui.FinishAnimations(Slid);
        var view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");

        await HoldNearEdgeAsync(app, BottomEdge(view), () => page.ScrollOffsetY > 120, "scrolled past the first screen");

        // pick a realized row under the pointer's half of the view (not the dragged first entry)
        var list = Gui.Find<ItemsControl>(page, "TrackList");
        var targetIndex = Enumerable.Range(1, LongCount - 1)
            .Where(i => list.ContainerFromIndex(i) is Control row && BoundsIn(app.Window, row) is { } b && b.Center.Y > view.Top && b.Center.Y < view.Bottom)
            .Skip(2)
            .First();
        var target = (Control)list.ContainerFromIndex(targetIndex)!;
        var targetTitle = Title(targetIndex + 1); // entries are 1-based titles Track 01 at index 0
        Assert.Equal(targetTitle, ((TrackRow)target.DataContext!).Item.Title);

        var above = target.TranslatePoint(new Point(60, 4), app.Window)!.Value;
        Mouse.MoveTo(app.Window, above);
        Assert.True(line.IsEffectivelyVisible);
        var lineY = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), app.Window)!.Value.Y;
        Assert.Equal(target.TranslatePoint(default, app.Window)!.Value.Y, lineY, 1.5);

        Mouse.Release(app.Window, above);
        await Gui.UntilAsync(() =>
        {
            var shown = Show(app, "Long");
            var at = Array.IndexOf(shown, targetTitle);
            return at > 0 && shown[at - 1] == Title(1);
        }, "the move");
        var after = Show(app, "Long");
        var destAt = Array.IndexOf(after, targetTitle);
        Assert.Equal(Title(1), after[destAt - 1]);
    }

    [AvaloniaFact]
    public async Task The_drop_line_follows_the_rows_as_they_scroll_under_a_fixed_pointer()
    {
        await using var app = await OpenPlaylistAsync();
        var page = PageView(app);
        var line = DropLine(app, page);
        Mouse.StartDrag(app.Window, PlaylistRow(app, 0));
        Gui.FinishAnimations(Slid);
        var view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        var at = BottomEdge(view);
        Mouse.MoveTo(app.Window, at);
        Assert.True(line.IsEffectivelyVisible);
        var y1 = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), app.Window)!.Value.Y;

        await HoldNearEdgeAsync(app, at, () => page.ScrollOffsetY > 40, "scrolled");
        Mouse.MoveTo(app.Window, at);
        var y2 = line.TranslatePoint(new Point(0, line.Bounds.Height / 2), app.Window)!.Value.Y;
        Assert.NotEqual(y1, y2);
    }

    [AvaloniaFact]
    public async Task Scrolling_stops_at_the_playlist_end_and_when_the_drag_is_cancelled()
    {
        await using var app = await OpenPlaylistAsync();
        var page = PageView(app);
        page.ScrollBy(page.MaxScrollOffsetY);
        Gui.Settle();
        var atEnd = page.ScrollOffsetY;

        var list = Gui.Find<ItemsControl>(page, "TrackList");
        var lastVisible = Enumerable.Range(0, LongCount).Last(i => list.ContainerFromIndex(i) is not null);
        Mouse.StartDrag(app.Window, PlaylistRow(app, lastVisible));
        Gui.FinishAnimations(Slid);
        var view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        Mouse.MoveTo(app.Window, BottomEdge(view));
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(atEnd, page.ScrollOffsetY);

        // cancel: the window loses the pointer
        Mouse.Release(app.Window, MiddleOf(view)); // end that drag cleanly first
        page.ScrollBy(-page.ScrollOffsetY);
        Gui.Settle();
        // fresh drag, then capture lost
        Mouse.StartDrag(app.Window, PlaylistRow(app, 0));
        Gui.FinishAnimations(Slid);
        view = page.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        Mouse.MoveTo(app.Window, BottomEdge(view));
        await HoldNearEdgeAsync(app, BottomEdge(view), () => page.ScrollOffsetY > 10, "scrolling");
        var before = page.ScrollOffsetY;
        app.Window.RaiseEvent(new PointerCaptureLostEventArgs(app.Window, new Pointer(1, PointerType.Mouse, true)));
        Gui.Settle();
        Assert.False(app.Window.Drag.IsDragging);
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(before, page.ScrollOffsetY);
    }

    // ---- queue drawer -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Near_the_bottom_of_a_long_queue_the_list_scrolls_down_and_the_middle_does_not()
    {
        await using var app = await OpenQueueAsync();
        var drawer = app.Window.QueueDrawerView;
        Assert.True(drawer.MaxScrollOffsetY > 0, "the queue is longer than the view");

        Mouse.StartDrag(app.Window, QueueRow(app, 0));
        var view = drawer.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        var before = drawer.ScrollOffsetY;

        Mouse.MoveTo(app.Window, MiddleOf(view));
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(before, drawer.ScrollOffsetY);

        await HoldNearEdgeAsync(app, BottomEdge(view), () => drawer.ScrollOffsetY > before + 20, "scrolled down");
    }

    [AvaloniaFact]
    public async Task Near_the_top_of_a_long_queue_the_list_scrolls_up()
    {
        await using var app = await OpenQueueAsync();
        var drawer = app.Window.QueueDrawerView;
        drawer.ScrollBy(drawer.MaxScrollOffsetY);
        Gui.Settle();
        var before = drawer.ScrollOffsetY;
        Assert.True(before > 100);

        Mouse.StartDrag(app.Window, QueueRow(app, LongCount - 1));
        var view = drawer.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        await HoldNearEdgeAsync(app, TopEdge(view), () => drawer.ScrollOffsetY < before - 20, "scrolled up");
    }

    [AvaloniaFact]
    public async Task A_queue_row_dropped_after_scrolling_lands_where_the_drop_line_showed()
    {
        await using var app = await OpenQueueAsync();
        var drawer = app.Window.QueueDrawerView;
        var line = DropLine(app, drawer);
        Mouse.StartDrag(app.Window, QueueRow(app, 0));
        var view = drawer.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");

        await HoldNearEdgeAsync(app, BottomEdge(view), () => drawer.ScrollOffsetY > 80, "scrolled");

        var target = QueueRow(app, 8);
        var above = new Point(BoundsIn(app.Window, target)!.Value.Center.X, BoundsIn(app.Window, target)!.Value.Top + 2);
        Mouse.MoveTo(app.Window, above);
        Assert.True(line.IsVisible);
        Mouse.Release(app.Window, above);

        var move = await SentAsync(app, "queue.move");
        Assert.Equal(1, move.GetProperty("from").GetInt32()); // queue index 1 is the first up-next row (index 0 in the drawer)
        Assert.Equal(8, move.GetProperty("to").GetInt32()); // drawer row 7's queue index
        Assert.Equal(SampleLibrary.Item(7, 1).Title, app.Scripted!.Queue[8].Item.Title);
    }

    [AvaloniaFact]
    public async Task A_library_track_dropped_into_a_long_drawer_after_scrolling_lands_where_the_line_showed()
    {
        await using var app = await OpenQueueAsync();
        var drawer = app.Window.QueueDrawerView;
        // a Kind of Blue track on the albums page
        app.ViewModel.Sidebar.NavigateCommand.Execute(app.ViewModel.Sidebar.Albums);
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "albums");
        Gui.Settle();
        var card = Gui.All<Button>(app.Window).First(b => b.Classes.Contains("card") && b.DataContext is AlbumItemViewModel { Title: "Kind of Blue" });

        Mouse.StartDrag(app.Window, card);
        Gui.FinishAnimations(Slid);
        var view = drawer.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        await HoldNearEdgeAsync(app, BottomEdge(view), () => drawer.ScrollOffsetY > 60, "scrolled");

        var target = QueueRow(app, 6);
        var above = new Point(BoundsIn(app.Window, target)!.Value.Center.X, BoundsIn(app.Window, target)!.Value.Top + 2);
        Mouse.MoveTo(app.Window, above);
        Mouse.Release(app.Window, above);

        await SentAsync(app, "queue.add");
        var move = await SentAsync(app, "queue.move");
        // three Kind of Blue tracks added at end then moved to the drop index among the live queue
        Assert.True(move.GetProperty("to").GetInt32() <= 10, $"to={move.GetProperty("to").GetInt32()}");
    }

    [AvaloniaFact]
    public async Task Scrolling_stops_at_the_queue_end_and_when_the_drag_is_cancelled()
    {
        await using var app = await OpenQueueAsync();
        var drawer = app.Window.QueueDrawerView;
        drawer.ScrollBy(drawer.MaxScrollOffsetY);
        Gui.Settle();
        var atEnd = drawer.ScrollOffsetY;

        Mouse.StartDrag(app.Window, QueueRow(app, LongCount - 1));
        var view = drawer.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        Mouse.MoveTo(app.Window, BottomEdge(view));
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(atEnd, drawer.ScrollOffsetY);

        Mouse.Release(app.Window, MiddleOf(view));
        drawer.ScrollBy(-atEnd / 2);
        Gui.Settle();
        Mouse.StartDrag(app.Window, QueueRow(app, 0));
        view = drawer.ViewportIn(app.Window) ?? throw new InvalidOperationException("no viewport");
        await HoldNearEdgeAsync(app, BottomEdge(view), () => drawer.ScrollOffsetY > 10, "scrolling");
        var before = drawer.ScrollOffsetY;
        app.Window.RaiseEvent(new PointerCaptureLostEventArgs(app.Window, new Pointer(1, PointerType.Mouse, true)));
        Gui.Settle();
        Assert.False(app.Window.Drag.IsDragging);
        app.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(before, drawer.ScrollOffsetY);
    }

    private static Rect? BoundsIn(Visual relativeTo, Control control)
        => control.TransformToVisual(relativeTo) is { } transform
            ? new Rect(control.Bounds.Size).TransformToAABB(transform)
            : null;
}
