using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Pure aggregation of per-frame match samples (no I/O, no timers):
/// repeated agreement on one realm raises confidence; a single very strong
/// sample can confirm immediately. Used by the timed orchestrator in Desktop.
/// </summary>
public sealed class DetectionAggregator
{
    private readonly DetectionPolicy _policy;
    private readonly List<MapMatchResult> _samples = new();

    public DetectionAggregator(DetectionPolicy? policy = null)
    {
        _policy = policy ?? new DetectionPolicy();
    }

    public int SampleCount => _samples.Count;

    public void AddSample(MapMatchResult? sample)
    {
        if (sample is not null)
        {
            _samples.Add(sample);
        }
    }

    /// <summary>True when sampling can stop early with a confident answer.</summary>
    public bool TryGetEarlyResult(out DetectionResult? result)
    {
        result = Evaluate();
        return result is not null && result.Success;
    }

    /// <summary>Final verdict after the detection window closes (success or failure).</summary>
    public DetectionResult Finalize()
    {
        return Evaluate() ?? new DetectionResult(false, null, null, 0.0, _samples.Count,
            _samples.Count == 0 ? "No OCR samples were captured." : "No confident map detected — Global preset.");
    }

    private DetectionResult? Evaluate()
    {
        var agreeing = _samples
            .Where(s => s.Success && !string.IsNullOrWhiteSpace(s.RealmName) && s.Confidence >= _policy.AcceptConfidence)
            .GroupBy(s => s.RealmName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(s => s.Confidence))
            .FirstOrDefault();

        if (agreeing is null)
        {
            // Single outstanding sample confirms immediately.
            var best = _samples
                .Where(s => s.Success && !string.IsNullOrWhiteSpace(s.RealmName))
                .OrderByDescending(s => s.Confidence)
                .FirstOrDefault();
            if (best is not null && best.Confidence >= _policy.ConfirmConfidence)
            {
                return new DetectionResult(true, best.MapName, best.RealmName, best.Confidence, _samples.Count);
            }

            return null;
        }

        var top = agreeing.OrderByDescending(s => s.Confidence).First();
        if (agreeing.Count() >= _policy.MinAgreeingSamples)
        {
            // Agreement bonus, capped.
            double confidence = Math.Min(0.99, top.Confidence + 0.02 * (agreeing.Count() - 1));
            string? map = agreeing.Where(s => s.MapName is not null)
                .OrderByDescending(s => s.Confidence)
                .FirstOrDefault()?.MapName;
            return new DetectionResult(true, map, top.RealmName, Math.Round(confidence, 4), _samples.Count);
        }

        if (top.Confidence >= _policy.ConfirmConfidence)
        {
            return new DetectionResult(true, top.MapName, top.RealmName, top.Confidence, _samples.Count);
        }

        return null;
    }
}
