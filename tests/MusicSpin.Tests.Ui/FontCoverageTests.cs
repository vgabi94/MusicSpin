using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The app draws its text in the bundled Inter on every OS. A character Inter lacks falls back to a system font, and
/// Linux CI has none for some of them (the mockup's "＋" drew as a box there), so a symbol Inter can't draw is an icon
/// (Icons/IconsResource.axaml), or allowed below when the usual fallback fonts of all three OSes have it. This checks
/// every non-ASCII character in the views' text and the app's strings.
/// </summary>
public sealed partial class FontCoverageTests
{
    /// <summary>View attributes that show text.</summary>
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Watermark", "PlaceholderText", "Tip", "Header", "Title",
    };

    /// <summary>Characters Inter lacks that the app shows anyway, each with its reason.</summary>
    private static readonly Dictionary<Rune, string> Allowed = new()
    {
        [new Rune('♪')] = "Now Playing's current track; Segoe UI Symbol, Apple's fonts and DejaVu Sans all have it.",
        [new Rune('⅔')] = "Settings' 16⅔ rpm; Segoe UI, Apple's fonts and DejaVu Sans all have it.",
    };

    [AvaloniaFact]
    public void Inter_draws_every_character_the_app_shows()
    {
        var missing = AppCharacters()
            .Where(c => !InterHas(c.Rune) && !Allowed.ContainsKey(c.Rune))
            .Select(c => $"U+{c.Rune.Value:X4} '{c.Rune}' in {c.Where}")
            .ToList();

        Assert.True(missing.Count == 0, $"Inter has no glyph for these; use an icon instead:\n{string.Join("\n", missing)}");
    }

    [AvaloniaFact]
    public void The_guard_finds_the_apps_text_and_catches_a_character_Inter_lacks()
    {
        Assert.False(InterHas(new Rune('＋')));
        Assert.True(InterHas(new Rune('·')));
        Assert.True(InterHas(new Rune('…')));
        Assert.Contains(AppCharacters(), c => c.Rune == new Rune('·'));
        Assert.All(Allowed.Keys, r => Assert.False(InterHas(r), $"{r} is in Inter now; drop it from Allowed."));
        Assert.Equal(["＋"], [.. NonAscii("Text=\"x\" a \"or use ＋ here\" // \"✓\"", CSharpString()).Select(r => r.ToString())]);
    }

    private static bool InterHas(Rune rune)
        => FontManager.Current.TryGetGlyphTypeface(new Typeface("fonts:Inter#Inter"), out var inter)
           && inter.CharacterToGlyphMap.ContainsGlyph(rune.Value);

    /// <summary>The non-ASCII characters in the views' text attributes and in the string literals of the app's C#.</summary>
    private static List<(Rune Rune, string Where)> AppCharacters()
    {
        var app = AppDirectory();
        var found = new List<(Rune, string)>();
        foreach (var file in SourceFiles(app, "*.axaml").Where(f => !Relative(app, f).StartsWith("Styles", StringComparison.Ordinal)))
        {
            var texts = XDocument.Load(file).Descendants().SelectMany(e => e.Attributes())
                .Where(a => TextAttributes.Contains(a.Name.LocalName.Split('.')[^1]) && !a.Value.TrimStart().StartsWith('{'))
                .Select(a => a.Value);
            found.AddRange(texts.SelectMany(t => t.EnumerateRunes()).Where(r => r.Value > 0x7F).Select(r => (r, Relative(app, file))));
        }

        foreach (var file in SourceFiles(app, "*.cs"))
        {
            found.AddRange(NonAscii(File.ReadAllText(file), CSharpString()).Select(r => (r, Relative(app, file))));
        }

        return [.. found.Distinct()];
    }

    /// <summary>The non-ASCII characters inside the quoted strings of <paramref name="code"/>, leaving out comments.</summary>
    private static IEnumerable<Rune> NonAscii(string code, Regex strings)
        => code.Split('\n')
            .Select(line => line.Contains("//", StringComparison.Ordinal) ? line[..line.IndexOf("//", StringComparison.Ordinal)] : line)
            .SelectMany(line => strings.Matches(line))
            .SelectMany(m => m.Value.EnumerateRunes())
            .Where(r => r.Value > 0x7F);

    private static IEnumerable<string> SourceFiles(string app, string pattern)
        => Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories)
            .Where(f => !Relative(app, f).Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj"));

    private static string Relative(string app, string file) => Path.GetRelativePath(app, file);

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

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex CSharpString();
}
