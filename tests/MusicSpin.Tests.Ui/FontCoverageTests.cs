using System.Text;
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
public sealed class FontCoverageTests
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
        Assert.Equal(["＋", "♪", "✕", "→"], [.. Literals(CodeSample).SelectMany(NonAscii).Select(r => r.ToString())]);
        Assert.Equal(["＋"], [.. ViewTexts(XDocument.Parse(ViewSample)).SelectMany(NonAscii).Select(r => r.ToString())]);
    }

    /// <summary>
    /// Strings, a char, a verbatim string with a doubled quote, and a URL whose "//" is not a comment; the comments' ✓
    /// and ✗ are left out.
    /// </summary>
    private const string CodeSample = """
        var a = "or use ＋ here"; // "✓"
        var b = '♪'; /* "✗" */
        var c = @"say ""✕"" ";
        var d = "https://example.com → there";
        """;

    private const string ViewSample = """
        <StackPanel xmlns="https://github.com/avaloniaui">
          <!-- ✓ a comment -->
          <TextBlock>Drag here <Run>or use ＋</Run></TextBlock>
          <TextBlock Text="{Binding Title}" />
        </StackPanel>
        """;

    private static bool InterHas(Rune rune)
        => FontManager.Current.TryGetGlyphTypeface(new Typeface("fonts:Inter#Inter"), out var inter)
           && inter.CharacterToGlyphMap.ContainsGlyph(rune.Value);

    /// <summary>The non-ASCII characters in the views' text and in the string and char literals of the app's C#.</summary>
    private static List<(Rune Rune, string Where)> AppCharacters()
    {
        var app = AppDirectory();
        var found = new List<(Rune, string)>();
        foreach (var file in SourceFiles(app, "*.axaml").Where(f => !Relative(app, f).StartsWith("Styles", StringComparison.Ordinal)))
        {
            found.AddRange(ViewTexts(XDocument.Load(file)).SelectMany(NonAscii).Select(r => (r, Relative(app, file))));
        }

        foreach (var file in SourceFiles(app, "*.cs"))
        {
            found.AddRange(Literals(File.ReadAllText(file)).SelectMany(NonAscii).Select(r => (r, Relative(app, file))));
        }

        return [.. found.Distinct()];
    }

    /// <summary>A view's text: its text attributes (not bindings) and the text inside elements (not comments).</summary>
    private static IEnumerable<string> ViewTexts(XDocument view)
        => view.Descendants().SelectMany(e => e.Attributes())
            .Where(a => TextAttributes.Contains(a.Name.LocalName.Split('.')[^1]) && !a.Value.TrimStart().StartsWith('{'))
            .Select(a => a.Value)
            .Concat(view.DescendantNodes().OfType<XText>().Select(t => t.Value));

    private static IEnumerable<Rune> NonAscii(string text) => text.EnumerateRunes().Where(r => r.Value > 0x7F);

    /// <summary>
    /// The string and char literals of C# <paramref name="code"/>, comments left out: "…" (with escapes), @"…" (with
    /// doubled quotes), """…""" and '…'. Interpolations stay inside their string, which is close enough here.
    /// </summary>
    private static IEnumerable<string> Literals(string code)
    {
        var i = 0;
        while (i < code.Length)
        {
            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                i = code.IndexOf('\n', i) is var end and >= 0 ? end : code.Length;
            }
            else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i = code.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : code.Length;
            }
            else if (string.CompareOrdinal(code, i, "\"\"\"", 0, 3) == 0 && (i == 0 || code[i - 1] != '@'))
            {
                var end = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end;
                yield return code[(i + 3)..end];
                i = Math.Min(code.Length, end + 3);
            }
            else if (code[i] is '"' or '\'')
            {
                var quote = code[i];
                var verbatim = quote == '"' && i > 0 && (code[i - 1] == '@' || (code[i - 1] == '$' && i > 1 && code[i - 2] == '@'));
                var start = ++i;
                while (i < code.Length && code[i] != '\n')
                {
                    if (!verbatim && code[i] == '\\')
                    {
                        i += 2;
                    }
                    else if (code[i] == quote && verbatim && i + 1 < code.Length && code[i + 1] == quote)
                    {
                        i += 2;
                    }
                    else if (code[i] == quote)
                    {
                        break;
                    }
                    else
                    {
                        i++;
                    }
                }

                yield return code[start..Math.Min(i, code.Length)];
                i++;
            }
            else
            {
                i++;
            }
        }
    }

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
}
