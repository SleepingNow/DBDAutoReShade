namespace DBDAutoReShade.Core.Services;

/// <summary>
/// String similarity with reduced substitution cost for common OCR confusions
/// (I↔l↔1, O↔0, S↔5, B↔8, G↔6, Z↔2). Inputs are expected to be normalized
/// (see <see cref="TextNormalizationService"/>); comparison is ordinal.
/// </summary>
public static class StringSimilarity
{
    private static readonly HashSet<(char, char)> ConfusablePairs = BuildConfusablePairs();

    private static HashSet<(char, char)> BuildConfusablePairs()
    {
        string[] groups = { "IL1", "O0", "S5", "B8", "G6", "Z2" };
        var set = new HashSet<(char, char)>();
        foreach (string g in groups)
        {
            for (int i = 0; i < g.Length; i++)
            {
                for (int j = 0; j < g.Length; j++)
                {
                    if (i != j)
                    {
                        set.Add((g[i], g[j]));
                    }
                }
            }
        }

        return set;
    }

    private static double SubstitutionCost(char a, char b)
    {
        if (a == b)
        {
            return 0.0;
        }

        return ConfusablePairs.Contains((a, b)) ? 0.4 : 1.0;
    }

    /// <summary>Weighted Levenshtein distance with cheap OCR-confusion substitutions.</summary>
    public static double WeightedLevenshtein(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        // Ensure b is the shorter string to bound memory.
        if (b.Length > a.Length)
        {
            (a, b) = (b, a);
        }

        var prev = new double[b.Length + 1];
        var curr = new double[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                double del = prev[j] + 1.0;
                double ins = curr[j - 1] + 1.0;
                double sub = prev[j - 1] + SubstitutionCost(a[i - 1], b[j - 1]);
                curr[j] = Math.Min(del, Math.Min(ins, sub));
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>Similarity in [0,1]; 1.0 is an exact match.</summary>
    public static double Similarity(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0)
        {
            return 1.0;
        }

        double distance = WeightedLevenshtein(a, b);
        double sim = 1.0 - distance / max;
        return sim < 0 ? 0 : sim;
    }

    /// <summary>Best similarity across OCR confusion variants of the observed text.</summary>
    public static double BestSimilarity(string observedNormalized, string candidateNormalized)
    {
        double best = 0.0;
        foreach (string variant in TextNormalizationService.ConfusionVariants(observedNormalized))
        {
            best = Math.Max(best, Similarity(variant, candidateNormalized));
        }

        return best;
    }
}
