using System.Globalization;
using System.Text;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Normalizes OCR text before matching: case, punctuation, apostrophes,
/// whitespace, line breaks and Unicode differences are folded away.
/// Deliberately does NOT blindly rewrite lookalike characters (I/l, O/0);
/// those are handled with reduced-cost scoring in <see cref="StringSimilarity"/>.
/// </summary>
public static class TextNormalizationService
{
    /// <summary>Normalize a single string for comparison (upper-case, letters/digits/spaces only).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Compatibility decomposition + strip diacritics (Léry's → LERYS).
        string folded = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(folded.Length);
        foreach (char c in folded)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c == '\'' || c == '’' || c == '‘' || c == '`')
            {
                // Apostrophes are dropped entirely (Campbell's → CAMPBELLS),
                // never turned into word breaks.
            }
            else if (char.IsWhiteSpace(c) || IsDash(c, cat) || c == '_' || c == '/')
            {
                sb.Append(' ');
            }
            // All other punctuation is dropped.
        }

        // Collapse whitespace.
        string[] parts = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Every Unicode dash is a word break like '-': all dash punctuation (hyphen, en and
    /// em dash, figure dash, ...) plus the minus sign U+2212, which Unicode files under
    /// math symbols but OCR and typography use as a dash ("Police–Station").
    /// </summary>
    private static bool IsDash(char c, UnicodeCategory category) =>
        category == UnicodeCategory.DashPunctuation || c == '\u2212';

    /// <summary>Split raw OCR output into normalized, non-trivial candidate lines.</summary>
    public static IReadOnlyList<string> SplitCandidateLines(string? ocrText)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return result;
        }

        foreach (string raw in ocrText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = Normalize(raw);
            if (line.Length < 3)
            {
                continue;
            }

            // Drop lines that are mostly digits/symbols (timers, pips, percentages).
            int letters = line.Count(char.IsLetter);
            if ((double)letters / line.Length < 0.4)
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// OCR multi-character confusion variants, e.g. "RN" read for "M".
    /// Each variant is scored and the best one wins; nothing is rewritten blindly.
    /// </summary>
    public static IEnumerable<string> ConfusionVariants(string normalized)
    {
        yield return normalized;
        if (normalized.Contains("RN", StringComparison.Ordinal))
        {
            yield return normalized.Replace("RN", "M", StringComparison.Ordinal);
        }

        if (normalized.Contains("VV", StringComparison.Ordinal))
        {
            yield return normalized.Replace("VV", "W", StringComparison.Ordinal);
        }

        if (normalized.Contains("CL", StringComparison.Ordinal))
        {
            yield return normalized.Replace("CL", "D", StringComparison.Ordinal);
        }
    }
}
