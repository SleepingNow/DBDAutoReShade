using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Confidence-based fuzzy matcher: OCR lines → known map/realm.
/// False positives are worse than recognition failures, so ambiguous or
/// low-confidence input yields failure ("remain on Global, do not guess").
/// </summary>
public sealed class MapMatchingService
{
    /// <summary>Scores within this of the required margin count as meeting it (floating-point noise).</summary>
    private const double MarginTolerance = 1e-9;

    /// <summary>Confidence penalty for a realm-only result (no specific map was identified).</summary>
    private const double RealmOnlyPenalty = 0.95;

    /// <summary>Minimum similarity to accept a map-name hit.</summary>
    public double MapAcceptThreshold { get; init; } = 0.78;

    /// <summary>Minimum similarity to accept a realm-name-only hit (stricter: shorter strings collide more).</summary>
    public double RealmAcceptThreshold { get; init; } = 0.85;

    /// <summary>
    /// Top-vs-runner-up margin below which a map hit is considered ambiguous. Maps of
    /// DIFFERENT realms always need this full margin (a wrong pick activates the wrong
    /// preset). Maps of the SAME realm need at most half of their own name gap, so
    /// near-identical names such as the two Raccoon City Police Station wings can still
    /// resolve to a specific map.
    /// </summary>
    public double AmbiguityMargin { get; init; } = 0.06;

    private sealed record MapHit(string Map, string Realm, string Normalized, double Score, string Line);

    private sealed record RealmHit(string Realm, double Score, string Line);

    public MapMatchResult Match(string? ocrText, IReadOnlyList<RealmEntry> realms)
    {
        if (realms is null || realms.Count == 0)
        {
            return MapMatchResult.Failure("Map database is empty.");
        }

        var lines = TextNormalizationService.SplitCandidateLines(ocrText);
        if (lines.Count == 0)
        {
            return MapMatchResult.Failure("No readable text.");
        }

        var normalizedRealms = realms
            .Select(r => (Entry: r, Normalized: TextNormalizationService.Normalize(r.Name)))
            .Where(x => x.Normalized.Length > 0)
            .ToList();

        var normalizedMaps = realms
            .SelectMany(r => r.Maps.Select(m => (Map: m, Realm: r.Name, Normalized: TextNormalizationService.Normalize(m))))
            .Where(x => x.Normalized.Length > 0)
            .ToList();

        // Every map's best line, so every close competitor is visible to the
        // ambiguity check below (not only the top two of each line).
        var mapHits = new List<MapHit>(normalizedMaps.Count);
        foreach (var m in normalizedMaps)
        {
            MapHit? best = null;
            foreach (string line in lines)
            {
                double score = StringSimilarity.BestSimilarity(line, m.Normalized);
                if (best is null || score > best.Score)
                {
                    best = new MapHit(m.Map, m.Realm, m.Normalized, score, line);
                }
            }

            if (best is not null)
            {
                mapHits.Add(best);
            }
        }

        // Every realm's best line: the realm line cross-checks the map line.
        var realmHits = new List<RealmHit>(normalizedRealms.Count);
        foreach (var r in normalizedRealms)
        {
            RealmHit? best = null;
            foreach (string line in lines)
            {
                double score = StringSimilarity.BestSimilarity(line, r.Normalized);
                if (best is null || score > best.Score)
                {
                    best = new RealmHit(r.Entry.Name, score, line);
                }
            }

            if (best is not null)
            {
                realmHits.Add(best);
            }
        }

        // MaxBy keeps the earliest element on a tie (database order).
        MapHit? top = mapHits.MaxBy(h => h.Score);
        if (top is not null && top.Score >= MapAcceptThreshold)
        {
            return ResolveMapHit(top, mapHits, realmHits);
        }

        RealmHit? realmHit = realmHits.MaxBy(h => h.Score);
        if (realmHit is not null && realmHit.Score >= RealmAcceptThreshold)
        {
            return new MapMatchResult(true, null, realmHit.Realm, Round(realmHit.Score * RealmOnlyPenalty),
                MatchMethod.RealmName, $"realm line '{realmHit.Line}'");
        }

        string bestSeen = top is not null
            ? $"'{top.Map}' {top.Score:P0}"
            : "nothing";
        return MapMatchResult.Failure($"No confident match (best: {bestSeen}).");
    }

