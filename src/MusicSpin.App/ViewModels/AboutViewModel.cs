using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>The About dialog's pages: the app itself, its license, and the third-party notices.</summary>
public enum AboutPage
{
    About,
    License,
    Notices,
}

/// <summary>
/// Help › About Music Spin, License and Third-party notices: one dialog over the window (a layer, so Esc closes it) with
/// three pages. About names the app, its version, who made it and its repository, and links to the other two; they
/// show the documents the executable carries (<see cref="AppDocuments"/>), read the first time they are shown.
/// </summary>
public sealed partial class AboutViewModel : LayerViewModel
{
    public const string AppName = "Music Spin";
    public const string Author = "Gabriel-Eugen Vaduva";
    public const string Summary =
        "A desktop music player for hi-res audio, with a command line that people and AI agents can script. Free and open source under the MIT License.";

    private string? _license;
    private ThirdPartyNotices? _notices;

    public AboutViewModel()
        : this(VersionOf(Assembly.GetEntryAssembly() ?? typeof(AboutViewModel).Assembly))
    {
    }

    public AboutViewModel(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
    }

    public override int Order => LayerOrder.About;

    public string Version { get; }

    public string VersionText => $"Version {Version}";

    public static string Copyright => $"Copyright © 2026 {Author}";

    public static Uri Repository => AppDocuments.Repository;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAboutPage), nameof(IsLicensePage), nameof(IsNoticesPage), nameof(Title))]
    public partial AboutPage Page { get; private set; }

    public bool IsAboutPage => Page == AboutPage.About;

    public bool IsLicensePage => Page == AboutPage.License;

    public bool IsNoticesPage => Page == AboutPage.Notices;

    public string Title => Page switch
    {
        AboutPage.License => "License",
        AboutPage.Notices => "Third-party notices",
        _ => $"About {AppName}",
    };

    /// <summary>The MIT License as the executable carries it.</summary>
    public string LicenseText => _license ??= AppDocuments.License();

    /// <summary>What the executable contains or loads, with each component's license.</summary>
    public ThirdPartyNotices Notices => _notices ??= AppDocuments.Notices();

    /// <summary>Help › About Music Spin.</summary>
    [RelayCommand]
    private void ShowAbout() => Show(AboutPage.About);

    /// <summary>Help › License, or License on the About page.</summary>
    [RelayCommand]
    private void ShowLicense() => Show(AboutPage.License);

    /// <summary>Help › Third-party notices, or the link on the About page.</summary>
    [RelayCommand]
    private void ShowNotices() => Show(AboutPage.Notices);

    /// <summary>From a document back to the About page.</summary>
    [RelayCommand]
    private void Back() => Page = AboutPage.About;

    private void Show(AboutPage page)
    {
        Page = page;
        Open();
    }

    /// <summary>The version the build stamped, without the source revision the SDK appends after a '+'.</summary>
    public static string VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational?.IndexOf('+', StringComparison.Ordinal) is int plus and >= 0)
        {
            informational = informational[..plus];
        }

        return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString(3) ?? "unknown" : informational;
    }
}
