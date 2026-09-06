namespace DBDAutoReShade.Core.Models;

/// <summary>How a single OCR sample was matched against the database.</summary>
public enum MatchMethod
{
    None,
    MapName,
    RealmName
}

/// <summary>Result of matching one OCR sample (one frame) against the database.</summary>
public sealed record MapMatchResult(
    bool Success,
    string? MapName,
    string? RealmName,
    double Confidence,
    MatchMethod Method,
    string? Diagnostic = null)
{
    public static MapMatchResult Failure(string diagnostic) =>
        new(false, null, null, 0.0, MatchMethod.None, diagnostic);
}

/// <summary>Aggregated result of a full detection attempt (up to ~10s of samples).</summary>
public sealed record DetectionResult(
    bool Success,
    string? MapName,
    string? RealmName,
    double Confidence,
    int SampleCount,
    string? FailureReason = null,
    bool WasStopped = false);

/// <summary>Tuning knobs for the detection window.</summary>
public sealed record DetectionPolicy(
    int TimeoutSeconds = 10,
    int SampleIntervalMs = 900,
    double AcceptConfidence = 0.78,
    double ConfirmConfidence = 0.90,
    int MinAgreeingSamples = 2);