    /// <summary>
    /// Margin the top map hit must keep over <paramref name="other"/>. Across realms it is the
    /// full <see cref="AmbiguityMargin"/>. Within one realm the preset is settled either way, so
    /// the read only has to be closer to one name than to the midpoint between the two names:
    /// <c>min(AmbiguityMargin, 0.5 * (1 - Similarity(top, other)))</c>. Without this, two names
    /// that differ by less than the margin (East vs West Wing: 2/37 ≈ 0.054) could never resolve.
    /// </summary>
    private double RequiredMargin(MapHit top, MapHit other)
    {
        if (!SameRealm(top.Realm, other.Realm))
        {
            return AmbiguityMargin;
        }

        double nameGap = 1.0 - StringSimilarity.Similarity(top.Normalized, other.Normalized);
        return Math.Min(AmbiguityMargin, 0.5 * nameGap);
    }

    private MapMatchResult ResolveMapHit(MapHit top, IReadOnlyList<MapHit> mapHits, IReadOnlyList<RealmHit> realmHits)
    {
        // Competitors that come closer to the top hit than their pair's required margin.
        var conflicts = mapHits
            .Where(h => !ReferenceEquals(h, top) && top.Score - h.Score < RequiredMargin(top, h) - MarginTolerance)
            .OrderByDescending(h => h.Score)
            .ToList();

        MapHit? crossRealm = conflicts.FirstOrDefault(h => !SameRealm(h.Realm, top.Realm));
        if (crossRealm is not null)
        {
            // The map line alone cannot pick a realm; the realm line may settle it.
            var candidateRealms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { top.Realm };
            foreach (MapHit h in conflicts)
            {
                candidateRealms.Add(h.Realm);
            }

            RealmHit? settled = SettleByRealmLine(candidateRealms, realmHits);
            if (settled is not null)
            {
                return new MapMatchResult(true, null, settled.Realm, Round(settled.Score * RealmOnlyPenalty),
                    MatchMethod.RealmName,
                    $"ambiguous maps '{top.Map}' vs '{crossRealm.Map}', settled by realm line '{settled.Line}'");
            }

            return MapMatchResult.Failure(
                $"Ambiguous: '{top.Map}' vs '{crossRealm.Map}'. Not guessing.");
        }

        // The map hit names realm X; a confident realm line naming another realm that
        // reads even better than the map contradicts it.
        RealmHit? otherRealm = realmHits.Where(r => !SameRealm(r.Realm, top.Realm)).MaxBy(r => r.Score);
        if (otherRealm is not null && otherRealm.Score >= RealmAcceptThreshold && otherRealm.Score > top.Score)
        {
            return MapMatchResult.Failure(
                $"Map and realm lines disagree: '{top.Map}' ({top.Realm}, {top.Score:P0}) vs realm line " +
                $"'{otherRealm.Line}' ({otherRealm.Realm}, {otherRealm.Score:P0}). Not guessing.");
        }

        if (conflicts.Count == 0)
        {
            return new MapMatchResult(true, top.Map, top.Realm, Round(top.Score),
                MatchMethod.MapName, $"line '{top.Line}'");
        }

        // Top maps agree on the realm: the preset is still unambiguous, but the
        // confidence is that of the weaker reading and may be below acceptance.
        MapHit runnerUp = conflicts[0];
        double conf = Round(Math.Min(top.Score, runnerUp.Score) * RealmOnlyPenalty);
        string diagnostic = $"ambiguous maps '{top.Map}' vs '{runnerUp.Map}', agreed realm '{top.Realm}'";
        if (conf < MapAcceptThreshold)
        {
            diagnostic += $"; weak: {conf:P0} is below the {MapAcceptThreshold:P0} accept threshold";
        }

        return new MapMatchResult(true, null, top.Realm, conf, MatchMethod.RealmName, diagnostic);
    }

    /// <summary>
    /// The realm line settles a cross-realm map ambiguity only when its best hit is confident,
    /// names one of the candidate realms, and no other candidate realm is also confidently read.
    /// </summary>
    private RealmHit? SettleByRealmLine(IReadOnlySet<string> candidateRealms, IReadOnlyList<RealmHit> realmHits)
    {
        RealmHit? best = realmHits.MaxBy(r => r.Score);
        if (best is null || best.Score < RealmAcceptThreshold || !candidateRealms.Contains(best.Realm))
        {
            return null;
        }

        bool anotherCandidateConfident = realmHits.Any(r =>
            !SameRealm(r.Realm, best.Realm) && candidateRealms.Contains(r.Realm) && r.Score >= RealmAcceptThreshold);
        return anotherCandidateConfident ? null : best;
    }

    private static bool SameRealm(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static double Round(double value) => Math.Round(Math.Min(1.0, value), 4);
}
