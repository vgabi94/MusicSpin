using System.Reflection;
using System.Reflection.Emit;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Help › About Music Spin, License and Third-party notices: the documents the executable carries, the notices read from
/// their Markdown, and the dialog's pages, links, Back and closing.
/// </summary>
public sealed class AboutTests
{
    // ---- the documents -------------------------------------------------------------------------------------------

    [Fact]
    public void The_license_and_the_notices_are_inside_the_executable()
    {
        var license = AppDocuments.Read(AppDocuments.LicenseResource);
        Assert.NotNull(license);
        Assert.StartsWith("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 Gabriel-Eugen Vaduva", license, StringComparison.Ordinal);
        Assert.Equal(license, AppDocuments.License());

        Assert.NotNull(AppDocuments.Read(AppDocuments.NoticesResource));
        Assert.Null(AppDocuments.Read("NOT-A-DOCUMENT.md"));
    }

    [Fact]
    public void A_document_missing_from_the_build_points_at_the_repository()
    {
        var note = AppDocuments.Missing(AppDocuments.NoticesResource);

        Assert.Contains("THIRD-PARTY-NOTICES.md is missing from this build", note, StringComparison.Ordinal);
        Assert.Contains("https://github.com/vgabi94/MusicSpin", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_of_the_notices_table_becomes_a_component_in_plain_text()
    {
        var markdown = AppDocuments.Read(AppDocuments.NoticesResource)!;
        var rows = markdown.Split('\n').Count(l => l.TrimStart().StartsWith('|')) - 2; // less the header and its rule

        var notices = AppDocuments.ParseNotices(markdown);

        Assert.Equal(rows, notices.Components.Count);
        Assert.StartsWith("MusicSpin itself is MIT licensed (see LICENSE)", notices.Intro, StringComparison.Ordinal);
        var ffmpeg = notices.Components[0];
        Assert.StartsWith("FFmpeg (libavcodec", ffmpeg.Name, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://ffmpeg.org"), ffmpeg.Link);
        Assert.Contains("LGPL-2.1-or-later", ffmpeg.License, StringComparison.Ordinal);
        Assert.StartsWith("Separate shared libraries in ffmpeg/", ffmpeg.Ships, StringComparison.Ordinal);
        Assert.All(notices.Components, c => Assert.DoesNotContain("](", c.Name + c.License + c.Ships, StringComparison.Ordinal));
        Assert.All(notices.Components, c => Assert.DoesNotContain("`", c.Name + c.License + c.Ships, StringComparison.Ordinal));
        Assert.Contains(notices.Components, c => c is { Name: ".NET runtime", Link: null });

        var lgpl = Assert.Single(notices.Sections);
        Assert.Equal("LGPL notes", lgpl.Title);
        Assert.Contains(lgpl.Paragraphs, p => p.StartsWith("• FFmpeg is loaded at runtime from MUSICSPIN_FFMPEG_PATH", StringComparison.Ordinal));
        Assert.Contains(lgpl.Paragraphs, p => p.StartsWith("The LGPL requires", StringComparison.Ordinal) && !p.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void Notices_without_a_table_short_rows_or_sections_still_read()
    {
        var empty = AppDocuments.ParseNotices(string.Empty);
        Assert.Equal((string.Empty, 0, 0), (empty.Intro, empty.Components.Count, empty.Sections.Count));
        Assert.Throws<ArgumentNullException>(() => AppDocuments.ParseNotices(null!));

        var notices = AppDocuments.ParseNotices("""
            # Notices

            Only **this**.

            | A | B | C |
            |---|:-:|---|
            | short | row |
            | [Lib](not a url) | MIT | `inside` |
            | [Other](https://example.org/x) | BSD | a | b |

            ## Notes
            First line
            continues.
            - one
            - two
            """);

        Assert.Equal("Only this.", notices.Intro);
        Assert.Equal(2, notices.Components.Count); // the short row is skipped
        Assert.Equal(new ThirdPartyComponent("[Lib](not a url)", null, "MIT", "inside"), notices.Components[0]);
        Assert.Equal(new ThirdPartyComponent("Other", new Uri("https://example.org/x"), "BSD", "a | b"), notices.Components[1]);
        Assert.Equal(["First line continues.", "• one", "• two"], Assert.Single(notices.Sections).Paragraphs);
    }

    [Fact]
    public void The_version_is_the_apps_without_the_source_revision()
    {
        var app = typeof(AboutViewModel).Assembly;
        var version = AboutViewModel.VersionOf(app);

        // The SDK stamps "0.1.0+<commit>"; the dialog shows the part before the '+'.
        Assert.Contains("+", app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, StringComparison.Ordinal);
        Assert.Equal(app.GetName().Version!.ToString(3), version);
        Assert.Equal(version, new AboutViewModel().Version);
        Assert.Equal("Version 1.2.3-beta", new AboutViewModel("1.2.3-beta").VersionText);
        Assert.Throws<ArgumentNullException>(() => AboutViewModel.VersionOf(null!));
    }

    [Fact]
    public void The_copyright_is_the_builds_with_the_full_name_and_a_build_without_one_names_the_author()
    {
        Assert.Equal("Copyright © 2026 Gabriel-Eugen Vaduva", AboutViewModel.Copyright);

        var unstamped = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Unstamped"), AssemblyBuilderAccess.Run);
        Assert.Equal("Copyright © Gabriel-Eugen Vaduva", AboutViewModel.CopyrightOf(unstamped));
        Assert.Equal("0.0.0", AboutViewModel.VersionOf(unstamped)); // no informational version: the assembly version
        Assert.Throws<ArgumentNullException>(() => AboutViewModel.CopyrightOf(null!));
    }

    [Fact]
    public void Each_entry_opens_its_page_and_Back_returns_to_About()
    {
        var about = new AboutViewModel("1.0");
        Assert.Equal((null, null), (about.LicenseText, about.Notices)); // nothing read before a page shows it

        about.ShowLicenseCommand.Execute(null);
        Assert.True(about.IsOpen);
        Assert.Equal((AboutPage.License, "License"), (about.Page, about.Title));
        Assert.Contains("Permission is hereby granted", about.LicenseText, StringComparison.Ordinal);
        Assert.Null(about.Notices);

        about.BackCommand.Execute(null);
        Assert.Equal((AboutPage.About, "About Music Spin"), (about.Page, about.Title));
        Assert.True(about.IsOpen);

        about.Close();
        about.ShowNoticesCommand.Execute(null);
        Assert.Equal((true, AboutPage.Notices, "Third-party notices"), (about.IsOpen, about.Page, about.Title));
        Assert.NotEmpty(about.Notices!.Components);

        about.Close();
        about.ShowAboutCommand.Execute(null);
        Assert.Equal((true, AboutPage.About), (about.IsOpen, about.Page));
        Assert.Equal(3, about.Opens);
    }

    // ---- the dialog ----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Help_opens_About_with_the_version_the_author_and_the_links()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        Choose(window, "_Help", "_About Music Spin");

        Assert.True(app.ViewModel.About.IsOpen);
        Assert.True(Gui.Find<StackPanel>(window, "AboutPage").IsEffectivelyVisible);
        Assert.Equal("About Music Spin", Gui.Find<TextBlock>(window, "AboutTitle").Text);
        Assert.Equal($"Version {AboutViewModel.VersionOf(typeof(AboutViewModel).Assembly)}", Gui.Find<TextBlock>(window, "AboutVersion").Text);
        Assert.Equal("Copyright © 2026 Gabriel-Eugen Vaduva", Gui.Find<TextBlock>(window, "AboutCopyright").Text);
        Assert.Equal(new Uri("https://github.com/vgabi94/MusicSpin"), Gui.Find<HyperlinkButton>(window, "RepositoryLink").NavigateUri);
        Assert.False(Gui.Find<Button>(window, "AboutBack").IsVisible);
        await Gui.UntilAsync(() => Gui.Find<Button>(window, "CloseAbout").IsFocused, "✕ has the focus");
    }

    [AvaloniaFact]
    public async Task The_links_show_the_license_and_the_notices_and_back_returns()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        Choose(window, "_Help", "_About Music Spin");

        Gui.Click(window, Gui.Find<HyperlinkButton>(window, "LicenseLink"));
        Assert.Equal("License", Gui.Find<TextBlock>(window, "AboutTitle").Text);
        Assert.True(Gui.Find<ScrollViewer>(window, "LicensePage").IsEffectivelyVisible);
        Assert.False(Gui.Find<StackPanel>(window, "AboutPage").IsEffectivelyVisible);
        Assert.Contains("Gabriel-Eugen Vaduva", Gui.Find<SelectableTextBlock>(window, "LicenseText").Text, StringComparison.Ordinal);

        Gui.Click(window, Gui.Find<Button>(window, "AboutBack"));
        Assert.True(Gui.Find<StackPanel>(window, "AboutPage").IsEffectivelyVisible);

        Gui.Click(window, Gui.Find<HyperlinkButton>(window, "NoticesLink"));
        Assert.Equal("Third-party notices", Gui.Find<TextBlock>(window, "AboutTitle").Text);
        var rows = Gui.Find<ItemsControl>(window, "NoticeComponents");
        var components = app.ViewModel.About.Notices!.Components;
        Assert.Equal(components.Count, rows.ItemCount);
        Assert.Contains(Gui.All<TextBlock>(rows), t => t.Text == components[^1].Name);
    }

    [AvaloniaFact]
    public async Task Help_License_and_Third_party_notices_open_their_pages_directly()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;

        Choose(window, "_Help", "_License");
        Assert.Equal((true, AboutPage.License), (app.ViewModel.About.IsOpen, app.ViewModel.About.Page));
        Assert.True(Gui.Find<Button>(window, "AboutBack").IsVisible);

        Gui.Click(window, Gui.Find<Button>(window, "CloseAbout"));
        Assert.False(app.ViewModel.About.IsOpen);

        Choose(window, "_Help", "_Third-party notices");
        Assert.Equal((true, AboutPage.Notices), (app.ViewModel.About.IsOpen, app.ViewModel.About.Page));
    }

    [AvaloniaFact]
    public async Task Esc_or_a_click_on_the_dim_closes_it_and_the_shortcuts_wait_while_it_is_open()
    {
        await using var app = await AppHarness.OpenAsync();
        var window = app.Window;
        var vm = app.ViewModel;

        vm.About.ShowAboutCommand.Execute(null);
        Gui.Settle();
        Assert.False(vm.SpaceCommand.CanExecute(null)); // Space doesn't play under the dialog

        Gui.Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(vm.About.IsOpen);
        Assert.True(vm.SpaceCommand.CanExecute(null));

        vm.About.ShowNoticesCommand.Execute(null);
        Gui.Settle();
        var backdrop = Gui.Find<Button>(window, "AboutBackdrop");
        window.MouseMove(new Avalonia.Point(10, Gui.BoundsIn(window, backdrop).Bottom - 10));
        window.MouseDown(new Avalonia.Point(10, Gui.BoundsIn(window, backdrop).Bottom - 10), MouseButton.Left);
        window.MouseUp(new Avalonia.Point(10, Gui.BoundsIn(window, backdrop).Bottom - 10), MouseButton.Left);
        Gui.Settle();
        Assert.False(vm.About.IsOpen);
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Snapshot_of_About(AppTheme theme)
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Theme = theme });
        app.ViewModel.About.ShowAboutCommand.Execute(null);
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, Gui.Find<Border>(app.Window, "AboutBox"), $"about-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaFact]
    public async Task Snapshot_of_the_notices()
    {
        await using var app = await AppHarness.OpenAsync();
        app.ViewModel.About.ShowNoticesCommand.Execute(null);
        Gui.FinishAnimations();

        Snapshots.Match(app.Window, Gui.Find<Border>(app.Window, "AboutBox"), "about-notices-dark");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static IEnumerable<MenuItem> TopItems(Window window) => Gui.All<MenuItem>(Gui.Find<NativeMenuBar>(window, "AppMenuBar"));

    /// <summary>Opens a menu by clicking its title, then clicks one of its entries.</summary>
    private static void Choose(Window window, string top, string header)
    {
        var item = TopItems(window).Single(i => (string?)i.Header == top);
        Gui.Click(window, item);
        Assert.True(item.IsSubMenuOpen, $"{top} didn't open");
        var entry = item.GetLogicalChildren().OfType<MenuItem>().Single(i => (string?)i.Header == header);
        Gui.Click(window, entry);
        Gui.Settle();
    }
}
