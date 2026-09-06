using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Confidence-based fuzzy matcher: OCR lines → known map/realm.
/// False positives are worse than recognition failures, so ambiguous or
/// low-confidence input yields failure ("remain on Global, do not guess").
/// </summary>
public sealed class MapMatchingService
{
    /// <summary>Minimum similarity to accept a map-name hit.</summary>
    public double MapAcceptThreshold { get; init; } = 0.78;

    /// <summary>Minimum similarity to accept a realm-name-only hit (stricter: shorter strings collide more).</summary>
    public double RealmAcceptThreshold { get; init; } = 0.85;

    /// <summary>Top-two margin below which a map hit is considered ambiguous.</summary>
    public double AmbiguityMargin { get; init; } = 0.06;

    private sealed record Candidate(string Text, string Realm, double Score);

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

        // Best map hits: keep the top-two distinct maps per line so a close
        // runner-up is visible to the ambiguity check below.
        var mapHits = new List<(Candidate Hit, string Line)>();
        foreach (string line in lines)
        {
            Candidate? best = null;
            Candidate? second = null;
            foreach (var m in normalizedMaps)
            {
                double score = StringSimilarity.BestSimilarity(line, m.Normalized);
                var candidate = new Candidate(m.Map, m.Realm, score);
                if (best is null || score > best.Score)
                {
                    if (best is not null && !string.Equals(best.Text, candidate.Text, StringComparison.Ordinal))
                    {
                        second = best;
                    }

                    best = candidate;
                }
                else if (!string.Equals(best.Text, candidate.Text, StringComparison.Ordinal) &&
                         (second is null || score > second.Score))
                {
                    second = candidate;
                }
            }

            if (best is not null)
            {
                mapHits.Add((best, line));
                if (second is not null)
                {
                    mapHits.Add((second, line));
                }
            }
        }

        // Best realm hit across all lines.
        (string Realm, double Score, string Line)? realmHit = null;
        foreach (string line in lines)
        {
            foreach (var r in normalizedRealms)
            {
                double score = StringSimilarity.BestSimilarity(line, r.Normalized);
                if (realmHit is null || score > realmHit.Value.Score)
                {
                    realmHit = (r.Entry.Name, score, line);
                }
            }
        }

        var orderedMapHits = mapHits.OrderByDescending(h => h.Hit.Score).ToList();
        if (orderedMapHits.Count > 0)
        {
            var top = orderedMapHits[0];
            if (top.Hit.Score >= MapAcceptThreshold)
            {
                // Ambiguity check: a close runner-up for a DIFFERENT map.
                var runnerUp = orderedMapHits
                    .Where(h => !string.Equals(h.Hit.Text, top.Hit.Text, StringComparison.Ordinal))
                    .Select(h => h.Hit)
                    .OrderByDescending(h => h.Score)
                    .FirstOrDefault();

                if (runnerUp is null || top.Hit.Score - runnerUp.Score >= AmbiguityMargin)
                {
                    return new MapMatchResult(true, top.Hit.Text, top.Hit.Realm, Round(top.Hit.Score),
                        MatchMethod.MapName, $"line '{top.Line}'");
                }

                // Top-two maps agree on the realm: the preset is still unambiguous.
                if (string.Equals(runnerUp.Realm, top.Hit.Realm, StringComparison.OrdinalIgnoreCase))
                {
                    double conf = Math.Min(top.Hit.Score, runnerUp.Score) * 0.95;
                    return new MapMatchResult(true, null, top.Hit.Realm, Round(conf),
                        MatchMethod.RealmName, $"ambiguous maps, agreed realm '{top.Hit.Realm}'");
                }

                return MapMatchResult.Failure(
                    $"Ambiguous: '{top.Hit.Text}' vs '{runnerUp.Text}'. Not guessing.");
            }
        }

        if (realmHit is { Score: var s } rh && s >= RealmAcceptThreshold)
        {
            return new MapMatchResult(true, null, rh.Realm, Round(s * 0.95),
                MatchMethod.RealmName, $"realm line '{rh.Line}'");
        }

        string bestSeen = orderedMapHits.Count > 0
            ? $"'{orderedMapHits[0].Hit.Text}' {orderedMapHits[0].Hit.Score:P0}"
            : "nothing";
        return MapMatchResult.Failure($"No confident match (best: {bestSeen}).");
    }

    private static double Round(double value) => Math.Round(Math.Min(1.0, value), 4);
}
