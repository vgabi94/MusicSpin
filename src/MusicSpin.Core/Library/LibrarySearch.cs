using System.Globalization;
using System.Text;

namespace MusicSpin.Core.Library;

/// <summary>Which field a search term must match. <see cref="Any"/>: any searchable field.</summary>
public enum SearchField
{
    Any,
    Title,
    Artist,
    Album,
    Genre,
}

/// <summary>One parsed term: folded text (see <see cref="LibrarySearch.Fold"/>), the field it applies to, and whether it was quoted.</summary>
public sealed record SearchTerm(SearchField Field, string Text, bool Phrase);

/// <summary>
/// A parsed search: every term must match (AND). Plain words match anywhere, quoted phrases must appear contiguously in one
/// field, <c>artist:</c>/<c>album:</c>/<c>title:</c>/<c>genre:</c> restrict a word or phrase to one field, <c>year:</c> takes
/// <c>1959</c> or <c>1950-1959</c>, and <c>is:favorite</c> (<c>is:fav</c>) keeps favorite tracks only.
/// </summary>
public sealed record SearchQuery(IReadOnlyList<SearchTerm> Terms, int? YearFrom, int? YearTo, bool Favorite = false)
{
    public bool IsEmpty => Terms.Count == 0 && YearFrom is null && YearTo is null && !Favorite;

    /// <summary>Terms that apply to a field (plain terms apply to all).</summary>
    internal IEnumerable<SearchTerm> For(SearchField field) => Terms.Where(t => t.Field == SearchField.Any || t.Field == field);

    /// <summary>Combines two queries (AND).</summary>
    public SearchQuery And(SearchQuery? other)
        => other is null ? this : new SearchQuery([.. Terms, .. other.Terms],
            Max(YearFrom, other.YearFrom), Min(YearTo, other.YearTo), Favorite || other.Favorite);

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>
/// Search normalization, parsing and matching. Text is folded the same way at scan time (stored keys) and at query time:
/// case-insensitive, diacritics removed (é→e, ñ→n, ü→u), ß→ss, æ→ae, œ→oe, ø→o, ł→l, đ/ð→d, þ→th; apostrophes and dots
/// dropped (Pharaoh's→pharaohs, R.E.M.→rem); every other non-letter/digit is a word break; whitespace collapsed.
/// </summary>
public static class LibrarySearch
{
    public const int MaxQueryLength = 1000;
    public const int MaxTerms = 32;

    /// <summary>Separates the fields inside a stored key; never produced by <see cref="Fold"/>, so phrases cannot span fields.</summary>
    internal const char FieldSeparator = '\u001f';

    private static readonly Dictionary<string, SearchField> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = SearchField.Artist,
        ["album"] = SearchField.Album,
        ["title"] = SearchField.Title,
        ["genre"] = SearchField.Genre,
    };

    /// <summary>Folds text for searching (see the class summary). Null/blank → empty.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = WellFormed(text).Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                || c is '\'' or '\u2019' or '\u2018' or '`' or '\u00b4' or '\u02bc' or '.' or '\u2024')
            {
                continue;
            }

            string? mapped = c switch
            {
                'ß' or 'ẞ' => "ss",
                'æ' or 'Æ' => "ae",
                'œ' or 'Œ' => "oe",
                'ø' or 'Ø' => "o",
                'ł' or 'Ł' => "l",
                'đ' or 'Đ' or 'ð' or 'Ð' => "d",
                'þ' or 'Þ' => "th",
                'ı' => "i",
                _ => null,
            };

