using System.Globalization;
using System.Text;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Deterministic normalizer for <c>games.normalized_title</c>. Lowercase, diacritics removed, curly
/// apostrophes folded, punctuation dropped, and only <em>trailing</em> edition tokens stripped. It is a
/// comparison/display key: no identity decision may depend on it.
/// </summary>
public static class GameTitleNormalizer
{
    // Multi-word edition suffixes are removed before single-token ones ("game of the year" before
    // trailing "edition"). Apostrophes are dropped, so "director's cut" arrives as "directors cut".
    private static readonly string[][] PhraseSuffixes =
    {
        ["game", "of", "the", "year"],
        ["directors", "cut"]
    };

    private static readonly HashSet<string> EditionTokens = new(StringComparer.Ordinal)
    {
        "edition", "goty", "definitive", "gold", "complete", "enhanced", "remastered",
        "ultimate", "deluxe", "standard"
    };

    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // FormD splits "á" into "a" + combining mark, so dropping NonSpacingMark removes diacritics.
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var mapped = ch switch
            {
                '\u2018' or '\u2019' or '\u02BC' => '\'',
                _ => ch
            };

            if (mapped == '\'')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(mapped) ? char.ToLowerInvariant(mapped) : ' ');
        }

        var tokens = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        StripEditionSuffix(tokens);
        return string.Join(' ', tokens);
    }

    private static void StripEditionSuffix(List<string> tokens)
    {
        var changed = true;
        while (changed && tokens.Count > 0)
        {
            changed = false;

            foreach (var phrase in PhraseSuffixes)
            {
                if (tokens.Count < phrase.Length || !EndsWith(tokens, phrase))
                {
                    continue;
                }

                tokens.RemoveRange(tokens.Count - phrase.Length, phrase.Length);
                changed = true;
                break;
            }

            if (changed)
            {
                continue;
            }

            if (EditionTokens.Contains(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
                changed = true;
            }
        }
    }

    private static bool EndsWith(List<string> tokens, string[] suffix)
    {
        var offset = tokens.Count - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (!string.Equals(tokens[offset + i], suffix[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
