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
        Assert.StartsWith("GNU GENERAL PUBLIC LICENSE", license.Trim(), StringComparison.Ordinal);
        Assert.Contains("Version 3, 29 June 2007", license, StringComparison.Ordinal);
        Assert.Contains("END OF TERMS AND CONDITIONS", license, StringComparison.Ordinal);

        var exception = AppDocuments.Read(AppDocuments.ExceptionResource);
        Assert.NotNull(exception);
        Assert.Contains("Copyright (C) 2026 Gabriel-Eugen Vaduva", exception, StringComparison.Ordinal);
        Assert.Contains("either version 3 of the License, or (at your option) any later", exception, StringComparison.Ordinal);
        Assert.Contains("Additional permission under GNU GPL version 3 section 7", exception, StringComparison.Ordinal);
        Assert.Contains("Svg.Custom", exception, StringComparison.Ordinal);

        Assert.NotNull(AppDocuments.Read(AppDocuments.NoticesResource));
        Assert.Null(AppDocuments.Read("NOT-A-DOCUMENT.md"));
    }

    [Fact]
    public void The_license_page_shows_the_notice_and_the_exception_before_the_gpl()
    {
        var page = AppDocuments.License();

        var notice = page.IndexOf("Copyright (C) 2026 Gabriel-Eugen Vaduva", StringComparison.Ordinal);
        var exception = page.IndexOf("Additional permission under GNU GPL version 3 section 7", StringComparison.Ordinal);
        var gpl = page.IndexOf("GNU GENERAL PUBLIC LICENSE", StringComparison.Ordinal);
        Assert.True(notice >= 0 && notice < exception && exception < gpl, $"notice {notice}, exception {exception}, GPL {gpl}");
        Assert.EndsWith(AppDocuments.Read(AppDocuments.LicenseResource)!, page, StringComparison.Ordinal); // the GPL whole
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(AppDocuments.ExceptionResource, AppDocuments.LicenseResource)]
    [InlineData(AppDocuments.LicenseResource, AppDocuments.ExceptionResource)]
    public void A_license_document_missing_from_the_build_is_named_and_the_other_still_shows(string missing, string present)
    {
        var page = AppDocuments.License(name => name == missing ? null : $"text of {name}");

        Assert.Contains($"{missing} is missing from this build", page, StringComparison.Ordinal);
        Assert.Contains($"text of {present}", page, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_build_without_either_license_document_points_at_the_repository_for_both()
    {
        var page = AppDocuments.License(_ => null);

        Assert.Contains("LICENSE-EXCEPTION.txt is missing from this build", page, StringComparison.Ordinal);
        Assert.Contains("LICENSE is missing from this build", page, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => AppDocuments.License(null!));
    }

    [Fact]
    public void A_document_missing_from_the_build_points_at_the_repository()
    {
        var note = AppDocuments.Missing(AppDocuments.NoticesResource);

        Assert.Contains("THIRD-PARTY-NOTICES.md is missing from this build", note, StringComparison.Ordinal);
        Assert.Contains("https://github.com/vgabi94/MusicSpin", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_of_the_notices_table_becomes_a_name_and_a_license_in_plain_text()
    {
        var markdown = AppDocuments.Read(AppDocuments.NoticesResource)!;
        var rows = markdown.Split('\n').Count(l => l.TrimStart().StartsWith('|')) - 2; // less the header and its rule

        var notices = AppDocuments.ParseNotices(markdown);

        Assert.Equal(rows, notices.Components.Count);
        Assert.Equal(
            "MusicSpin itself is licensed under the GNU GPL, version 3 or later (see LICENSE and LICENSE-EXCEPTION.txt). Release builds contain or load the components below.",
            notices.Intro);
        var ffmpeg = notices.Components[0];
        Assert.StartsWith("FFmpeg (libavcodec", ffmpeg.Name, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://ffmpeg.org"), ffmpeg.Link);
        Assert.Contains("LGPL-2.1-or-later", ffmpeg.License, StringComparison.Ordinal);
        Assert.All(notices.Components, c => Assert.DoesNotContain("](", c.Name + c.License, StringComparison.Ordinal));
        Assert.All(notices.Components, c => Assert.DoesNotContain("`", c.Name + c.License, StringComparison.Ordinal));
        Assert.Contains(notices.Components, c => c is { Name: ".NET runtime", Link: null, License: "MIT" });
    }

    [Fact]
    public void Notices_keep_only_the_first_paragraph_and_the_first_tables_names_and_licenses()
    {
        var empty = AppDocuments.ParseNotices(string.Empty);
        Assert.Equal((string.Empty, 0), (empty.Intro, empty.Components.Count));
        Assert.Throws<ArgumentNullException>(() => AppDocuments.ParseNotices(null!));

        var notices = AppDocuments.ParseNotices("""
            # Notices

            Only **this**,
            on two lines.

            Not this paragraph.

            | A | B | C |
            |---|:-:|---|
            | lonely |
            | [Lib](not a url) | MIT | `inside` |
            | [Other](https://example.org/x) | BSD |

            ## Notes
            Not this either.

            | X | Y |
            |---|---|
            | not | this |
            """);

        Assert.Equal("Only this, on two lines.", notices.Intro);
        Assert.Equal(
            [new ThirdPartyComponent("[Lib](not a url)", null, "MIT"), new ThirdPartyComponent("Other", new Uri("https://example.org/x"), "BSD")],
            notices.Components); // a one-cell row is skipped, and nothing after the first table is read
    }

    [Fact]
    public void The_version_is_the_apps_without_the_source_revision()
    {
        var app = typeof(AboutViewModel).Assembly;
        var version = AboutViewModel.VersionOf(app);

        // The SDK stamps "1.0.0-preview.1+<commit>"; the dialog shows the part before the '+', prerelease label included.
        var informational = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Contains("+", informational, StringComparison.Ordinal);
        Assert.Equal(informational[..informational.IndexOf('+')], version);
        Assert.Equal("1.0.0-preview.1", version);
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
        Assert.Contains("Additional permission under GNU GPL version 3 section 7", about.LicenseText, StringComparison.Ordinal);
        Assert.Contains("TERMS AND CONDITIONS", about.LicenseText, StringComparison.Ordinal);
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
        Assert.Contains(Gui.All<TextBlock>(rows), t => t.Text == components[^1].License);
        Assert.Equal(AppDocuments.NoticesOnline, Gui.Find<HyperlinkButton>(window, "NoticesOnlineLink").NavigateUri);
        Assert.Equal(new Uri("https://github.com/vgabi94/MusicSpin/blob/main/THIRD-PARTY-NOTICES.md"), AppDocuments.NoticesOnline);
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
