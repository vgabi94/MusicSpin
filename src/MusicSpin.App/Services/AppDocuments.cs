using System.Text;
using System.Text.RegularExpressions;

namespace MusicSpin.App.Services;

/// <summary>One component of the third-party notices: its name (and home page), its license, and how it ships.</summary>
public sealed record ThirdPartyComponent(string Name, Uri? Link, string License, string Ships);

/// <summary>A section of prose after the components (the LGPL notes): its title and paragraphs, list items marked •.</summary>
public sealed record NoticeSection(string Title, IReadOnlyList<string> Paragraphs);

/// <summary>THIRD-PARTY-NOTICES.md as the About dialog shows it.</summary>
public sealed record ThirdPartyNotices(string Intro, IReadOnlyList<ThirdPartyComponent> Components, IReadOnlyList<NoticeSection> Sections);

/// <summary>
/// The documents the app is obliged to carry (Help › License and Third-party notices): <c>LICENSE</c> and
/// <c>THIRD-PARTY-NOTICES.md</c>, embedded in the executable so a published single file still has them. The notices are
/// Markdown; the dialog shows them as plain text, the table as one entry a component.
/// </summary>
public static partial class AppDocuments
{
    public const string LicenseResource = "LICENSE";
    public const string NoticesResource = "THIRD-PARTY-NOTICES.md";

    /// <summary>Where the documents are published with the source, for a build that lacks them.</summary>
    public static readonly Uri Repository = new("https://github.com/vgabi94/MusicSpin");

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

    /// <summary>The license text, or a note pointing at the repository.</summary>
    public static string License() => Read(LicenseResource) ?? Missing(LicenseResource);

    /// <summary>The third-party notices, or a note pointing at the repository.</summary>
    public static ThirdPartyNotices Notices() => Read(NoticesResource) is { } markdown ? ParseNotices(markdown) : new ThirdPartyNotices(Missing(NoticesResource), [], []);

    /// <summary>
    /// The notices' parts: the first paragraph, the table's rows (a row of three cells: the component, its license, how
    /// it ships; the header and its rule are skipped), and each <c>##</c> section after it.
    /// </summary>
    public static ThirdPartyNotices ParseNotices(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var intro = new List<string>();
        var components = new List<ThirdPartyComponent>();
        var sections = new List<NoticeSection>();
        string? title = null;
        var paragraphs = new List<string>();
        var paragraph = new StringBuilder();
        var header = true;

        void EndParagraph()
        {
            if (paragraph.Length > 0)
            {
                (title is null ? intro : paragraphs).Add(Plain(paragraph.ToString()));
                paragraph.Clear();
            }
        }

        void EndSection()
        {
            EndParagraph();
            if (title is not null)
            {
                sections.Add(new NoticeSection(title, [.. paragraphs]));
                paragraphs.Clear();
            }
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('|'))
            {
                EndParagraph();
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (header || cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
                {
                    header = false; // the header row, then its rule
                    continue;
                }

                if (cells.Length >= 3)
                {
                    var link = LinkPattern().Match(cells[0]);
                    var url = link.Success && Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out var u) ? u : null;
                    components.Add(new ThirdPartyComponent(Plain(cells[0]), url, Plain(cells[1]), Plain(string.Join(" | ", cells[2..]))));
                }

                continue;
            }

            header = true; // a later table has its own header
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                EndSection();
                title = Plain(line[3..]);
            }
            else if (line.StartsWith('#'))
            {
                EndParagraph(); // the document's own title
            }
            else if (line.Length == 0)
            {
                EndParagraph();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                EndParagraph();
                paragraph.Append("• ").Append(line[2..]);
            }
            else
            {
                paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(line);
            }
        }

        EndSection();
        return new ThirdPartyNotices(string.Join(Environment.NewLine + Environment.NewLine, intro), components, sections);
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
