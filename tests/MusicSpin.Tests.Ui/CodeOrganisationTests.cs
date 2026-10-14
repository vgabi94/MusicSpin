using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Guards the app's code organisation (docs/architecture.md, "Desktop app"): views outside Styles/ hold structure and
/// bindings only. Colours, sizes, spacing, radii, opacity and durations come from Styles/.
/// </summary>
public sealed partial class CodeOrganisationTests
{
    /// <summary>Attributes that must not carry a literal value in a view.</summary>
    private static readonly HashSet<string> SizedAttributes = new(StringComparer.Ordinal)
    {
        "FontSize", "Margin", "Padding", "CornerRadius", "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
        "Opacity", "Spacing", "ColumnSpacing", "RowSpacing", "Duration", "Delay", "BorderThickness", "LetterSpacing", "LineHeight",
    };

    private static readonly HashSet<string> BrushAttributes = new(StringComparer.Ordinal)
    {
        "Background", "Foreground", "Fill", "Stroke", "BorderBrush", "Color", "CaretBrush", "SelectionBrush",
    };

    /// <summary>Allowed literals, each with its reason.</summary>
    private static readonly (string Element, string Attribute, Regex Value, string Reason)[] Exceptions =
    [
        ("ColumnDefinition", "Width", StarOrAuto(), "Grid structure: proportions, not sizes."),
        ("RowDefinition", "Height", StarOrAuto(), "Grid structure: proportions, not sizes."),
    ];

    public static TheoryData<string> Views() => [.. ViewFiles().Select(f => Path.GetRelativePath(AppDirectory(), f))];

    [Theory]
    [MemberData(nameof(Views))]
    public void Views_have_no_hard_coded_colours_or_sizes(string view)
    {
        var problems = new List<string>();
        foreach (var element in XDocument.Load(Path.Combine(AppDirectory(), view), LoadOptions.SetLineInfo).Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName.Split('.')[^1];
                var value = attribute.Value.Trim();
                if (value.StartsWith('{') || attribute.IsNamespaceDeclaration)
                {
                    continue; // a resource, a binding, or a markup extension
                }

                var line = ((System.Xml.IXmlLineInfo)attribute).LineNumber;
                if (ColourLiteral().IsMatch(value) || (BrushAttributes.Contains(name) && value.Length > 0))
                {
                    problems.Add($"line {line}: {element.Name.LocalName}.{name}=\"{value}\" is a colour; use a token from Styles/Tokens.axaml.");
                }
                else if (SizedAttributes.Contains(name)
                         && !Exceptions.Any(e => e.Element == element.Name.LocalName && e.Attribute == name && e.Value.IsMatch(value)))
                {
                    problems.Add($"line {line}: {element.Name.LocalName}.{name}=\"{value}\" is hard-coded; use Styles/Metrics.axaml or Motion.axaml.");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{view}:\n{string.Join("\n", problems)}");
    }

    [Fact]
    public void The_guard_finds_the_views_and_catches_literals()
    {
        Assert.Contains(Views(), v => v.Data.EndsWith("MainWindow.axaml", StringComparison.Ordinal));
        Assert.DoesNotContain(Views(), v => v.Data.StartsWith("Styles", StringComparison.Ordinal));
        Assert.Matches(ColourLiteral(), "#E86A3C");
        Assert.Matches(ColourLiteral(), "#80FFFFFF");
        Assert.DoesNotMatch(ColourLiteral(), "Icon.Play");
    }

    private static IEnumerable<string> ViewFiles()
        => Directory.EnumerateFiles(AppDirectory(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(AppDirectory(), f).StartsWith("Styles", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string AppDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "src", "MusicSpin.App");
            if (Directory.Exists(app))
            {
                return app;
            }
        }

        throw new InvalidOperationException("src/MusicSpin.App not found above the test output.");
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex ColourLiteral();

    [GeneratedRegex(@"^(Auto|\d*(\.\d+)?\*)$")]
    private static partial Regex StarOrAuto();
}
