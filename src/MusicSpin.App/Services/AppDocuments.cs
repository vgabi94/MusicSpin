using System.Text;
using System.Text.RegularExpressions;

namespace MusicSpin.App.Services;

/// <summary>One component of the third-party notices: its name (and home page) and its license.</summary>
public sealed record ThirdPartyComponent(string Name, Uri? Link, string License);

/// <summary>
/// THIRD-PARTY-NOTICES.md as the About dialog shows it: the first paragraph and a line a component. How each one ships
/// and the LGPL notes stay in the file, which the dialog links to.
/// </summary>
public sealed record ThirdPartyNotices(string Intro, IReadOnlyList<ThirdPartyComponent> Components);

/// <summary>
/// The documents the app is obliged to carry (Help › License and Third-party notices): <c>LICENSE</c> (the GNU GPL
/// version 3), <c>LICENSE-EXCEPTION.txt</c> (the copyright notice and the additional permission for Svg.Custom) and
/// <c>THIRD-PARTY-NOTICES.md</c>, embedded in the executable so a published single file still has them. The notices are
/// Markdown; the dialog shows the notices' first paragraph and a line a component, and links to the whole file.
/// </summary>
public static partial class AppDocuments
{
    public const string LicenseResource = "LICENSE";
    public const string ExceptionResource = "LICENSE-EXCEPTION.txt";
    public const string NoticesResource = "THIRD-PARTY-NOTICES.md";

    /// <summary>Where the documents are published with the source, for a build that lacks them.</summary>
    public static readonly Uri Repository = new("https://github.com/vgabi94/MusicSpin");

    /// <summary>The whole notices file online: how each component ships, and the LGPL notes.</summary>
    public static readonly Uri NoticesOnline = new(Repository + "/blob/main/" + NoticesResource);

    /// <summary>The embedded document <paramref name="resource"/>, or null when this build doesn't have it.</summary>
    public static string? Read(string resource)
    {
        using var stream = typeof(AppDocuments).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>What the dialog says in place of a document this build lacks.</summary>
    public static string Missing(string resource) => $"{resource} is missing from this build. It is published with the source at {Repository}.";

    /// <summary>The copyright notice and the additional permission, then the GPL; a note for either this build lacks.</summary>
    public static string License() => License(Read);

    /// <summary>The License page's text from <paramref name="read"/> (a resource's text, or null when it is missing).</summary>
    public static string License(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var exception = read(ExceptionResource) ?? Missing(ExceptionResource);
        var license = read(LicenseResource) ?? Missing(LicenseResource);
        return exception.TrimEnd() + "\n\n\n" + license;
    }

    /// <summary>The third-party notices, or a note pointing at the repository.</summary>
    public static ThirdPartyNotices Notices() => Read(NoticesResource) is { } markdown ? ParseNotices(markdown) : new ThirdPartyNotices(Missing(NoticesResource), []);

    /// <summary>
    /// The notices' first paragraph and the first table's rows: a row's first cell is the component (its first link its
    /// home page), its second the license. The header and its rule are skipped, and so is a row of fewer than two cells.
    /// </summary>
    public static ThirdPartyNotices ParseNotices(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var intro = new StringBuilder();
        var introDone = false;
        var components = new List<ThirdPartyComponent>();
        var rows = 0;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('|'))
            {
                if (rows++ < 2)
                {
                    continue; // the header row, then its rule
                }

                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length >= 2)
                {
                    var link = LinkPattern().Match(cells[0]);
                    var url = link.Success && Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out var u) ? u : null;
                    components.Add(new ThirdPartyComponent(Plain(cells[0]), url, Plain(cells[1])));
                }
            }
            else if (rows > 0)
            {
                break; // past the table: how each component ships and the LGPL notes stay in the file
            }
            else if (line.Length == 0 || line.StartsWith('#'))
            {
                introDone |= intro.Length > 0; // a blank line or a heading ends the first paragraph
            }
            else if (!introDone)
            {
                intro.Append(intro.Length > 0 ? " " : string.Empty).Append(line);
            }
        }

        return new ThirdPartyNotices(Plain(intro.ToString()), components);
    }

    /// <summary>Markdown's links, code and emphasis as plain text: <c>[FFmpeg](https://…)</c> is "FFmpeg".</summary>
    public static string Plain(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = LinkPattern().Replace(markdown, "$1");
        text = text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        return text.Trim();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkPattern();
}