            if (mapped is null && !char.IsLetterOrDigit(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            if (mapped is not null)
            {
                sb.Append(mapped);
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        // Recompose what decomposition split without marks (e.g. Hangul syllables).
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Unpaired surrogates (possible in tags) make <see cref="string.Normalize()"/> throw: they become word breaks.</summary>
    private static string WellFormed(string text)
    {
        if (!text.Any(char.IsSurrogate))
        {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    /// <summary>Folds several values into one stored key (fields separated so phrases cannot span them; empty values and duplicates dropped).</summary>
    internal static string Key(params string?[] values)
        => string.Join(FieldSeparator, values.Select(Fold).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal));

    /// <summary>Parses the search syntax.</summary>
    /// <exception cref="MusicSpinException">InvalidArgument: empty, too long, too many terms, nothing searchable, a prefix without a value, a bad year.</exception>
    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid("Search text must not be empty.");
        }

        if (text.Length > MaxQueryLength)
        {
            throw Invalid($"Search text is too long ({text.Length} characters; at most {MaxQueryLength}).");
        }

        var terms = new List<SearchTerm>();
        int? yearFrom = null, yearTo = null;
        var favorite = false;
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            // field prefix: word characters followed by ':'
            var field = SearchField.Any;
            var isYear = false;
            var isIs = false;
            var colon = PrefixEnd(text, i);
            string? prefix = null;
            if (colon > i)
            {
                prefix = text[i..colon];
                if (Prefixes.TryGetValue(prefix, out var f))
                {
                    field = f;
                    i = colon + 1;
                }
                else if (prefix.Equals("year", StringComparison.OrdinalIgnoreCase))
                {
                    isYear = true;
                    i = colon + 1;
                }
                else if (prefix.Equals("is", StringComparison.OrdinalIgnoreCase))
                {
                    isIs = true;
                    i = colon + 1;
                }
                else
                {
                    prefix = null; // unknown "word:" is plain text
                }
            }

            string raw;
            var phrase = false;
            if (i < text.Length && text[i] is '"' or '\u201c' or '\u201d')
            {
                var close = text.IndexOfAny(['"', '\u201c', '\u201d'], i + 1);
                raw = close < 0 ? text[(i + 1)..] : text[(i + 1)..close];
                i = close < 0 ? text.Length : close + 1;
                phrase = true;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                raw = text[start..i];
            }

            if (isIs)
            {
                favorite |= IsValue(raw.Trim(), prefix!);
                continue;
            }

            if (prefix is not null && string.IsNullOrWhiteSpace(raw))
            {
                throw Invalid($"'{prefix}:' needs a value, e.g. {prefix.ToLowerInvariant()}:{(isYear ? "1959" : "blue")} or {prefix.ToLowerInvariant()}:\"kind of blue\".");
            }

            if (isYear)
            {
                (yearFrom, yearTo) = ParseYear(raw.Trim(), yearFrom, yearTo);
                continue;
            }

            var folded = Fold(raw);
            if (folded.Length == 0)
            {
                if (prefix is not null)
                {
                    throw Invalid($"'{prefix}:{raw}' has no letters or digits to search for.");
                }

                continue; // stray punctuation such as "-" or "&"
            }

            if (phrase)
            {
                terms.Add(new SearchTerm(field, folded, folded.Contains(' ', StringComparison.Ordinal)));
            }
            else
            {
                // "AC/DC" folds to two words: both must match (in the field, when prefixed)
                terms.AddRange(folded.Split(' ').Select(w => new SearchTerm(field, w, Phrase: false)));
            }
        }

        if (terms.Count == 0 && yearFrom is null && yearTo is null && !favorite)
        {
            throw Invalid($"'{text.Trim()}' has no letters or digits to search for.");
        }

        if (terms.Count > MaxTerms)
        {
            throw Invalid($"Too many search terms ({terms.Count}; at most {MaxTerms}).");
        }

        return new SearchQuery(terms, yearFrom, yearTo, favorite);
    }

    /// <summary>The values <c>is:</c> takes (all mean a favorite track).</summary>
    public static IReadOnlyList<string> IsValues { get; } = ["favorite", "fav"];

    private static bool IsValue(string raw, string prefix)
        => IsValues.Contains(raw, StringComparer.OrdinalIgnoreCase)
            ? true
            : throw Invalid(raw.Length == 0
                ? $"'{prefix}:' needs a value: is:favorite (or is:fav)."
                : $"Unknown '{prefix}:{raw}': use is:favorite (or is:fav).");

    /// <summary>Words of a per-field "contains" filter (<c>--title</c>, <c>--album-contains</c>, ...), as a query restricted to that field.</summary>
    /// <exception cref="MusicSpinException">InvalidArgument when the value is empty or has no letters/digits.</exception>
    public static SearchQuery Contains(SearchField field, string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{option} needs some text.");
        }

        if (value.Length > MaxQueryLength)
        {
            throw Invalid($"{option} is too long ({value.Length} characters; at most {MaxQueryLength}).");
        }

        var folded = Fold(value);
        return folded.Length == 0
            ? throw Invalid($"{option} '{value.Trim()}' has no letters or digits to search for.")
            : new SearchQuery([.. folded.Split(' ').Select(w => new SearchTerm(field, w, Phrase: false))], null, null);
    }

    /// <summary>Combines optional queries (AND); null when all are null.</summary>
    public static SearchQuery? All(params SearchQuery?[] queries)
        => queries.Where(q => q is not null).Aggregate((SearchQuery?)null, (acc, q) => acc is null ? q : acc.And(q));

    private static int PrefixEnd(string text, int start)
    {
        var j = start;
        while (j < text.Length && char.IsAsciiLetter(text[j]))
        {
            j++;
        }

        return j > start && j < text.Length && text[j] == ':' ? j : -1;
    }

