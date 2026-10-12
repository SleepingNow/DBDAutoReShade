using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Pure aggregation of per-frame match samples (no I/O, no timers), used by
/// the detection orchestrator in Desktop for single-shot and continuous scans.
/// Only the most recent <see cref="DetectionPolicy.WindowSamples"/> samples
/// count, so reads from long ago expire. Within that window:
/// <list type="bullet">
/// <item><description>Agreement: a realm confirms when it has at least
/// <see cref="DetectionPolicy.MinAgreeingSamples"/> accepted samples AND
/// strictly more accepted samples than any other realm (contradiction veto).</description></item>
/// <item><description>Fast path: a single sample at or above
/// <see cref="DetectionPolicy.ConfirmConfidence"/> confirms immediately, but
/// only when no other realm has an accepted sample in the window.</description></item>
/// </list>
/// A sample is "accepted" when it succeeded, names a realm and reaches
/// <see cref="DetectionPolicy.AcceptConfidence"/> (see <see cref="IsAccepted"/>).
/// </summary>
public sealed class DetectionAggregator
{
    private readonly DetectionPolicy _policy;
    private readonly int _windowSize;
    private readonly Queue<MapMatchResult> _window = new();
    private int _sampleCount;

    public DetectionAggregator(DetectionPolicy? policy = null)
    {
        _policy = policy ?? new DetectionPolicy();
        _windowSize = Math.Max(1, _policy.WindowSamples);
    }

    /// <summary>Total number of samples added (including those that have left the window).</summary>
    public int SampleCount => _sampleCount;

    /// <summary>True when the sample would count toward agreement under <paramref name="policy"/>.</summary>
    public static bool IsAccepted(MapMatchResult? sample, DetectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return sample is not null
            && sample.Success
            && !string.IsNullOrWhiteSpace(sample.RealmName)
            && sample.Confidence >= policy.AcceptConfidence;
    }

    public void AddSample(MapMatchResult? sample)
    {
        if (sample is null)
        {
            return;
        }

        _sampleCount++;
        _window.Enqueue(sample);
        while (_window.Count > _windowSize)
        {
            _window.Dequeue();
        }
    }

    /// <summary>True when sampling can stop early with a confident answer.</summary>
    public bool TryGetEarlyResult(out DetectionResult? result)
    {
        result = Evaluate();
        return result is not null && result.Success;
    }

    /// <summary>Final verdict after the detection attempt ends (success or failure).</summary>
    public DetectionResult Finalize()
    {
        return Evaluate() ?? new DetectionResult(false, null, null, 0.0, _sampleCount,
            _sampleCount == 0 ? "No OCR samples were captured." : "No confident map detected — Global preset.");
    }

    private DetectionResult? Evaluate()
    {
        var groups = _window
            .Where(s => IsAccepted(s, _policy))
            .GroupBy(s => s.RealmName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Max(s => s.Confidence))
            .ToList();

        if (groups.Count == 0)
        {
            return null;
        }

        List<MapMatchResult> leader = groups[0];
        int strongestRival = groups.Count > 1 ? groups[1].Count : 0;
        MapMatchResult top = leader.OrderByDescending(s => s.Confidence).First();

        if (leader.Count >= _policy.MinAgreeingSamples && leader.Count > strongestRival)
        {
            // Agreement bonus, capped.
            double confidence = Math.Min(0.99, top.Confidence + 0.02 * (leader.Count - 1));
            string? map = leader.Where(s => s.MapName is not null)
                .OrderByDescending(s => s.Confidence)
                .FirstOrDefault()?.MapName;
            return new DetectionResult(true, map, top.RealmName, Math.Round(confidence, 4), _sampleCount);
        }

        // Single outstanding sample, uncontested inside the window.
        if (groups.Count == 1 && top.Confidence >= _policy.ConfirmConfidence)
        {
            return new DetectionResult(true, top.MapName, top.RealmName, top.Confidence, _sampleCount);
        }

        return null;
    }
}
