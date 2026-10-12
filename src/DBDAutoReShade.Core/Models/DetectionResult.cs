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

/// <summary>
/// Aggregated result of one detection attempt: a single-shot window
/// (<see cref="DetectionPolicy.TimeoutSeconds"/>) or a continuous toggle scan
/// that runs until it succeeds or is stopped.
/// </summary>
public sealed record DetectionResult(
    bool Success,
    string? MapName,
    string? RealmName,
    double Confidence,
    int SampleCount,
    string? FailureReason = null,
    bool WasStopped = false)
{
    /// <summary>
    /// Debug Mode only: path of the summary (.txt) of the debug set saved for
    /// this unsuccessful scan. The summary holds the OCR text and match
    /// diagnostic of the most recent failed sample (or, when no sample failed,
    /// the most recent accepted-but-unconfirmed one, labelled as such) and
    /// names the crop images saved next to it. Null when nothing was saved.
    /// </summary>
    public string? DebugCropPath { get; init; }

    /// <summary>Every file of the saved debug set (summary first, then the crops); empty when nothing was saved.</summary>
    public IReadOnlyList<string> DebugCropFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Per-sample feedback raised while a scan runs: the outcome of matching one
/// frame. <paramref name="Index"/> is 1-based. For a paused tick
/// (<see cref="Paused"/>) it is the number of samples evaluated so far.
/// </summary>
public sealed record DetectionSampleInfo(
    int Index,
    bool Success,
    string? MapName,
    string? RealmName,
    double Confidence,
    string? Diagnostic)
{
    /// <summary>
    /// True for a tick where Dead by Daylight was not in front (not the foreground
    /// window, and another window covered its loading-screen text, or it was
    /// minimized): nothing was captured, read or counted, and <see cref="Diagnostic"/> says why.
    /// </summary>
    public bool Paused { get; init; }
}

/// <summary>
/// Tuning knobs for detection. <see cref="TimeoutSeconds"/> applies to
/// single-shot attempts only (continuous scans run until stopped).
/// <see cref="WindowSamples"/> is the sliding window of most recent samples
/// that count toward agreement; older samples expire.
/// </summary>
public sealed record DetectionPolicy(
    int TimeoutSeconds = 10,
    int SampleIntervalMs = 900,
    double AcceptConfidence = 0.78,
    double ConfirmConfidence = 0.90,
    int MinAgreeingSamples = 2,
    int WindowSamples = 8);