    private static (int? From, int? To) ParseYear(string raw, int? from, int? to)
    {
        var parts = raw.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!TryYear(parts[0], out var a) || (parts.Length == 2 && !TryYear(parts[1], out _)))
        {
            throw Invalid($"Bad year '{raw}': use year:1959 or year:1950-1959.");
        }

        var b = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : a;
        if (a > b)
        {
            throw Invalid($"Bad year range '{raw}': the first year is after the second.");
        }

        return (from is null ? a : Math.Max(from.Value, a), to is null ? b : Math.Min(to.Value, b));
    }

    private static bool TryYear(string s, out int year)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out year) && year is >= 0 and <= 9999;

    private static MusicSpinException Invalid(string message) => new(MusicSpinErrorCode.InvalidArgument, message);

    // ---------------------------------------------------------------- matching and ranking

    /// <summary>Match levels, best first (exact field > field prefix > word start > anywhere).</summary>
    private const int Exact = 4, Prefix = 3, WordStart = 2, Anywhere = 1;

    /// <summary>
    /// Score of a track (null: no match). Keys are the stored folded fields (<see cref="TrackRecord.SearchTitle"/> ...).
    /// <paramref name="favorites"/>: the favorite track ids, read once per search; with <c>is:favorite</c>, a track not in
    /// them (or no set at all) doesn't match.
    /// </summary>
    internal static int? Score(SearchQuery query, TrackRecord t, IReadOnlySet<string>? favorites = null)
        => query.YearFrom is { } from && (t.Year is null || t.Year < from) || query.YearTo is { } to && (t.Year is null || t.Year > to)
           || (query.Favorite && favorites?.Contains(t.Id) != true)
            ? null
            : Score(query, [(SearchField.Title, t.SearchTitle, 3), (SearchField.Artist, t.SearchArtist, 2), (SearchField.Album, t.SearchAlbum, 2),
                (SearchField.Genre, t.SearchGenre, 1), (SearchField.Any, t.SearchFile, 0)]);

    /// <summary>Score of an album; null with <c>is:favorite</c> (only tracks are favorites).</summary>
    internal static int? Score(SearchQuery query, AlbumRecord a)
        => query.Favorite || query.YearFrom is { } from && (a.Year is null || a.Year < from) || query.YearTo is { } to && (a.Year is null || a.Year > to)
            ? null
            : Score(query, [(SearchField.Album, a.SearchTitle, 3), (SearchField.Artist, a.SearchArtist, 2), (SearchField.Genre, a.SearchGenre, 1)]);

    /// <summary>Score of an artist; null with <c>year:</c> or <c>is:favorite</c> (artists have neither).</summary>
    internal static int? Score(SearchQuery query, ArtistRecord a)
        => query.YearFrom is not null || query.YearTo is not null || query.Favorite ? null : Score(query, [(SearchField.Artist, a.SearchName, 3)]);

    /// <summary>
    /// Every term must match one of the fields it applies to (fields of other kinds, e.g. title: on an artist, never match).
    /// Score = sum over terms of the best (level × 4 + field weight), plus 20 when the plain words together equal a whole field.
    /// </summary>
    private static int? Score(SearchQuery query, (SearchField Field, string Key, int Weight)[] fields)
    {
        var score = 0;
        foreach (var term in query.Terms)
        {
            var best = 0;
            foreach (var (field, key, weight) in fields)
            {
                if (term.Field != SearchField.Any && term.Field != field)
                {
                    continue;
                }

                var level = Level(key, term.Text);
                if (level > 0)
                {
                    best = Math.Max(best, level * 4 + weight);
                }
            }

            if (best == 0)
            {
                return null;
            }

            score += best;
        }

        var plain = string.Join(' ', query.Terms.Where(t => t.Field == SearchField.Any).Select(t => t.Text));
        if (plain.Length > 0 && fields.Any(f => Segments(f.Key).Contains(plain, StringComparer.Ordinal)))
        {
            score += 20;
        }

        return score;
    }

    private static int Level(string key, string term)
    {
        if (key.Length == 0)
        {
            return 0;
        }

        var best = 0;
        var index = key.IndexOf(term, StringComparison.Ordinal);
        while (index >= 0)
        {
            var end = index + term.Length;
            var startsSegment = index == 0 || key[index - 1] == FieldSeparator;
            var endsSegment = end == key.Length || key[end] == FieldSeparator;
            var level = startsSegment && endsSegment ? Exact
                : startsSegment ? Prefix
                : key[index - 1] == ' ' ? WordStart
                : Anywhere;
            best = Math.Max(best, level);
            if (best == Exact)
            {
                break;
            }

            index = key.IndexOf(term, index + 1, StringComparison.Ordinal);
        }

        return best;
    }

    private static string[] Segments(string key) => key.Split(FieldSeparator);
}
